using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace SnapX.Avalonia.Views;

/// <summary>
/// An in-window, auto-dismissing capture preview for platforms whose Avalonia
/// overlay is safe to use. Native Wayland deliberately uses only the desktop
/// notification path because its OverlayLayer is backed by a transient EGL
/// surface on the affected renderer.
/// </summary>
public sealed class ToastNotificationWindow
{
    private static ToastNotificationWindow? _current;

    private readonly Window _owner;
    private readonly OverlayLayer _overlay;
    private readonly Border _border;
    private readonly DispatcherTimer _dismissTimer;
    private readonly Action? _onClick;
    private readonly Bitmap? _ownedThumbnail;
    private bool _isClosed;

    public static void ShowToast(
        Bitmap? thumbnail,
        string title,
        string message,
        Action? onClick,
        bool disposeThumbnail = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _current?.Close();

            // Avalonia's native Wayland backend hosts OverlayLayer content on
            // a transient EGL surface.  On the affected NVIDIA/Hyprland path
            // that surface can abort in eglMakeCurrent while a capture is
            // restoring the main window.  Capture callers already send the
            // equivalent freedesktop desktop notification, so do not create
            // an Avalonia overlay at all on native Wayland.
            if (OperatingSystem.IsLinux() && SnapX.Core.Utils.Native.LinuxAPI.IsWayland())
            {
                if (disposeThumbnail) thumbnail?.Dispose();
                return;
            }

            // A popup must be attached to a real visible top level. Capture
            // previews are raised from the main UI, so this deliberately
            // avoids manufacturing a separate Wayland toplevel when the main
            // window is unavailable.
            Window? owner = App.MyMainWindow;
            if (owner is not { IsVisible: true })
            {
                if (disposeThumbnail) thumbnail?.Dispose();
                return;
            }

            OverlayLayer? overlay = OverlayLayer.GetOverlayLayer(owner);
            if (overlay is null)
            {
                if (disposeThumbnail) thumbnail?.Dispose();
                return;
            }

            var toast = new ToastNotificationWindow(owner, overlay, thumbnail, title, message, onClick, disposeThumbnail);
            _current = toast;
            toast.Show();
        });
    }

    private ToastNotificationWindow(
        Window owner,
        OverlayLayer overlay,
        Bitmap? thumbnail,
        string title,
        string message,
        Action? onClick,
        bool disposeThumbnail)
    {
        _owner = owner;
        _overlay = overlay;
        _onClick = onClick;
        _ownedThumbnail = disposeThumbnail ? thumbnail : null;

        var closeButton = new Button
        {
            Content = "×",
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        closeButton.Click += (_, _) => Close();

        var textPanel = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 4,
            MaxWidth = 200,
            VerticalAlignment = VerticalAlignment.Center
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.White
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.8,
            FontSize = 12,
            Foreground = Brushes.White
        });

        var contentGrid = new Grid
        {
            Width = 320,
            Height = 96,
            ColumnDefinitions = new ColumnDefinitions("80,*")
        };
        if (thumbnail is not null)
        {
            var image = new Image
            {
                Source = thumbnail,
                Width = 80,
                Height = 96,
                Stretch = Stretch.UniformToFill
            };
            Grid.SetColumn(image, 0);
            contentGrid.Children.Add(image);
        }
        Grid.SetColumn(textPanel, 1);
        contentGrid.Children.Add(textPanel);

        var card = new Grid { Children = { contentGrid, closeButton } };
        var border = new Border
        {
            Width = 320,
            Height = 96,
            Background = new SolidColorBrush(Color.FromArgb(240, 32, 32, 32)),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16),
            Child = card
        };
        _border = border;
        border.PointerPressed += Border_OnPointerPressed;
        border.PointerEntered += (_, _) => _dismissTimer.Stop();
        border.PointerExited += (_, _) => _dismissTimer.Start();

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _dismissTimer.Tick += (_, _) => Close();
        _owner.Closed += Owner_OnClosed;
    }

    private void Show()
    {
        _overlay.Children.Add(_border);
        _dismissTimer.Start();
    }

    private void Border_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _onClick?.Invoke();
        Close();
    }

    private void Close()
    {
        if (_isClosed) return;
        _isClosed = true;
        _dismissTimer.Stop();
        _owner.Closed -= Owner_OnClosed;
        _overlay.Children.Remove(_border);
        _ownedThumbnail?.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
    }

    private void Owner_OnClosed(object? sender, EventArgs e) => Close();
}
