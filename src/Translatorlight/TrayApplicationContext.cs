using Microsoft.Win32;

namespace Translatorlight;

/// <summary>
/// Owns the notification-area icon and the menu behind it, and keeps the widget window in step
/// with the settings.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int MaxTooltipLength = 63;

    private readonly AppSettings _settings;
    private readonly WidgetForm _widget;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;

    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _topMostItem;
    private readonly ToolStripMenuItem _copyItem;
    private readonly ToolStripMenuItem _directionItem;
    private readonly ToolStripMenuItem _serviceItem;
    private readonly ToolStripMenuItem _dimItem;
    private readonly ToolStripMenuItem _startHiddenItem;
    private readonly ToolStripMenuItem _autostartItem;

    private Icon? _currentIcon;
    private IntPtr _currentIconHandle;
    private Icon? _retiredIcon;
    private IntPtr _retiredIconHandle;
    private string _iconSignature = string.Empty;
    private IconState _state = IconState.Idle;
    private bool _lightTaskbar = true;
    private bool _disposed;

    internal TrayApplicationContext()
    {
        _settings = AppSettings.Load();

        _widget = new WidgetForm(_settings);
        _widget.StateChanged += (_, state) => SetState(state);
        _widget.SettingsChanged += (_, _) => RefreshMenuState();

        _showItem = new ToolStripMenuItem("Показать виджет", null, (_, _) => _widget.ToggleWidget())
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };

        _topMostItem = new ToolStripMenuItem("Поверх всех окон", null, (_, _) => ToggleTopMost());
        _copyItem = new ToolStripMenuItem("Сразу копировать перевод", null, (_, _) => ToggleCopy());
        _dimItem = new ToolStripMenuItem("Приглушать, пока не активен", null, (_, _) => ToggleDim());
        _startHiddenItem = new ToolStripMenuItem("Запускаться свёрнутым", null, (_, _) => ToggleStartHidden());
        _autostartItem = new ToolStripMenuItem("Запускать вместе с Windows", null, (_, _) => ToggleAutostart());

        _directionItem = new ToolStripMenuItem("Направление");
        _serviceItem = new ToolStripMenuItem("Сервис перевода");
        BuildDirectionMenu();
        BuildServiceMenu();

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _showItem,
            new ToolStripMenuItem("Перевести из буфера", null, (_, _) => TranslateClipboard()),
            new ToolStripMenuItem("Прижать к панели задач", null, (_, _) => SnapWidget()),
            new ToolStripSeparator(),
            _directionItem,
            _serviceItem,
            new ToolStripSeparator(),
            _topMostItem,
            _copyItem,
            _dimItem,
            _startHiddenItem,
            _autostartItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("О программе", null, (_, _) => ShowAbout()),
            new ToolStripMenuItem("Выход", null, (_, _) => ExitApplication()),
        });
        _menu.Opening += (_, _) => RefreshMenuState();

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = "Translatorlight",
            Visible = true,
        };
        _notifyIcon.MouseClick += OnTrayIconClicked;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _lightTaskbar = Theme.IsTaskbarLight();
        UpdateIcon();
        UpdateTooltip();

        if (!_settings.StartHidden)
        {
            // Shown without taking the focus away from whatever the user is doing.
            _widget.ShowWidget(activate: false);
        }
    }

    private void OnTrayIconClicked(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _widget.ToggleWidget();
        }
    }

    private void TranslateClipboard()
    {
        _widget.ShowWidget(activate: false);
        _widget.TranslateClipboard();
    }

    private void SnapWidget()
    {
        _widget.ShowWidget(activate: false);
        _widget.SnapToTaskbar();
    }

    private void BuildDirectionMenu()
    {
        _directionItem.DropDownItems.Clear();
        _directionItem.DropDownItems.AddRange(new ToolStripItem[]
        {
            CreateDirectionItem("Авто — по тексту", AppSettings.DirectionAuto),
            CreateDirectionItem("Всегда EN → RU", AppSettings.DirectionEnglishToRussian),
            CreateDirectionItem("Всегда RU → EN", AppSettings.DirectionRussianToEnglish),
        });
    }

    private ToolStripMenuItem CreateDirectionItem(string text, string value) =>
        new(text, null, (_, _) => SetDirection(value)) { Tag = value };

    private void BuildServiceMenu()
    {
        _serviceItem.DropDownItems.Clear();
        _serviceItem.DropDownItems.Add(CreateServiceItem("Любой — по очереди", AppSettings.ServiceAuto));

        foreach (string name in Translator.ServiceNames)
        {
            _serviceItem.DropDownItems.Add(CreateServiceItem(name + " — первым", name));
        }
    }

    private ToolStripMenuItem CreateServiceItem(string text, string value) =>
        new(text, null, (_, _) => SetService(value)) { Tag = value };

    private void SetDirection(string value)
    {
        _settings.Direction = value;
        _settings.Save();
        _widget.ApplySettings();
        RefreshMenuState();
    }

    private void SetService(string value)
    {
        _settings.Service = value;
        _settings.Save();
        RefreshMenuState();
    }

    private void ToggleTopMost()
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        _settings.Save();
        _widget.ApplySettings();
    }

    private void ToggleCopy()
    {
        _settings.CopyToClipboard = !_settings.CopyToClipboard;
        _settings.Save();
    }

    private void ToggleDim()
    {
        _settings.DimWhenInactive = !_settings.DimWhenInactive;
        _settings.Save();
        _widget.ApplySettings();
    }

    private void ToggleStartHidden()
    {
        _settings.StartHidden = !_settings.StartHidden;
        _settings.Save();
    }

    private void ToggleAutostart()
    {
        bool wanted = !Autostart.IsEnabled();
        if (!Autostart.SetEnabled(wanted))
        {
            _notifyIcon.ShowBalloonTip(
                6000,
                "Translatorlight",
                "Windows не дала записать автозапуск. Попробуйте ещё раз или добавьте программу в автозагрузку вручную.",
                ToolTipIcon.Warning);
        }

        RefreshMenuState();
    }

    private void RefreshMenuState()
    {
        _showItem.Text = _widget.Visible ? "Скрыть виджет" : "Показать виджет";
        _topMostItem.Checked = _settings.AlwaysOnTop;
        _copyItem.Checked = _settings.CopyToClipboard;
        _dimItem.Checked = _settings.DimWhenInactive;
        _startHiddenItem.Checked = _settings.StartHidden;
        _autostartItem.Checked = Autostart.IsEnabled();

        CheckByTag(_directionItem, _settings.Direction);
        CheckByTag(_serviceItem, _settings.Service);
    }

    private static void CheckByTag(ToolStripMenuItem parent, string value)
    {
        foreach (ToolStripItem item in parent.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem && menuItem.Tag is string tag)
            {
                menuItem.Checked = string.Equals(tag, value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private void SetState(IconState state)
    {
        _state = state;
        UpdateIcon();
        UpdateTooltip();
    }

    private void UpdateIcon()
    {
        int size = TrayIconSize();

        string signature = $"{size}|{_state}|{_lightTaskbar}";
        if (signature == _iconSignature && _currentIcon is not null)
        {
            return;
        }

        _iconSignature = signature;

        using Bitmap bitmap = TrayIconRenderer.Render(_state, _lightTaskbar, size);

        IntPtr handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);

        ReleaseRetiredIcon();
        _retiredIcon = _currentIcon;
        _retiredIconHandle = _currentIconHandle;

        _currentIcon = icon;
        _currentIconHandle = handle;
        _notifyIcon.Icon = icon;
    }

    /// <summary>
    /// Frees the icon that was replaced one update ago. Icon.FromHandle does not take ownership,
    /// so the HICON has to be destroyed by hand or the process leaks GDI handles.
    /// </summary>
    private void ReleaseRetiredIcon()
    {
        _retiredIcon?.Dispose();
        _retiredIcon = null;

        if (_retiredIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_retiredIconHandle);
            _retiredIconHandle = IntPtr.Zero;
        }
    }

    private void UpdateTooltip()
    {
        string text = _state switch
        {
            IconState.Busy => "Translatorlight · перевожу…",
            IconState.Error => "Translatorlight · перевод не удался",
            _ => "Translatorlight · перетащите текст на виджет",
        };

        if (text.Length > MaxTooltipLength)
        {
            text = text[..MaxTooltipLength];
        }

        if (_notifyIcon.Text != text)
        {
            _notifyIcon.Text = text;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle
            or UserPreferenceCategory.Color or UserPreferenceCategory.Window)
        {
            _lightTaskbar = Theme.IsTaskbarLight();
            UpdateIcon();
            _widget.ApplyTheme();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // A monitor may have gone away, taking the widget's corner with it.
        _iconSignature = string.Empty;
        UpdateIcon();
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            "Translatorlight — виджет-переводчик для Windows 11.\n\n"
            + "Перетащите выделенный текст на окошко виджета — перевод появится там же, "
            + "уже выделенный и скопированный в буфер обмена.\n\n"
            + "Перевод берётся у бесплатных сервисов: "
            + string.Join(", ", Translator.ServiceNames)
            + ". Ключи и регистрация не нужны, но интернет нужен.\n\n"
            + "Настройки хранятся в " + AppSettings.FilePath,
            "О программе",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static int TrayIconSize()
    {
        int size = SystemInformation.SmallIconSize.Width;
        return size <= 0 ? 16 : Math.Clamp(size, 16, 64);
    }

    private void ExitApplication()
    {
        _settings.Save();

        // Remove the icon immediately rather than leaving a ghost until the tray is hovered.
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _widget.Dispose();

            ReleaseRetiredIcon();

            _currentIcon?.Dispose();
            _currentIcon = null;
            if (_currentIconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_currentIconHandle);
                _currentIconHandle = IntPtr.Zero;
            }
        }

        base.Dispose(disposing);
    }
}
