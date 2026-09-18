using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Translatorlight;

/// <summary>
/// The widget itself: a strip exactly one line tall that lives above the taskbar. Text is
/// dropped on it or typed into it, and the translation appears in the same line - selected
/// whole, however far past the edge it runs, and copied to the clipboard.
/// </summary>
internal sealed class WidgetForm : Form
{
    private const int DefaultWidth = 580;
    private const int SmallestWidth = 380;
    private const int LargestWidth = 2400;

    private const int GripWidth = 18;
    private const int StatusWidth = 104;
    private const int ButtonWidth = 26;
    private const int ResizeWidth = 6;

    /// <summary>How far the bar sits from the corner of the working area.</summary>
    private const int ScreenMargin = 8;

    private const int DragThreshold = 4;
    private const double DimmedOpacity = 0.9d;
    private const int DragLeaveGraceMilliseconds = 150;
    private const int ClipboardAttempts = 3;

    private const string IdleStatus = "перетащите текст";

    private readonly AppSettings _settings;
    private readonly DesktopBar _dock;
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _dragLeaveTimer;

    private readonly Font _fieldFont;
    private readonly Font _smallFont;
    private readonly Font _glyphFont;

    private readonly SmoothTable _root;
    private readonly SmoothPanel _grip;
    private readonly SmoothPanel _fieldHost;
    private readonly TextBox _field;
    private readonly Label _statusLabel;
    private readonly Label _lockButton;
    private readonly Label _closeButton;
    private readonly Panel _resizeStrip;

    private Palette _palette = Theme.Current;
    private CancellationTokenSource? _work;
    private IconState _state = IconState.Idle;
    private string _sourceText = string.Empty;
    private bool _statusIsError;
    private bool _dropActive;
    private bool _windowActive;
    private bool _pressed;
    private Point _pressOrigin;
    private bool _resizing;
    private int _resizeOriginX;
    private int _resizeStartWidth;
    private bool _extrasDisposed;

    /// <summary>Raised when the widget wants the tray icon to show a different state.</summary>
    internal event EventHandler<IconState>? StateChanged;

    /// <summary>Raised when the widget itself changed a setting, so the tray menu can catch up.</summary>
    internal event EventHandler? SettingsChanged;

