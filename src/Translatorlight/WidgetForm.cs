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

    private const int ButtonWidth = 26;
    private const int LockColumn = 2;
    private const int ResizeWidth = 6;

    /// <summary>How far the bar sits from the corner of the working area.</summary>
    private const int ScreenMargin = 8;

    private const int DragThreshold = 4;
    private const double DimmedOpacity = 0.9d;
    private const int DragLeaveGraceMilliseconds = 150;
    private const int ClipboardAttempts = 3;

    private const string IdleHint = "перетащите текст";
    private const string BusyHint = "обрабатывается";

    private readonly AppSettings _settings;
    private readonly DesktopBar _dock;
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _dragLeaveTimer;

    private readonly Font _fieldFont;
    private readonly Font _smallFont;
    private readonly Font _glyphFont;

    private readonly SmoothTable _root;
    private readonly SmoothPanel _fieldHost;
    private readonly TextBox _field;
    private readonly Label _fieldHint;
    private readonly Label _statusLabel;
    private readonly Label _lockButton;
    private readonly Label _closeButton;
    private readonly Panel _resizeStrip;

    private Palette _palette = Theme.Current;
    private CancellationTokenSource? _work;
    private IconState _state = IconState.Idle;
    private string _sourceText = string.Empty;
    private bool _statusIsError;
    private float _lockColumnWidth = ButtonWidth;
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
            PlaceholderText = IdleHint,
        };
        _field.KeyDown += OnFieldKeyDown;
        _field.TextChanged += (_, _) => UpdateHint();
        _field.Enter += (_, _) => UpdateHint();
        _field.Leave += (_, _) => UpdateHint();

        // The hint is a label laid over the empty field rather than only the placeholder text,
        // so it is certain to be seen - including the one shown while a translation is on its way.
        _fieldHint = new Label
        {
            AutoSize = false,
            Font = _fieldFont,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = IdleHint,
        };
        _fieldHint.Click += (_, _) => FocusField();

        _fieldHost = new SmoothPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 6),
            Padding = new Padding(9, 3, 9, 3),
        };
        _fieldHost.Controls.Add(_fieldHint);
        _fieldHost.Controls.Add(_field);
        _fieldHint.BringToFront();
        _fieldHost.Paint += OnFieldHostPaint;
        _fieldHost.Resize += (_, _) => LayoutField();

        _statusLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Font = _smallFont,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(8, 0, 4, 0),
            Text = string.Empty,
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
            ColumnCount = 5,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ButtonWidth));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ButtonWidth));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ResizeWidth));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _root.Controls.Add(_fieldHost, 0, 0);
        _root.Controls.Add(_statusLabel, 1, 0);
        _root.Controls.Add(_lockButton, LockColumn, 0);
        _root.Controls.Add(_closeButton, 3, 0);
        _root.Controls.Add(_resizeStrip, 4, 0);

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

        // Everything that is not the field or a button moves the window when dragged and puts
        // the caret in the field when merely clicked: the frame around the field, the bands
        // above and below it, and the status corner.
        AttachDragOrFocus(this);
        AttachDragOrFocus(_root);
        AttachDragOrFocus(_fieldHost);
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
        _tips.SetToolTip(_field, "Перетащите сюда текст или напечатайте его и нажмите Enter");
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
            ShowError("буфер пуст", "В буфере обмена нет текста.");
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

        // The frame around the field is the handle, so it wears the move cursor while the
        // strip can actually be moved. Over the text itself the caret cursor still wins.
        bool held = _settings.LockedInPlace || _dock.IsDocked;
        _fieldHost.Cursor = held ? Cursors.Default : Cursors.SizeAll;
        _resizeStrip.Cursor = _dock.IsDocked ? Cursors.Default : Cursors.SizeWE;

        // Docked, there is nothing left to lock: the shell holds the strip.
        ColumnStyle lockColumn = _root.ColumnStyles[LockColumn];
        if (lockColumn.Width > 0)
        {
            // Whatever the display scaling made of the column, that is its real width.
            _lockColumnWidth = lockColumn.Width;
        }

        bool showLock = !_dock.IsDocked;
        _lockButton.Visible = showLock;
        lockColumn.Width = showLock ? _lockColumnWidth : 0;

        UpdateOpacity();
        UpdateHint();
    }

    /// <summary>Repaints the widget in the current Windows colours.</summary>
    internal void ApplyTheme()
    {
        _palette = Theme.Current;

        BackColor = _palette.Window;
        _root.BackColor = _palette.Window;

        _fieldHost.BackColor = _palette.Surface;
        _field.BackColor = _palette.Surface;
        _field.ForeColor = _palette.Text;
        _fieldHint.BackColor = _palette.Surface;
        _fieldHint.ForeColor = _palette.Muted;

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
                ClearError();
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
        var bounds = new Rectangle(
            area.Left,
            area.Top + Math.Max(0, (area.Height - height) / 2),
            Math.Max(20, area.Width),
            height);

        _field.Bounds = bounds;
        _fieldHint.Bounds = bounds;
    }

    /// <summary>
    /// The grey word inside the field: what to do while it is empty, and what is happening
    /// while a translation is on its way.
    /// </summary>
    private void UpdateHint()
    {
        bool busy = _state == IconState.Busy;
        string hint = busy ? BusyHint : IdleHint;

        _fieldHint.Text = hint;
        _field.PlaceholderText = hint;

        // While the caret is in an empty field the hint steps aside - except when it is the one
        // saying that the translation is being fetched.
        _fieldHint.Visible = _field.TextLength == 0 && (busy || !_field.Focused);
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
            ShowError("нет текста", "В том, что перетащили, не нашлось текста.");
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
            // Nothing to translate and nothing worth saying about it; the hint already says it.
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
        ClearError();
        SetState(IconState.Busy);
        _field.Clear();
        UpdateHint();
        _tips.SetToolTip(_field, Shorten(text));

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
                ShowError("не перевелось", Capitalise(ex.Message));
            }
        }
        catch (Exception ex)
        {
            // A widget that dies on an odd answer from a web service would be worse than one
            // that says it could not translate.
            if (!token.IsCancellationRequested)
            {
                ShowError("не перевелось", "Не получилось перевести: " + ex.Message);
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
        SetState(IconState.Idle);
        ClearError();
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

        _tips.SetToolTip(_field, details
            + "\nОригинал: " + Shorten(_sourceText)
            + "\nПеревод через " + outcome.Service);

        UpdateHint();
    }

    /// <summary>
    /// The strip has no room for a running commentary, so only a failure is written out - short,
    /// in red, with the whole of it in the tooltip. Everything else is said inside the field.
    /// </summary>
    private void ShowError(string text, string details)
    {
        _statusIsError = true;
        _statusLabel.Text = text;
        _statusLabel.ForeColor = _palette.Danger;
        _tips.SetToolTip(_statusLabel, details);

        SetState(IconState.Error);

        // The text that failed comes back, so a hiccup in the network costs nothing typed.
        if (_field.TextLength == 0 && _sourceText.Length > 0)
        {
            _field.Text = ForField(_sourceText);
            _field.Select(_field.TextLength, 0);
        }

        UpdateHint();
    }

    private void ClearError()
    {
        if (!_statusIsError)
        {
            return;
        }

        _statusIsError = false;
        _statusLabel.Text = string.Empty;
        _tips.SetToolTip(_statusLabel, string.Empty);
    }

    private void SetState(IconState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
        UpdateHint();
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
