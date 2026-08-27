// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SnapX.Avalonia.Views;
using SnapX.Core.Media;
using SnapX.Core.Utils;

namespace SnapX.Avalonia.Utils;

/// <summary>
/// Keeps the tray icon in sync with an active screen recording, mirroring the
/// upstream ShareX behaviour. When a recording starts the tray icon switches to
/// a red recording indicator, the main window is hidden so the user can focus
/// on the content being captured, and Pause/Resume, Stop and Abort menu items
/// are shown in a dedicated on-screen control panel while recording.
/// </summary>
public sealed class RecordingTrayController : IDisposable
{
    private readonly TrayIcon? _trayIcon;
    private readonly Bitmap? _normalIcon;
    private readonly Bitmap? _recordingIcon;
    private bool _disposed;

    public RecordingTrayController(TrayIcon? trayIcon = null)
    {
        _trayIcon = trayIcon;
        if (trayIcon is not null)
        {
            _normalIcon = new Bitmap(
                AssetLoader.Open(new Uri("avares://snapx-ui/SnapX_Logo.png")));
            _recordingIcon = CreateRecordingIcon();
        }

        ScreenRecordManager.StateChanged += OnStateChanged;
        ScreenRecordManager.RecordingCompleted += OnRecordingCompleted;
        ScreenRecordManager.RecordingFailed += OnRecordingFailed;
        OnStateChanged(ScreenRecordManager.CurrentState);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ScreenRecordManager.StateChanged -= OnStateChanged;
        ScreenRecordManager.RecordingCompleted -= OnRecordingCompleted;
        ScreenRecordManager.RecordingFailed -= OnRecordingFailed;
        RecordingControlWindow.HideRecording();
        _recordingIcon?.Dispose();
        _normalIcon?.Dispose();
    }

    private static void OnRecordingCompleted(string path)
    {
        Dispatcher.UIThread.Post(() =>
        {
            App.SendDesktopNotification(Core.SnapXL.AppName, $"Recording finished:\n{path}");
            ToastNotificationWindow.ShowToast(
                null,
                Core.SnapXL.AppName,
                $"Recording finished:\n{path}",
                () => FileHelpers.OpenFile(path));
        });
    }

    private static void OnRecordingFailed(Exception exception)
    {
        Dispatcher.UIThread.Post(() =>
        {
            App.SendDesktopNotification(Core.SnapXL.AppName, $"Recording failed: {exception.Message}");
            ToastNotificationWindow.ShowToast(
                null,
                Core.SnapXL.AppName,
                $"Recording failed: {exception.Message}",
                () => { });
        });
    }

    private void OnStateChanged(ScreenRecordManager.RecordingManagerState state)
    {
        if (_disposed)
        {
            return;
        }

        bool isRecording = IsActiveRecordingState(state);
        Dispatcher.UIThread.Post(() =>
        {
            if (isRecording)
            {
                ShowRecordingUi();
            }
            else
            {
                HideRecordingUi();
            }
            RecordingControlWindow.RefreshState();
        });
    }

    private static bool IsActiveRecordingState(ScreenRecordManager.RecordingManagerState state)
    {
        return state is ScreenRecordManager.RecordingManagerState.Recording
            or ScreenRecordManager.RecordingManagerState.Pausing
            or ScreenRecordManager.RecordingManagerState.Paused
            or ScreenRecordManager.RecordingManagerState.Encoding;
    }

    private void ShowRecordingUi()
    {
        if (_trayIcon is not null && _recordingIcon is not null)
        {
            _trayIcon.Icon = new WindowIcon(_recordingIcon);
            _trayIcon.ToolTipText = "SnapX is recording";
        }
        var captureRectangle = ScreenRecordManager.CurrentCaptureRectangle;
        RecordingRegionOutline.Show(captureRectangle);
        RecordingControlWindow.ShowRecording(captureRectangle);
        App.SendDesktopNotification(Core.SnapXL.AppName, "Recording started.");
    }

    private void HideRecordingUi()
    {
        if (_trayIcon is not null && _normalIcon is not null)
        {
            _trayIcon.Icon = new WindowIcon(_normalIcon);
            _trayIcon.ToolTipText = Core.SnapXL.AppName;
        }
        RecordingRegionOutline.Hide();
        RecordingControlWindow.HideRecording();
    }

    private static Bitmap CreateRecordingIcon()
    {
        const int size = 32;
        var bitmap = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var fb = bitmap.Lock();
        int rowBytes = fb.RowBytes;

        unsafe
        {
            byte* dst = (byte*)fb.Address.ToPointer();
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int index = y * rowBytes + x * 4;
                    double dx = x - (size - 1) / 2.0;
                    double dy = y - (size - 1) / 2.0;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    double radius = size / 2.0;

                    byte b, g, r, a;
                    if (dist > radius)
                    {
                        // Outside the disc: fully transparent.
                        b = g = r = a = 0;
                    }
                    else if (dist > radius - 3)
                    {
                        // Bright red ring.
                        b = 20; g = 40; r = 235; a = 255;
                    }
                    else
                    {
                        // Dark red fill.
                        b = 20; g = 30; r = 180; a = 255;
                    }

                    dst[index + 0] = b;
                    dst[index + 1] = g;
                    dst[index + 2] = r;
                    dst[index + 3] = a;
                }
            }
        }

        return bitmap;
    }
}
