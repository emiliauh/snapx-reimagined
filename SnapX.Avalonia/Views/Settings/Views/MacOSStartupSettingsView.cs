using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapX.Avalonia.Utils;
using SnapX.Core;

namespace SnapX.Avalonia.Views.Settings.Views;

public sealed class MacOSStartupSettingsView : UserControl
{
    private readonly MacOSLoginItemService _service = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _enable = new() { Content = "Enable launch at login" };
    private readonly Button _disable = new() { Content = "Disable" };
    private Window? _window;

    public MacOSStartupSettingsView()
    {
        IsVisible = OperatingSystem.IsMacOS();
        if (!IsVisible) return;
        var systemSettings = new Button { Content = "Open Login Items settings" };
        var refresh = new Button { Content = "Refresh status" };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { _enable, _disable, systemSettings, refresh })
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(button);
        }
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Launch at login", FontWeight = FontWeight.SemiBold },
                _status,
                new TextBlock { Text = MacOSStartupPrompt.RemovalInstructions, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                buttons
            }
        };
        _enable.Click += async (_, _) => await ChangeAsync(true);
        _disable.Click += async (_, _) => await ChangeAsync(false);
        systemSettings.Click += (_, _) => { try { _service.OpenSystemSettings(); } catch (Exception ex) { _status.Text = ex.Message; } };
        refresh.Click += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window != null) _window.Activated += WindowActivated;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_window != null) _window.Activated -= WindowActivated;
            _window = null;
        };
    }

    private void WindowActivated(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        try
        {
            var status = _service.GetStatus();
            _status.Text = MacOSStartupPrompt.StatusText(status);
            _enable.IsEnabled = !SnapXL.Portable && _service.CanManage && status is LoginItemStatus.NotRegistered or LoginItemStatus.NotFound;
            _disable.IsEnabled = _service.CanManage && status is LoginItemStatus.Enabled or LoginItemStatus.RequiresApproval;
        }
        catch (Exception ex) { _status.Text = ex.Message; _enable.IsEnabled = _disable.IsEnabled = false; }
    }

    private async Task ChangeAsync(bool enable)
    {
        try
        {
            // Only an explicit button click reaches registration; never synchronize
            // a saved preference back into macOS after the user disables it there.
            _enable.IsEnabled = _disable.IsEnabled = false;
            SnapXL.Settings.MacOSLoginPromptDismissed = true;
            SettingManager.SaveApplicationConfigAsync();
            if (enable) new LoginItemController(_service).ApplyChoice(true);
            else _service.Unregister();
            if (_service.GetStatus() == LoginItemStatus.RequiresApproval) _service.OpenSystemSettings();
        }
        catch (Exception ex)
        {
            if (_window != null) await MacOSStartupPrompt.ShowMessageAsync(_window, "Launch at login", ex.Message);
        }
        finally { Refresh(); }
    }
}
