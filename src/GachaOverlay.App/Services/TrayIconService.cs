using System.Drawing;
using System.Windows.Forms;
using System.Windows;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly Action _toggleHudVisibility;
    private readonly Action _toggleHudLock;
    private readonly Action _openSettings;
    private readonly Action _reconnectDiscord;
    private readonly Action _exitApplication;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _titleItem;
    private readonly ToolStripMenuItem _connectionStatusItem;
    private readonly ToolStripMenuItem _visibilityItem;
    private readonly ToolStripMenuItem _lockItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _connectionSetupItem;
    private readonly ToolStripMenuItem _reconnectItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _applicationIcon;
    private HudSessionState _state = HudSessionState.CreateDefault();
    private RemoteChatHealthState _connectionState = RemoteChatHealthState.Disconnected;
    private bool _canReconnectDiscord;
    private bool _disposed;

    public TrayIconService(
        ILocalizationService localization,
        IAppLogger logger,
        Action toggleHudVisibility,
        Action toggleHudLock,
        Action openSettings,
        Action reconnectDiscord,
        Action exitApplication)
    {
        _localization = localization;
        _logger = logger;
        _toggleHudVisibility = toggleHudVisibility;
        _toggleHudLock = toggleHudLock;
        _openSettings = openSettings;
        _reconnectDiscord = reconnectDiscord;
        _exitApplication = exitApplication;

        _titleItem = new ToolStripMenuItem { Enabled = false };
        _connectionStatusItem = new ToolStripMenuItem { Enabled = false };
        _visibilityItem = new ToolStripMenuItem();
        _lockItem = new ToolStripMenuItem();
        _settingsItem = new ToolStripMenuItem();
        _connectionSetupItem = new ToolStripMenuItem();
        _reconnectItem = new ToolStripMenuItem();
        _exitItem = new ToolStripMenuItem();
        _visibilityItem.Click += OnVisibilityClick;
        _lockItem.Click += OnLockClick;
        _settingsItem.Click += OnSettingsClick;
        _connectionSetupItem.Click += OnSettingsClick;
        _reconnectItem.Click += OnReconnectClick;
        _exitItem.Click += OnExitClick;

        _contextMenu = new ContextMenuStrip { ShowImageMargin = false };
        _contextMenu.Items.Add(_titleItem);
        _contextMenu.Items.Add(_connectionStatusItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_visibilityItem);
        _contextMenu.Items.Add(_lockItem);
        _contextMenu.Items.Add(_settingsItem);
        _contextMenu.Items.Add(_connectionSetupItem);
        _contextMenu.Items.Add(_reconnectItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_exitItem);

        var iconStream = System.Windows.Application.GetResourceStream(new Uri(
            "pack://application:,,,/Assets/Branding/LSOverlay-AppIcon.ico"))?.Stream
            ?? throw new InvalidOperationException("The embedded application icon is unavailable.");
        using (iconStream)
        {
            _applicationIcon = new Icon(iconStream);
        }

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = _applicationIcon,
        };
        _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;

        _localization.LanguageChanged += OnLanguageChanged;
        RefreshText();
        _notifyIcon.Visible = true;
        _logger.Information("TRAY", "Tray icon created.");
    }

    public void UpdateHudState(HudSessionState state)
    {
        _state = state;
        RefreshText();
    }

    public void UpdateRemoteStatus(RemoteChatSnapshot snapshot)
    {
        _connectionState = snapshot.Health;
        _canReconnectDiscord = snapshot.HasProtectedCredential;
        RefreshText();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localization.LanguageChanged -= OnLanguageChanged;
        _visibilityItem.Click -= OnVisibilityClick;
        _lockItem.Click -= OnLockClick;
        _settingsItem.Click -= OnSettingsClick;
        _connectionSetupItem.Click -= OnSettingsClick;
        _reconnectItem.Click -= OnReconnectClick;
        _exitItem.Click -= OnExitClick;
        _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
        _contextMenu.Dispose();
        _logger.Information("TRAY", "Tray icon disposed.");
    }

    private void OnLanguageChanged(object? sender, EventArgs eventArgs) => RefreshText();

    private void OnNotifyIconDoubleClick(object? sender, EventArgs eventArgs) =>
        Execute("HUD visibility", _toggleHudVisibility);

    private void OnVisibilityClick(object? sender, EventArgs eventArgs) =>
        Execute("HUD visibility", _toggleHudVisibility);

    private void OnLockClick(object? sender, EventArgs eventArgs) =>
        Execute("HUD lock", _toggleHudLock);

    private void OnSettingsClick(object? sender, EventArgs eventArgs) =>
        Execute("Settings", _openSettings);

    private void OnExitClick(object? sender, EventArgs eventArgs) =>
        Execute("Exit", _exitApplication);

    private void OnReconnectClick(object? sender, EventArgs eventArgs) =>
        Execute("Discord reconnect", _reconnectDiscord);

    private void Execute(string actionName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _logger.Error("TRAY", $"{actionName} command failed.", exception);
        }
    }

    private void RefreshText()
    {
        var appName = _localization["AppName"];
        _titleItem.Text = appName;
        _connectionStatusItem.Text = string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            _localization["TrayDiscordStatusFormat"],
            _localization[GetConnectionStatusKey()]);
        _visibilityItem.Text = _localization[
            _state.UserHudEnabled ? "TrayHideHud" : "TrayShowHud"];
        _lockItem.Text = _localization[_state.IsLocked ? "TrayUnlockHud" : "TrayLockHud"];
        _settingsItem.Text = _localization["TraySettings"];
        _connectionSetupItem.Text = _localization["TrayDiscordConnectionSetup"];
        _reconnectItem.Text = _localization["TrayDiscordReconnect"];
        _reconnectItem.Enabled = _canReconnectDiscord;
        _exitItem.Text = _localization["TrayExit"];
        _notifyIcon.Text = appName.Length <= 63 ? appName : appName[..63];
    }

    private string GetConnectionStatusKey() => _connectionState switch
    {
        RemoteChatHealthState.Live => "DiscordStatusConnected",
        RemoteChatHealthState.Connecting or
            RemoteChatHealthState.Bootstrapping => "DiscordStatusConnecting",
        RemoteChatHealthState.Authenticating => "DiscordStatusAuthenticating",
        RemoteChatHealthState.Reconnecting => "DiscordStatusReconnecting",
        RemoteChatHealthState.LoginRequired or
            RemoteChatHealthState.LoginInProgress => "DiscordStatusAuthenticationRequired",
        RemoteChatHealthState.ChannelSelectionRequired =>
            "DiscordStatusTargetConfigurationRequired",
        RemoteChatHealthState.Error or
            RemoteChatHealthState.AccessRevoked or
            RemoteChatHealthState.AuthorizationUnavailable => "DiscordStatusFailed",
        _ => "DiscordStatusDisconnected",
    };
}