    internal WidgetForm(AppSettings settings)
    {
        _settings = settings;
        _dock = new DesktopBar(this, () => _settings.AlwaysOnTop);

        _fieldFont = new Font("Segoe UI", 10F);
        _smallFont = new Font("Segoe UI", 8.25F);
        _glyphFont = new Font(Glyphs.FamilyName, Glyphs.Available ? 9F : 10F);

        _field = new TextBox
        {
            // Multiline with no wrapping and a height of exactly one line: the bar never grows,
            // and text that does not fit scrolls sideways instead of pushing the window open.
            Multiline = true,
            WordWrap = false,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.None,
            BorderStyle = BorderStyle.None,

            // Keeps the translation visibly selected even while another window has the focus.
            HideSelection = false,
            Font = _fieldFont,
            PlaceholderText = "Перетащите или напечатайте текст — Enter переведёт",
        };
        _field.KeyDown += OnFieldKeyDown;

        _fieldHost = new SmoothPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 6),
            Padding = new Padding(9, 3, 9, 3),
        };
        _fieldHost.Controls.Add(_field);
        _fieldHost.Paint += OnFieldHostPaint;
        _fieldHost.Resize += (_, _) => LayoutField();
        _fieldHost.Click += (_, _) => FocusField();

        _grip = new SmoothPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _grip.Paint += OnGripPaint;

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = _smallFont,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 8, 0),
        };

        _lockButton = CreateGlyphButton(Glyphs.Unlocked, "Закрепить полоску на месте", (_, _) => ToggleLock());
        _closeButton = CreateGlyphButton(Glyphs.Close, "Скрыть виджет (Esc) — он останется в трее", (_, _) => HideWidget());

        _resizeStrip = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Cursor = Cursors.SizeWE,
        };
        _resizeStrip.MouseDown += OnResizeMouseDown;
        _resizeStrip.MouseMove += OnResizeMouseMove;
        _resizeStrip.MouseUp += OnResizeMouseUp;

        _root = new SmoothTable
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, GripWidth));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, StatusWidth));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ButtonWidth));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ButtonWidth));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ResizeWidth));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _root.Controls.Add(_grip, 0, 0);
        _root.Controls.Add(_fieldHost, 1, 0);
        _root.Controls.Add(_statusLabel, 2, 0);
        _root.Controls.Add(_lockButton, 3, 0);
        _root.Controls.Add(_closeButton, 4, 0);
        _root.Controls.Add(_resizeStrip, 5, 0);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        // One pixel of padding all round leaves room for the border drawn in OnPaint.
        Padding = new Padding(1);
        KeyPreview = true;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Text = "Translatorlight";
        ClientSize = new Size(DefaultWidth, 42);

        Controls.Add(_root);

        _field.Enter += (_, _) => _fieldHost.Invalidate();
        _field.Leave += (_, _) => _fieldHost.Invalidate();

        // The grip and the empty part of the bar both move the window when dragged and put the
        // caret in the field when merely clicked.
        AttachDragOrFocus(this);
        AttachDragOrFocus(_grip);
        AttachDragOrFocus(_statusLabel);

        _dragLeaveTimer = new System.Windows.Forms.Timer { Interval = DragLeaveGraceMilliseconds };
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            SetDropActive(false);
        };

        DpiChanged += (_, _) => ApplyBarHeight();

        EnableDropTarget(this);
        ApplyTheme();
        ApplySettings();
        ShowStatus(IdleStatus, error: false, "Перетащите текст на полоску или напечатайте его здесь");
    }

    /// <summary>The widget must never steal the focus from the window the text came from.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;

            // WS_EX_TOOLWINDOW: a widget has no business in Alt+Tab.
            parameters.ExStyle |= 0x00000080;
            return parameters;
        }
    }

    internal void ShowWidget(bool activate)
    {
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        ApplyDockSetting();
        BringToFront();

        if (activate)
        {
            Activate();
            FocusField();
        }
    }

    internal void HideWidget()
    {
        SavePlacement();

        // A hidden bar must not keep holding a strip of screen nobody can see.
        _dock.Release();
        Hide();
    }

    internal void ToggleWidget()
    {
        if (Visible)
        {
            HideWidget();
        }
        else
        {
            ShowWidget(activate: true);
        }
    }

    /// <summary>Parks the bar in the corner of the screen, right above the taskbar.</summary>
    internal void SnapToTaskbar()
    {
        if (_dock.IsDocked)
        {
            // A docked bar already sits where the shell put it.
            return;
        }

        Rectangle work = Screen.FromControl(this).WorkingArea;
        Location = new Point(work.Right - Width - ScreenMargin, work.Bottom - Height - ScreenMargin);
        SavePlacement();
    }

    /// <summary>Translates whatever is in the clipboard right now.</summary>
    internal void TranslateClipboard()
    {
        string text = TryGetClipboardText();
        if (text.Trim().Length == 0)
        {
            ShowStatus("буфер пуст", error: true, "В буфере обмена нет текста.");
            SetState(IconState.Error);
            return;
        }

        Translate(text);
    }

    /// <summary>Starts a translation; the result lands in the bar when it arrives.</summary>
    internal void Translate(string text)
    {
        _ = TranslateAsync(text);
    }

    /// <summary>
    /// Docks the strip to the edge the settings name, or lets it float in its own corner again.
    /// </summary>
    internal void ApplyDockSetting()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        DockEdge edge = _settings.DockEdge switch
        {
            AppSettings.DockTop => DockEdge.Top,
            AppSettings.DockBottom => DockEdge.Bottom,
            _ => DockEdge.None,
        };

        bool wasDocked = _dock.IsDocked;

        if (edge == DockEdge.None)
        {
            _dock.Release();
            NativeMethods.UseRoundedCorners(Handle, rounded: true);
            ApplyBarHeight();

            if (wasDocked)
            {
                RestorePlacement();
            }
        }
        else
        {
            // A strip that spans the screen looks wrong with rounded ends.
            NativeMethods.UseRoundedCorners(Handle, rounded: false);
            ApplyBarHeight();
            _dock.Apply(edge);
        }

        ApplySettings();
    }

    /// <summary>Re-reads the settings that change how the widget looks and behaves.</summary>
    internal void ApplySettings()
    {
        TopMost = _settings.AlwaysOnTop;

        _lockButton.Text = _settings.LockedInPlace ? Glyphs.Locked : Glyphs.Unlocked;
        _tips.SetToolTip(_lockButton, _settings.LockedInPlace
            ? "Полоска закреплена и не перетаскивается. Щелчок открепит её"
            : "Закрепить полоску на месте, чтобы не сдвинуть её случайно");

        bool held = _settings.LockedInPlace || _dock.IsDocked;
        _grip.Cursor = held ? Cursors.Default : Cursors.SizeAll;
        _resizeStrip.Cursor = _dock.IsDocked ? Cursors.Default : Cursors.SizeWE;
        UpdateOpacity();
    }

    /// <summary>Repaints the widget in the current Windows colours.</summary>
    internal void ApplyTheme()
    {
        _palette = Theme.Current;

        BackColor = _palette.Window;
        _root.BackColor = _palette.Window;
        _grip.BackColor = _palette.Window;

        _fieldHost.BackColor = _palette.Surface;
        _field.BackColor = _palette.Surface;
        _field.ForeColor = _palette.Text;

        _statusLabel.BackColor = Color.Transparent;
        _statusLabel.ForeColor = _statusIsError ? _palette.Danger : _palette.Muted;

        foreach (Label button in new[] { _lockButton, _closeButton })
        {
            button.BackColor = Color.Transparent;
            button.ForeColor = _palette.Muted;
        }

        _resizeStrip.BackColor = _palette.Window;

        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDockSetting();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // The strip of screen goes back to the shell before the window it belonged to is gone.
        _dock.Release();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        // WndProc can run while the constructor is still assigning fields.
        _dock?.OnMessage(m);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyBarHeight();

        if (_dock.IsDocked)
        {
            _dock.Reposition();
        }
        else
        {
            RestorePlacement();
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _windowActive = true;
        UpdateOpacity();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _windowActive = false;
        UpdateOpacity();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The cross hides the widget; only the tray menu really closes the program.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideWidget();
            return;
        }

        SavePlacement();
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            // Esc clears the line first, and hides the widget only once it is empty.
            if (_field.TextLength > 0)
            {
                _field.Clear();
                ShowStatus(IdleStatus, error: false);
            }
            else
            {
                HideWidget();
            }

            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.F5 && _sourceText.Length > 0)
        {
            Translate(_sourceText);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using var border = new Pen(_dropActive ? _palette.Accent : _palette.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || _extrasDisposed)
        {
            return;
        }

        _extrasDisposed = true;

        _work?.Cancel();
        _work = null;

        _dragLeaveTimer.Dispose();
        _tips.Dispose();

        _fieldFont.Dispose();
        _smallFont.Dispose();
        _glyphFont.Dispose();
    }

    /// <summary>
    /// The bar is exactly one line of the field font tall, whatever the font and the display
    /// scaling say that is, and cannot be resized vertically.
    /// </summary>
    private void ApplyBarHeight()
    {
        double scale = DeviceDpi / 96d;
        // Form padding, the host's margin and its padding, so the field lands exactly in the
        // middle of the strip: 2 + 12 + 6 around one line of text plus two pixels of slack.
        int height = _field.Font.Height + 22;

        MinimumSize = new Size((int)Math.Round(SmallestWidth * scale), height);
        MaximumSize = _dock.IsDocked
            ? Size.Empty
            : new Size((int)Math.Round(LargestWidth * scale), height);

        if (ClientSize.Height != height)
        {
            ClientSize = new Size(ClientSize.Width, height);
        }

        LayoutField();
    }

    /// <summary>
    /// A single-line text box has a height of its own; this centres it in the strip instead of
    /// letting it sit against the top edge.
    /// </summary>
    private void LayoutField()
    {
        Rectangle area = _fieldHost.DisplayRectangle;
        int height = _field.Font.Height + 2;
        _field.SetBounds(
            area.Left,
            area.Top + Math.Max(0, (area.Height - height) / 2),
            Math.Max(20, area.Width),
            height);
    }

    private Label CreateGlyphButton(string glyph, string tooltip, EventHandler onClick)
    {
        var button = new Label
        {
            Text = glyph,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = _glyphFont,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            Margin = Padding.Empty,
        };

        button.Click += onClick;
        button.MouseEnter += (_, _) => button.BackColor = _palette.AccentSoft;
        button.MouseLeave += (_, _) => button.BackColor = Color.Transparent;
        _tips.SetToolTip(button, tooltip);
        return button;
    }

    /// <summary>
    /// A press that moves hands the window over to Windows to drag; a press that does not move
    /// is an ordinary click, and puts the caret in the field.
    /// </summary>
    private void AttachDragOrFocus(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                _pressOrigin = System.Windows.Forms.Cursor.Position;
            }
        };

        control.MouseMove += (_, _) =>
        {
            if (!_pressed)
            {
                return;
            }

            Point now = System.Windows.Forms.Cursor.Position;
            if (Math.Abs(now.X - _pressOrigin.X) + Math.Abs(now.Y - _pressOrigin.Y) < DragThreshold)
            {
                return;
            }

            if (_settings.LockedInPlace || _dock.IsDocked)
            {
                return;
            }

            _pressed = false;
            NativeMethods.BeginWindowDrag(Handle);
            SavePlacement();
        };

        control.MouseUp += (_, _) =>
        {
            if (!_pressed)
            {
                return;
            }

            _pressed = false;
            FocusField();
        };
    }

    private void EnableDropTarget(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnWidgetDragEnter;
        control.DragOver += OnWidgetDragOver;
        control.DragLeave += OnWidgetDragLeave;
        control.DragDrop += OnWidgetDragDrop;

        foreach (Control child in control.Controls)
        {
            EnableDropTarget(child);
        }
    }

    private void OnWidgetDragEnter(object? sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();

        bool accepted = DroppedText.CanAccept(e.Data);
        e.Effect = accepted ? DragDropEffects.Copy : DragDropEffects.None;

        if (accepted)
        {
            SetDropActive(true);
        }
    }

    private void OnWidgetDragOver(object? sender, DragEventArgs e)
    {
        e.Effect = DroppedText.CanAccept(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnWidgetDragLeave(object? sender, EventArgs e)
    {
        // Moving from one of the widget's controls to another raises a leave immediately, so the
        // highlight is only dropped when no new enter follows within a moment.
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
    }

    private void OnWidgetDragDrop(object? sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        SetDropActive(false);

        string text = DroppedText.Extract(e.Data);
        if (text.Length == 0)
        {
            ShowStatus("нет текста", error: true, "В том, что перетащили, не нашлось текста.");
            SetState(IconState.Error);
            return;
        }

        Translate(text);
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || e.Shift)
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        TranslateField();
    }

    private void TranslateField()
    {
        string text = _field.Text.Trim();
        if (text.Length == 0)
        {
            ShowStatus("нечего переводить", error: true, "Напечатайте текст или перетащите его на полоску.");
            return;
        }

        Translate(text);
    }

    /// <summary>Puts the caret in the field, so the widget can be typed into straight away.</summary>
    private void FocusField()
    {
        if (!_field.Focused)
        {
            _field.Focus();
        }

        _field.Select(_field.TextLength, 0);
    }

    private async Task TranslateAsync(string text)
    {
        // The older request is only told to stop here; it frees its own source when it unwinds,
        // because the HTTP call still holds a registration on that token until then.
        _work?.Cancel();

        using var work = new CancellationTokenSource();
        _work = work;
        CancellationToken token = work.Token;

        _sourceText = text;
        ShowInField(text);
        ShowStatus("перевожу…", error: false, "Перевожу…");
        SetState(IconState.Busy);

        TranslationDirection direction = Translator.Resolve(_settings.Direction, text);

        try
        {
            TranslationOutcome outcome = await Translator.TranslateAsync(text, direction, _settings.Service, token);
            if (!token.IsCancellationRequested)
            {
                ShowResult(outcome);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer request took over; whatever it is doing is already on screen.
        }
        catch (TranslationException ex)
        {
            if (!token.IsCancellationRequested)
            {
                ShowStatus("не перевелось", error: true, Capitalise(ex.Message));
                SetState(IconState.Error);
            }
        }
        catch (Exception ex)
        {
            // A widget that dies on an odd answer from a web service would be worse than one
            // that says it could not translate.
            if (!token.IsCancellationRequested)
            {
                ShowStatus("не перевелось", error: true, "Не получилось перевести: " + ex.Message);
                SetState(IconState.Error);
            }
        }
        finally
        {
            // Only the request that is still the current one may clear the field.
            if (ReferenceEquals(_work, work))
            {
                _work = null;
            }
        }
    }

    private void ShowResult(TranslationOutcome outcome)
    {
        string display = ForField(outcome.Text);
        _field.Text = display;

        // Selected from the far end, so the whole translation goes into Ctrl+C while the strip
        // still shows its beginning rather than its tail.
        NativeMethods.SelectAllShowingStart(_field.Handle, _field.TextLength);

        bool copied = _settings.CopyToClipboard && TrySetClipboard(display);

        string details = copied
            ? "Перевод выделен и скопирован — жмите Ctrl+V"
            : "Перевод выделен — Ctrl+C, чтобы скопировать";

        if (outcome.Truncated)
        {
            details += string.Create(CultureInfo.CurrentCulture, $". Перевёл первые {Translator.MaxCharacters} знаков");
        }

        ShowStatus(copied ? "скопировано" : "выделено", error: false, details);
        _tips.SetToolTip(_field, "Оригинал: " + Shorten(_sourceText) + "\nПеревод через " + outcome.Service);
        SetState(IconState.Idle);
    }

    /// <summary>Puts the text about to be translated into the field, ready to be edited.</summary>
    private void ShowInField(string text)
    {
        string display = ForField(text);
        if (!string.Equals(_field.Text, display, StringComparison.Ordinal))
        {
            _field.Text = display;
        }

        _field.Select(_field.TextLength, 0);
        _tips.SetToolTip(_field, Shorten(text));
    }

    private void ShowStatus(string text, bool error, string? details = null)
    {
        _statusIsError = error;
        _statusLabel.Text = text;
        _statusLabel.ForeColor = error ? _palette.Danger : _palette.Muted;
        _tips.SetToolTip(_statusLabel, details ?? text);
    }

    private void SetState(IconState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void ToggleLock()
    {
        _settings.LockedInPlace = !_settings.LockedInPlace;
        _settings.Save();
        ApplySettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetDropActive(bool active)
    {
        if (_dropActive == active)
        {
            return;
        }

        _dropActive = active;
        UpdateOpacity();
        _fieldHost.Invalidate();
        Invalidate();
    }

    private void UpdateOpacity()
    {
        double target = !_settings.DimWhenInactive || _windowActive || _dropActive ? 1d : DimmedOpacity;
        if (Math.Abs(Opacity - target) > 0.001d)
        {
            Opacity = target;
        }
    }

    private void OnFieldHostPaint(object? sender, PaintEventArgs e)
    {
        if (_dropActive)
        {
            using var highlight = new Pen(_palette.Accent, 2f) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawRectangle(highlight, 1, 1, _fieldHost.Width - 3, _fieldHost.Height - 3);
            return;
        }

        using var border = new Pen(_field.Focused ? _palette.Accent : _palette.Border);
        e.Graphics.DrawRectangle(border, 0, 0, _fieldHost.Width - 1, _fieldHost.Height - 1);
    }

    private void OnGripPaint(object? sender, PaintEventArgs e)
    {
        using var brush = new SolidBrush(_palette.Muted);
        int left = (_grip.Width - 5) / 2;
        int top = (_grip.Height - 13) / 2;

        for (int row = 0; row < 3; row++)
        {
            e.Graphics.FillRectangle(brush, left, top + (row * 5), 2, 2);
            e.Graphics.FillRectangle(brush, left + 4, top + (row * 5), 2, 2);
        }
    }

    private void OnResizeMouseDown(object? sender, MouseEventArgs e)
    {
        // Docked, the width is the shell's business, not the mouse's.
        if (e.Button != MouseButtons.Left || _dock.IsDocked)
        {
            return;
        }

        _resizing = true;
        _resizeOriginX = System.Windows.Forms.Cursor.Position.X;
        _resizeStartWidth = Width;
    }

    private void OnResizeMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_resizing)
        {
            return;
        }

        int wanted = _resizeStartWidth + (System.Windows.Forms.Cursor.Position.X - _resizeOriginX);
        Width = Math.Clamp(wanted, MinimumSize.Width, MaximumSize.Width);
    }

    private void OnResizeMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_resizing)
        {
            return;
        }

        _resizing = false;
        SavePlacement();
    }

    private void RestorePlacement()
    {
        int fallbackWidth = (int)Math.Round(DefaultWidth * DeviceDpi / 96d);
        Width = Math.Clamp(_settings.WindowWidth ?? fallbackWidth, MinimumSize.Width, MaximumSize.Width);

        Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var corner = new Point(work.Right - Width - ScreenMargin, work.Bottom - Height - ScreenMargin);

        if (_settings.WindowX is not int x || _settings.WindowY is not int y)
        {
            Location = corner;
            return;
        }

        // A monitor that has been unplugged must not take the widget off screen with it.
        var wanted = new Rectangle(new Point(x, y), Size);
        bool onScreen = Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(wanted));
        Location = onScreen ? wanted.Location : corner;
    }

    private void SavePlacement()
    {
        if (WindowState != FormWindowState.Normal || _dock.IsDocked)
        {
            return;
        }

        _settings.WindowX = Location.X;
        _settings.WindowY = Location.Y;
        _settings.WindowWidth = Width;
        _settings.Save();
    }

    /// <summary>Windows text boxes want CRLF; anything else shows up as a stray box.</summary>
    private static string ForField(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private static string Shorten(string text)
    {
        string oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length > 300 ? oneLine[..300] + "…" : oneLine;
    }

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    private static bool TrySetClipboard(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        for (int attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                // Another program is holding the clipboard open; it usually lets go at once.
                Thread.Sleep(60);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return false;
    }

    private static string TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (ExternalException)
        {
            return string.Empty;
        }
    }
}
