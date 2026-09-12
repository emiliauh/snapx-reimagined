using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Messaging;
using FluentAvalonia.UI.Windowing;
using SnapX.Avalonia.Views.Settings.Views;

namespace SnapX.Avalonia.Views.Settings;

public record NotificationMessage(string Title, string Message, NotificationType Type);

public partial class SettingsWindow : FAAppWindow
{
    private bool _initialSizeApplied;

    public SettingsWindow()
    {
        InitializeComponent();
        WeakReferenceMessenger.Default.Register<NotificationMessage>(
            this,
            (r, m) =>
            {
                NotificationManager?.Show(new Notification(m.Title, m.Message, m.Type));
            }
        );
        WeakReferenceMessenger.Default.Register<ChangeWindowTitleRequest>(this, (r, m) =>
        {
            Title = m.Title;
        });
    }

    private void SettingsWindowOpened(object? Sender, EventArgs E)
    {
        if (_initialSizeApplied)
            return;
        _initialSizeApplied = true;

        var owner = App.MyMainWindow;
        var activeScreen = owner is { IsVisible: true }
            ? Screens.ScreenFromWindow(owner)
            : Screens.ScreenFromWindow(this);
        activeScreen ??= Screens.All.FirstOrDefault();

        double scaling = activeScreen?.Scaling ?? 1d;
        if (!double.IsFinite(scaling) || scaling <= 0d)
            scaling = 1d;

        int workingPixelWidth = activeScreen?.WorkingArea.Width ?? 0;
        int workingPixelHeight = activeScreen?.WorkingArea.Height ?? 0;
        if (workingPixelWidth <= 0 || workingPixelHeight <= 0)
        {
            workingPixelWidth = activeScreen?.Bounds.Width ?? 0;
            workingPixelHeight = activeScreen?.Bounds.Height ?? 0;
        }

        double workingWidth = workingPixelWidth > 0 ? workingPixelWidth / scaling : 1280d;
        double workingHeight = workingPixelHeight > 0 ? workingPixelHeight / scaling : 800d;
        if (!double.IsFinite(workingWidth) || workingWidth <= 0d)
            workingWidth = 1280d;
        if (!double.IsFinite(workingHeight) || workingHeight <= 0d)
            workingHeight = 800d;

        // Bounds and WorkingArea are expressed in physical pixels while Avalonia
        // window dimensions use DIPs. Keep the minimum large enough for settings
        // pages that do not yet reflow, even on an unusually small work area.
        double maximumWidth = Math.Max(MinWidth, workingWidth - 32d);
        double maximumHeight = Math.Max(MinHeight, workingHeight - 32d);
        Width = Math.Clamp(workingWidth * 0.68d, MinWidth, maximumWidth);
        Height = Math.Clamp(workingHeight * 0.84d, MinHeight, maximumHeight);
    }

    private WindowNotificationManager NotificationManager;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        NotificationManager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3,
        };
    }
}
