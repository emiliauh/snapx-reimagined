using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapX.Core.Utils.Native;
using System.Diagnostics;

namespace SnapX.Avalonia.Views.Settings.Views;

public sealed class MacOSPermissionsView : UserControl
{
    private readonly TextBlock _screenStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _screenGrant = new() { Content = "Allow screen recording" };
    private Window? _window;
    private CancellationTokenSource? _activationRefresh;
    private bool _requesting;
    public event Action<bool>? CompletionChanged;
    public bool IsComplete { get; private set; }

    public MacOSPermissionsView()
    {
        IsVisible = OperatingSystem.IsMacOS();
        if (!IsVisible) return;
        var screenSettings = new Button { Content = "Open screen recording settings" };
        var refresh = new Button { Content = "Check permission again" };
        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "macOS permissions", FontSize = 18, FontWeight = FontWeight.SemiBold },
                Paragraph("SnapX needs screen recording permission for screenshots and videos. SnapX also uses this permission when you record system audio. SnapX does not use your microphone. Only you can change macOS privacy settings."),
                Heading("Screen and System Audio Recording permission is required"),
                Paragraph("Allow SnapX to record your displays and the windows of other applications. SnapX can also record system audio if you enable this option. If macOS tells you to restart SnapX, quit SnapX and open it again."),
                _screenStatus,
                Buttons(_screenGrant, screenSettings),
                Paragraph("Keyboard shortcuts do not need Accessibility or Input Monitoring permission. Launch at login is an optional and separate setting."),
                refresh,
                _error
            }
        };
        _screenGrant.Click += (_, _) => RequestScreenAccess();
        screenSettings.Click += (_, _) => OpenSettings(MacOSPermissions.ScreenRecordingSettingsUrl);
        refresh.Click += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window != null) _window.Activated += Activated;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_window != null) _window.Activated -= Activated;
            _window = null;
            _activationRefresh?.Cancel();
            _activationRefresh?.Dispose();
            _activationRefresh = null;
        };
    }

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Heading(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private static WrapPanel Buttons(params Button[] buttons)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) { button.Margin = new Thickness(0, 0, 8, 4); panel.Children.Add(button); }
        return panel;
    }
    private void Activated(object? sender, EventArgs args)
    {
        _activationRefresh?.Cancel();
        _activationRefresh?.Dispose();
        _activationRefresh = new CancellationTokenSource();
        _ = RefreshAfterActivationAsync(_activationRefresh.Token);
    }

    private async Task RefreshAfterActivationAsync(CancellationToken cancellationToken)
    {
        Refresh();
        try
        {
            // TCC and WindowServer can publish a changed Screen Recording grant a
            // moment after SnapX becomes active. A few bounded reads avoid leaving
            // the checklist stale while still treating native preflight as truth.
            foreach (int delayMs in new[] { 250, 750, 1500 })
            {
                await Task.Delay(delayMs, cancellationToken);
                Refresh();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer activation or detachment owns refresh now.
        }
    }

    private void Refresh()
    {
        try
        {
            bool screen = MacOSPermissions.HasScreenCaptureAccess();
            _screenStatus.Text = screen ? "1. Screen recording permission: Allowed." : "1. Screen recording permission: Not allowed. Select Allow screen recording. Then allow SnapX in System Settings. If necessary, quit SnapX and open it again.";
            IsComplete = screen;
            CompletionChanged?.Invoke(IsComplete);
            _screenGrant.IsEnabled = !_requesting && !screen;
        }
        catch (Exception ex)
        {
            IsComplete = false;
            CompletionChanged?.Invoke(false);
            _screenStatus.Text = "1. SnapX could not check the screen recording permission.";
            _screenGrant.IsEnabled = false;
            _error.Text = ex.Message;
        }
    }
    private void RequestScreenAccess()
    {
        if (_requesting) return;
        _requesting = true;
        _error.Text = "";
        Refresh();
        try
        {
            MacOSPermissions.RequestScreenCaptureAccess();
            if (!MacOSPermissions.HasScreenCaptureAccess()) OpenSettings(MacOSPermissions.ScreenRecordingSettingsUrl);
        }
        catch (Exception ex) { _error.Text = ex.Message; }
        finally { _requesting = false; Refresh(); }
    }
    private void OpenSettings(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _error.Text = "SnapX could not open System Settings. In System Settings, go to Privacy and Security. " + ex.Message; }
    }
}
