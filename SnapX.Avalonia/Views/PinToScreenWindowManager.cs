// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SixLabors.ImageSharp.PixelFormats;
using SnapX.Core;
using SnapX.Core.ImageEffects;
using SnapX.Core.Job;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Native;
using AvaloniaColor = Avalonia.Media.Color;

namespace SnapX.Avalonia.Views;

/// <summary>
/// Pins an image to the screen in a lightweight, resizable, draggable topmost
/// window. This mirrors ShareX's Pin to Screen tool. On Wayland a plain
/// top-level window may be tiled by the compositor instead of floating, so the
/// feature is capability-gated: it still works, but it is reported honestly
/// rather than claiming guaranteed layer-shell struts.
/// </summary>
public sealed class PinToScreenWindowManager
{
    private static readonly List<Window> ActiveWindows = [];
    private static readonly object WindowLock = new();

    /// <summary>Closes every pinned window.</summary>
    public static void CloseAll()
    {
        Dispatcher.UIThread.Post(() =>
        {
            List<Window> windows;
            lock (WindowLock)
            {
                windows = [.. ActiveWindows];
                ActiveWindows.Clear();
            }

            foreach (Window window in windows)
            {
                try
                {
                    window.Close();
                }
                catch
                {
                    // Already closed or destroyed.
                }
            }

        });
    }

    /// <summary>
    /// Pins a pre-converted bitmap in a new topmost window. The caller owns the
    /// source image and converts it (copying the pixels) before the worker can
    /// dispose it. This window manager owns the bitmap and disposes it on close.
    /// </summary>
    public static void Pin(Bitmap bitmap, TaskSettings? taskSettings)
    {
        if (bitmap is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            PinToScreenOptions options = taskSettings?.ToolsSettings?.PinToScreenOptions ?? new();
            int scalePercent = Math.Clamp(options.InitialScale, 10, 400);
            int opacityPercent = Math.Clamp(options.InitialOpacity, 10, 100);
            Rgba32 backgroundPixel = options.BackgroundColor.ToPixel<Rgba32>();
            Rgba32 borderPixel = options.BorderColor.ToPixel<Rgba32>();

            var imageControl = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var border = new Border
            {
                Background = new SolidColorBrush(AvaloniaColor.FromArgb(
                    (byte)(opacityPercent * 255 / 100),
                    backgroundPixel.R,
                    backgroundPixel.G,
                    backgroundPixel.B)),
                BorderBrush = options.Border
                    ? new SolidColorBrush(AvaloniaColor.FromArgb(
                        borderPixel.A,
                        borderPixel.R,
                        borderPixel.G,
                        borderPixel.B))
                    : Brushes.Transparent,
                BorderThickness = options.Border ? new Thickness(options.BorderSize) : new Thickness(0),
                CornerRadius = options.Shadow ? new CornerRadius(4) : new CornerRadius(0),
                BoxShadow = options.Shadow
                    ? new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 4, Blur = 16, Color = AvaloniaColor.FromArgb(80, 0, 0, 0) })
                    : default,
                Child = imageControl
            };

            var root = new Grid
            {
                RowDefinitions = { new RowDefinition(GridLength.Auto) }
            };
            root.Children.Add(border);

            var window = new Window
            {
                Title = "SnapX | Pin to screen",
                SystemDecorations = WindowDecorations.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = options.TopMost,
                CanResize = true,
                Content = root,
                Background = Brushes.Transparent,
                Width = Math.Max(100, bitmap.PixelSize.Width * scalePercent / 100),
                Height = Math.Max(100, bitmap.PixelSize.Height * scalePercent / 100),
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            AddDragSupport(window);
            PositionWindow(window, options.Placement, options.PlacementOffset);

            bool closed = false;
            window.Closed += (_, _) =>
            {
                lock (WindowLock)
                {
                    ActiveWindows.Remove(window);
                }

                if (!closed)
                {
                    closed = true;
                    bitmap.Dispose();
                }
            };

            lock (WindowLock)
            {
                ActiveWindows.Add(window);
            }

            window.Show();
            DebugHelper.WriteLine(
                $"Pin to screen opened at {window.Position} ({window.Width}x{window.Height}, opacity {opacityPercent}%, scale {scalePercent}%)." +
                (OperatingSystem.IsLinux() && LinuxAPI.IsWayland()
                    ? " Wayland: topmost stacking is compositor-dependent."
                    : ""));
        });
    }

    private static void AddDragSupport(Window window)
    {
        window.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
            {
                window.BeginMoveDrag(e);
            }
        };
    }

    private static void PositionWindow(Window window, ContentAlignment placement, int offset)
    {
        var screen = window.Screens.Primary;
        if (screen is null)
        {
            return;
        }

        PixelRect workArea = screen.WorkingArea;
        int x;
        int y;

        switch (placement)
        {
            case ContentAlignment.TopLeft:
                x = workArea.X + offset;
                y = workArea.Y + offset;
                break;
            case ContentAlignment.TopCenter:
                x = workArea.X + (workArea.Width - (int)window.Width) / 2;
                y = workArea.Y + offset;
                break;
            case ContentAlignment.TopRight:
                x = workArea.Right - (int)window.Width - offset;
                y = workArea.Y + offset;
                break;
            case ContentAlignment.MiddleLeft:
                x = workArea.X + offset;
                y = workArea.Y + (workArea.Height - (int)window.Height) / 2;
                break;
            case ContentAlignment.Center:
                x = workArea.X + (workArea.Width - (int)window.Width) / 2;
                y = workArea.Y + (workArea.Height - (int)window.Height) / 2;
                break;
            case ContentAlignment.MiddleRight:
                x = workArea.Right - (int)window.Width - offset;
                y = workArea.Y + (workArea.Height - (int)window.Height) / 2;
                break;
            case ContentAlignment.BottomLeft:
                x = workArea.X + offset;
                y = workArea.Bottom - (int)window.Height - offset;
                break;
            case ContentAlignment.BottomCenter:
                x = workArea.X + (workArea.Width - (int)window.Width) / 2;
                y = workArea.Bottom - (int)window.Height - offset;
                break;
            default:
                x = workArea.Right - (int)window.Width - offset;
                y = workArea.Bottom - (int)window.Height - offset;
                break;
        }

        window.Position = new PixelPoint(x, y);
    }
}
