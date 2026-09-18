using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Translatorlight;

/// <summary>
/// The widget itself: a small window that hangs above the other windows, takes text dragged
/// onto it from anywhere, and leaves the translation selected and copied, ready to be pasted.
/// </summary>
internal sealed class WidgetForm : Form
{
    private const int DefaultWidth = 372;
    private const int DefaultHeight = 214;
    private const int SmallestWidth = 280;
    private const int SmallestHeight = 168;
    private const int LargestWidth = 1200;
    private const int LargestHeight = 900;
    private const int ScreenMargin = 24;
    private const int GripSize = 14;
    private const double DimmedOpacity = 0.9d;
    private const int DragLeaveGraceMilliseconds = 150;
    private const int ClipboardAttempts = 3;

    private readonly AppSettings _settings;
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _dragLeaveTimer;

    private readonly Font _titleFont;
    private readonly Font _glyphFont;
    private readonly Font _sourceFont;
    private readonly Font _resultFont;
    private readonly Font _statusFont;

    private readonly Panel _resizeGrip;
    private readonly TableLayoutPanel _root;
    private readonly TableLayoutPanel _header;
    private readonly Label _directionLabel;
    private readonly Panel _headerDrag;
    private readonly FlowLayoutPanel _headerButtons;
    private readonly Label _swapButton;
    private readonly Label _pinButton;
    private readonly Label _closeButton;
    private readonly TableLayoutPanel _body;
    private readonly Label _sourceLabel;
    private readonly Panel _resultHost;
    private readonly TextBox _resultBox;
    private readonly Label _hintLabel;
    private readonly TableLayoutPanel _footer;
    private readonly Label _statusLabel;
    private readonly Label _dragOutLabel;

    private Palette _palette = Theme.Current;
    private CancellationTokenSource? _work;
    private IconState _state = IconState.Idle;
    private string _sourceText = string.Empty;
    private bool _statusIsError;
    private bool _dropActive;
    private bool _windowActive;
    private bool _draggingOut;
    private bool _resizing;
    private Point _resizeOrigin;
    private Size _resizeStartSize;
    private bool _extrasDisposed;

    /// <summary>Raised when the widget wants the tray icon to show a different state.</summary>
    internal event EventHandler<IconState>? StateChanged;

    /// <summary>Raised when the widget itself changed a setting, so the tray menu can catch up.</summary>
    internal event EventHandler? SettingsChanged;

    internal WidgetForm(AppSettings settings)
    {
        _settings = settings;

        _titleFont = new Font("Segoe UI", 9F);
        _glyphFont = new Font(Glyphs.FamilyName, Glyphs.Available ? 9F : 10F);
        _sourceFont = new Font("Segoe UI", 8.25F);
        _resultFont = new Font("Segoe UI", 10.5F);
        _statusFont = new Font("Segoe UI", 8.25F);

        // Built before anything can lay out, so the grip is never touched while still null.
        _resizeGrip = new Panel
        {
            Size = new Size(GripSize, GripSize),
            Cursor = Cursors.SizeNWSE,
            BackColor = Color.Transparent,
        };

        _directionLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = _titleFont,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(11, 0, 6, 0),
            Margin = Padding.Empty,
        };

        _headerDrag = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };

        _swapButton = CreateGlyphButton(Glyphs.Swap, "Направление: авто, EN → RU, RU → EN", (_, _) => CycleDirection());
        _pinButton = CreateGlyphButton(Glyphs.Pin, "Поверх всех окон", (_, _) => TogglePin());
        _closeButton = CreateGlyphButton(Glyphs.Close, "Скрыть виджет (Esc) — он останется в трее", (_, _) => HideWidget());

        _headerButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _headerButtons.Controls.Add(_swapButton);
        _headerButtons.Controls.Add(_pinButton);
        _headerButtons.Controls.Add(_closeButton);

        _header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _header.Controls.Add(_directionLabel, 0, 0);
        _header.Controls.Add(_headerDrag, 1, 0);
        _header.Controls.Add(_headerButtons, 2, 0);

        _sourceLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 17,
            Font = _sourceFont,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 0, 0, 4),
        };

        _resultBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.Vertical,
            Font = _resultFont,

            // Keeps the translation visibly selected even while another window has the focus.
            HideSelection = false,
        };

        _hintLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = _titleFont,
            Text = "Перетащите сюда выделенный текст\nи получите русский перевод",
        };

        _resultHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(8, 6, 6, 6),
        };
        _resultHost.Controls.Add(_hintLabel);
        _resultHost.Controls.Add(_resultBox);
        _hintLabel.BringToFront();
        _resultHost.Paint += OnResultHostPaint;

        _body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(10, 6, 10, 4),
        };
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _body.Controls.Add(_sourceLabel, 0, 0);
        _body.Controls.Add(_resultHost, 0, 1);

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = _statusFont,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Padding = new Padding(11, 0, 0, 0),
            Margin = Padding.Empty,
        };

        _dragOutLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Font = _statusFont,
            Text = "⇗ перетащить",
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 20, 0),
        };
        _dragOutLabel.MouseDown += OnDragOutMouseDown;
        _tips.SetToolTip(_dragOutLabel, "Перетащите перевод мышью прямо в нужное окно");

        _footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _footer.Controls.Add(_statusLabel, 0, 0);
        _footer.Controls.Add(_dragOutLabel, 1, 0);

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
        _root.Controls.Add(_header, 0, 0);
        _root.Controls.Add(_body, 0, 1);
        _root.Controls.Add(_footer, 0, 2);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(SmallestWidth, SmallestHeight);
        ClientSize = new Size(DefaultWidth, DefaultHeight);

        // One pixel of padding all round leaves room for the border drawn in OnPaint.
        Padding = new Padding(1);
        KeyPreview = true;
        DoubleBuffered = true;
        Text = "Translatorlight";

        Controls.Add(_root);
        Controls.Add(_resizeGrip);
        _resizeGrip.BringToFront();
        _resizeGrip.Paint += OnGripPaint;
        _resizeGrip.MouseDown += OnGripMouseDown;
        _resizeGrip.MouseMove += OnGripMouseMove;
        _resizeGrip.MouseUp += OnGripMouseUp;

        _directionLabel.MouseDown += OnHeaderMouseDown;
        _headerDrag.MouseDown += OnHeaderMouseDown;

        _dragLeaveTimer = new System.Windows.Forms.Timer { Interval = DragLeaveGraceMilliseconds };
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            SetDropActive(false);
        };

        Resize += (_, _) => PositionGrip();
        ResizeEnd += (_, _) => SavePlacement();

        EnableDropTarget(this);
        ApplyTheme();
        ApplySettings();
        ShowStatus("Перетащите текст — переведу на русский", error: false);
        PositionGrip();
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

        BringToFront();

        if (activate)
        {
            Activate();
        }
    }

    internal void HideWidget()
    {
        SavePlacement();
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

    /// <summary>Translates whatever is in the clipboard right now.</summary>
    internal void TranslateClipboard()
    {
        string text = TryGetClipboardText();
        if (text.Trim().Length == 0)
        {
            ShowStatus("В буфере обмена нет текста.", error: true);
            SetState(IconState.Error);
            return;
        }

        Translate(text);
    }

    /// <summary>Starts a translation; the result lands in the widget when it arrives.</summary>
    internal void Translate(string text)
    {
        _ = TranslateAsync(text);
    }

    /// <summary>Re-reads the settings that change how the widget looks and behaves.</summary>
    internal void ApplySettings()
    {
        TopMost = _settings.AlwaysOnTop;
        _pinButton.Text = _settings.AlwaysOnTop ? Glyphs.Pin : Glyphs.Unpin;
        _tips.SetToolTip(_pinButton, _settings.AlwaysOnTop ? "Поверх всех окон: включено" : "Поверх всех окон: выключено");
        _sourceLabel.Visible = _settings.ShowSourceText && _sourceLabel.Text.Length > 0;
        UpdateDirectionLabel(null);
        UpdateOpacity();
    }

    /// <summary>Repaints the widget in the current Windows colours.</summary>
    internal void ApplyTheme()
    {
        _palette = Theme.Current;

        BackColor = _palette.Window;
        _root.BackColor = _palette.Window;

        _header.BackColor = _palette.Header;
        _headerDrag.BackColor = Color.Transparent;
        _headerButtons.BackColor = Color.Transparent;
        _directionLabel.BackColor = Color.Transparent;
        _directionLabel.ForeColor = _palette.Muted;

        foreach (Label button in new[] { _swapButton, _pinButton, _closeButton })
        {
            button.BackColor = Color.Transparent;
            button.ForeColor = _palette.Muted;
        }

        _body.BackColor = _palette.Window;
        _sourceLabel.BackColor = Color.Transparent;
        _sourceLabel.ForeColor = _palette.Muted;

        _resultHost.BackColor = _palette.Surface;
        _resultBox.BackColor = _palette.Surface;
        _resultBox.ForeColor = _palette.Text;
        _hintLabel.BackColor = _palette.Surface;
        _hintLabel.ForeColor = _palette.Muted;

        _footer.BackColor = _palette.Window;
        _statusLabel.BackColor = Color.Transparent;
        _statusLabel.ForeColor = _statusIsError ? _palette.Danger : _palette.Muted;
        _dragOutLabel.BackColor = Color.Transparent;
        _dragOutLabel.ForeColor = _palette.Muted;

        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseRoundedCorners(Handle);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RestorePlacement();
        PositionGrip();
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
            HideWidget();
            e.Handled = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.V)
        {
            TranslateClipboard();
            e.Handled = true;
            e.SuppressKeyPress = true;
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

        _titleFont.Dispose();
        _glyphFont.Dispose();
        _sourceFont.Dispose();
        _resultFont.Dispose();
        _statusFont.Dispose();
    }

    private Label CreateGlyphButton(string glyph, string tooltip, EventHandler onClick)
    {
        var button = new Label
        {
            Text = glyph,
            AutoSize = false,
            Size = new Size(30, 30),
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

        bool accepted = !_draggingOut && DroppedText.CanAccept(e.Data);
        e.Effect = accepted ? DragDropEffects.Copy : DragDropEffects.None;

        if (accepted)
        {
            SetDropActive(true);
        }
    }

    private void OnWidgetDragOver(object? sender, DragEventArgs e)
    {
        e.Effect = !_draggingOut && DroppedText.CanAccept(e.Data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
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

        if (_draggingOut)
        {
            return;
        }

        string text = DroppedText.Extract(e.Data);
        if (text.Length == 0)
        {
            ShowStatus("В том, что перетащили, не нашлось текста.", error: true);
            SetState(IconState.Error);
            return;
        }

        Translate(text);
    }

    private void OnDragOutMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _resultBox.TextLength == 0)
        {
            return;
        }

        _draggingOut = true;
        try
        {
            _dragOutLabel.DoDragDrop(_resultBox.Text, DragDropEffects.Copy);
        }
        finally
        {
            _draggingOut = false;
        }
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
        ShowSource(text);
        _hintLabel.Visible = false;
        ShowStatus("Перевожу…", error: false);
        SetState(IconState.Busy);

        TranslationDirection direction = Translator.Resolve(_settings.Direction, text);
        UpdateDirectionLabel(direction);

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
            // A newer drop took over; whatever it is doing is already on screen.
        }
        catch (TranslationException ex)
        {
            if (!token.IsCancellationRequested)
            {
                ShowStatus(Capitalise(ex.Message), error: true);
                SetState(IconState.Error);
            }
        }
        catch (Exception ex)
        {
            // A widget that dies on an odd answer from a web service would be worse than one
            // that says it could not translate.
            if (!token.IsCancellationRequested)
            {
                ShowStatus("Не получилось перевести: " + ex.Message, error: true);
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
        _hintLabel.Visible = false;
        _resultBox.Text = outcome.Text;

        // The whole translation is left selected, so it can be dragged out or copied at once.
        _resultBox.Select(0, _resultBox.TextLength);

        bool copied = _settings.CopyToClipboard && TrySetClipboard(outcome.Text);

        var status = new StringBuilder(copied
            ? "Перевод выделен и скопирован — жмите Ctrl+V"
            : "Перевод выделен — Ctrl+C, чтобы скопировать");

        if (outcome.Truncated)
        {
            status.Append(CultureInfo.CurrentCulture, $" · перевёл первые {Translator.MaxCharacters} знаков");
        }

        ShowStatus(status.ToString(), error: false);
        UpdateDirectionLabel(outcome.Direction);
        _tips.SetToolTip(_resultBox, "Перевод через " + outcome.Service);
        SetState(IconState.Idle);
    }

    private void ShowSource(string text)
    {
        string oneLine = Regex.Replace(text, @"\s+", " ").Trim();
        _sourceLabel.Text = oneLine;
        _sourceLabel.Visible = _settings.ShowSourceText && oneLine.Length > 0;
        _tips.SetToolTip(_sourceLabel, oneLine.Length > 200 ? oneLine[..200] + "…" : oneLine);
    }

    private void ShowStatus(string text, bool error)
    {
        _statusIsError = error;
        _statusLabel.Text = text;
        _statusLabel.ForeColor = error ? _palette.Danger : _palette.Muted;
        _tips.SetToolTip(_statusLabel, text);
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

    private void CycleDirection()
    {
        _settings.Direction = _settings.Direction switch
        {
            AppSettings.DirectionAuto => AppSettings.DirectionEnglishToRussian,
            AppSettings.DirectionEnglishToRussian => AppSettings.DirectionRussianToEnglish,
            _ => AppSettings.DirectionAuto,
        };

        _settings.Save();
        UpdateDirectionLabel(null);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TogglePin()
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        _settings.Save();
        ApplySettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateDirectionLabel(TranslationDirection? resolved)
    {
        _directionLabel.Text = _settings.Direction switch
        {
            AppSettings.DirectionEnglishToRussian => Translator.Describe(TranslationDirection.EnglishToRussian),
            AppSettings.DirectionRussianToEnglish => Translator.Describe(TranslationDirection.RussianToEnglish),
            _ => resolved is null ? "Авто EN ⇄ RU" : "Авто · " + Translator.Describe(resolved.Value),
        };
    }

    private void SetDropActive(bool active)
    {
        if (_dropActive == active)
        {
            return;
        }

        _dropActive = active;
        UpdateOpacity();
        _resultHost.Invalidate();
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

    private void OnHeaderMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            NativeMethods.BeginWindowDrag(Handle);
        }
    }

    private void OnResultHostPaint(object? sender, PaintEventArgs e)
    {
        if (_dropActive)
        {
            using var highlight = new Pen(_palette.Accent, 2f) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawRectangle(highlight, 1, 1, _resultHost.Width - 3, _resultHost.Height - 3);
            return;
        }

        using var border = new Pen(_palette.Border);
        e.Graphics.DrawRectangle(border, 0, 0, _resultHost.Width - 1, _resultHost.Height - 1);
    }

    private void OnGripPaint(object? sender, PaintEventArgs e)
    {
        using var brush = new SolidBrush(_palette.Muted);
        for (int line = 0; line < 3; line++)
        {
            int offset = line * 4;
            e.Graphics.FillRectangle(brush, GripSize - 3 - offset, GripSize - 3, 2, 2);
            e.Graphics.FillRectangle(brush, GripSize - 3, GripSize - 3 - offset, 2, 2);
        }
    }

    private void OnGripMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _resizing = true;
        _resizeOrigin = System.Windows.Forms.Cursor.Position;
        _resizeStartSize = Size;
    }

    private void OnGripMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_resizing)
        {
            return;
        }

        Point now = System.Windows.Forms.Cursor.Position;
        Size = new Size(
            Math.Clamp(_resizeStartSize.Width + (now.X - _resizeOrigin.X), SmallestWidth, LargestWidth),
            Math.Clamp(_resizeStartSize.Height + (now.Y - _resizeOrigin.Y), SmallestHeight, LargestHeight));
    }

    private void OnGripMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_resizing)
        {
            return;
        }

        _resizing = false;
        SavePlacement();
    }

    private void PositionGrip()
    {
        _resizeGrip.Location = new Point(
            Math.Max(0, ClientSize.Width - _resizeGrip.Width - 2),
            Math.Max(0, ClientSize.Height - _resizeGrip.Height - 2));
    }

    private void RestorePlacement()
    {
        if (_settings.WindowWidth is int width && _settings.WindowHeight is int height)
        {
            Size = new Size(
                Math.Clamp(width, SmallestWidth, LargestWidth),
                Math.Clamp(height, SmallestHeight, LargestHeight));
        }

        Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var fallback = new Point(work.Right - Width - ScreenMargin, work.Bottom - Height - ScreenMargin);

        if (_settings.WindowX is not int x || _settings.WindowY is not int y)
        {
            Location = fallback;
            return;
        }

        // A monitor that has been unplugged must not take the widget off screen with it.
        var wanted = new Rectangle(new Point(x, y), Size);
        bool onScreen = Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(wanted));
        Location = onScreen ? wanted.Location : fallback;
    }

    private void SavePlacement()
    {
        if (WindowState != FormWindowState.Normal)
        {
            return;
        }

        _settings.WindowX = Location.X;
        _settings.WindowY = Location.Y;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.Save();
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
