// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using SnapX.Core;
using SnapX.Core.Job;
using SnapX.Core.Media;
using SnapX.Core.Utils;
using ImageRectangle = SixLabors.ImageSharp.Rectangle;
using SystemPath = System.IO.Path;

namespace SnapX.Avalonia.Views;

/// <summary>
/// The recording controller is an interactive wlr-layer-shell surface on
/// Wayland. This avoids both failure modes seen with Avalonia here: a normal
/// Window becomes a tiled xdg-toplevel, while an owner OverlayLayer/Popup can
/// be invisible or clipped. The native layer surface is compact, anchored to
/// the recording output, and has no task-view entry.
///
/// Non-Wayland platforms use an independent top-level window. The recording
/// controls therefore stay available when the main window is closed.
/// </summary>
public sealed class RecordingControlWindow
{
    private static RecordingControlWindow? _current;

    private readonly Border _card;
    private readonly TextBlock _elapsed;
    private readonly TextBlock _status;
    private readonly Button _pauseResume;
    private readonly DispatcherTimer _timer;
    private Window? _fallbackWindow;
    private Process? _nativeController;
    private bool _usesNativeLayer;

    public static void ShowRecording(ImageRectangle captureRectangle)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _current?.Close();

            // Start the native layer-shell controller before looking for an
            // Avalonia host. During recording the app can be tray-only, which
            // was exactly the path that made prior popup/overlay controls
            // disappear.
            if (OperatingSystem.IsLinux() && SnapX.Core.Utils.Native.LinuxAPI.IsWayland())
            {
                var nativeController = new RecordingControlWindow(captureRectangle);
                _current = nativeController;
                if (nativeController.Show(captureRectangle))
                {
                    return;
                }
                nativeController.Close();

                // Do not fall through to OverlayLayer here. Native Wayland
                // renders that layer through the transient EGL WSI path which
                // is the crash source during capture. A missing layer-shell
                // helper means recording continues without the controller;
                // the tray controls and global hotkeys remain available.
                DebugHelper.WriteLine("Recording controller skipped: native Wayland layer-shell helper is unavailable.");
                return;
            }

            // Keep the fallback independent from the main window. An owner
            // would close this window when SnapX changes to tray-only mode.
            var controller = new RecordingControlWindow(captureRectangle);
            _current = controller;
            controller.Show(captureRectangle);
        });
    }

    public static void HideRecording()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _current?.Close();
            _current = null;
        });
    }

    public static void RefreshState()
    {
        Dispatcher.UIThread.Post(() => _current?.UpdateLabels());
    }

    private RecordingControlWindow(ImageRectangle captureRectangle)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateLabels();

        var panel = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            }
        };
        header.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brushes.Red,
            VerticalAlignment = VerticalAlignment.Center
        });
        _status = new TextBlock
        {
            Text = "Recording",
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(_status, 1);
        header.Children.Add(_status);
        _elapsed = new TextBlock
        {
            Text = "00:00",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 247, 250)),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(_elapsed, 2);
        header.Children.Add(_elapsed);
        panel.Children.Add(header);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _pauseResume = MakeButton("Pause", () => TaskHelpers.PauseScreenRecording());
        buttons.Children.Add(_pauseResume);
        buttons.Children.Add(MakeButton("Stop", () => TaskHelpers.StopScreenRecording()));
        buttons.Children.Add(MakeButton("Abort", () => TaskHelpers.AbortScreenRecording()));
        panel.Children.Add(buttons);

        _card = new Border
        {
            Width = 340,
            Height = 118,
            Background = new SolidColorBrush(Color.FromArgb(238, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 58, 58, 58)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 4,
                Blur = 16,
                Color = Color.FromArgb(96, 0, 0, 0)
            }),
            Child = panel
        };

        DebugHelper.WriteLine($"Recording controller created for capture rectangle {captureRectangle}.");
    }

    private static Button MakeButton(string text, Action onClick)
    {
        // Keep the Avalonia fallback aligned with the native controller's
        // three 96px-wide hit targets: x=16/122/228, y=68..102 (see
        // CONTROLLER_BUTTON_* in Native/snapx-outline.c).
        var button = new Button
        {
            Content = text,
            Width = 96,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 13,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(255, 51, 51, 51)),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 247, 250))
        };
        if (text == "Abort")
        {
            button.Background = new SolidColorBrush(Color.FromArgb(255, 58, 38, 38));
            button.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 211, 216));
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    private bool Show(ImageRectangle captureRectangle)
    {
        _usesNativeLayer = TryStartNativeController(captureRectangle);
        if (_usesNativeLayer)
        {
            _timer.Start();
            return true;
        }

        ShowFallbackWindow(captureRectangle);
        _timer.Start();
        return true;
    }

    private void Close()
    {
        _timer.Stop();
        if (_usesNativeLayer)
        {
            StopNativeController();
        }
        else
        {
            Window? window = _fallbackWindow;
            _fallbackWindow = null;
            window?.Close();
        }
        if (ReferenceEquals(_current, this)) _current = null;
    }

    private void ShowFallbackWindow(ImageRectangle captureRectangle)
    {
        var window = new Window
        {
            Title = "SnapX Recording Controls",
            SystemDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            CanResize = false,
            Width = _card.Width,
            Height = _card.Height,
            Background = _card.Background,
            Content = _card,
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        window.Position = GetFallbackPosition(window, captureRectangle);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_fallbackWindow, window))
            {
                _fallbackWindow = null;
            }
        };
        _fallbackWindow = window;
        window.Show();
        DebugHelper.WriteLine(
            $"Recording controller Avalonia top-level opened at {window.Position}; " +
            $"topmost={window.Topmost}, taskbar={window.ShowInTaskbar}.");
    }

    private static PixelPoint GetFallbackPosition(Window window, ImageRectangle region)
    {
        const int gap = 12;
        const int cardWidth = 340;
        const int cardHeight = 118;

        var center = new PixelPoint(
            region.X + region.Width / 2,
            region.Y + region.Height / 2);
        var screen = window.Screens.ScreenFromPoint(center) ?? window.Screens.Primary;
        PixelRect workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        int maxX = Math.Max(workArea.X, workArea.Right - cardWidth);
        int maxY = Math.Max(workArea.Y, workArea.Bottom - cardHeight);
        int alignedX = Math.Clamp(region.X + region.Width - cardWidth, workArea.X, maxX);
        int alignedY = Math.Clamp(region.Y + (region.Height - cardHeight) / 2, workArea.Y, maxY);

        int belowY = region.Y + region.Height + gap;
        if (belowY <= maxY)
        {
            return new PixelPoint(alignedX, belowY);
        }

        int aboveY = region.Y - cardHeight - gap;
        if (aboveY >= workArea.Y)
        {
            return new PixelPoint(alignedX, aboveY);
        }

        int rightX = region.X + region.Width + gap;
        if (rightX <= maxX)
        {
            return new PixelPoint(rightX, alignedY);
        }

        int leftX = region.X - cardWidth - gap;
        if (leftX >= workArea.X)
        {
            return new PixelPoint(leftX, alignedY);
        }

        // A full-screen region has no outside space. Keep the controls in the
        // work area's lower-right corner so Stop and Abort stay reachable.
        return new PixelPoint(maxX, maxY);
    }

    private void UpdateLabels()
    {
        bool paused = ScreenRecordManager.IsPaused;
        string state = paused ? "Paused" : "Recording";
        _status.Text = state;
        _elapsed.Text = FormatElapsed(ScreenRecordManager.Elapsed);
        _pauseResume.Content = paused ? "Resume" : "Pause";

        // Read the reference once so a watchdog that disposes the process
        // cannot race HasExited outside the guard. This is set only from the
        // UI thread's Dispatcher or the watchdog's Interlocked.CompareExchange,
        // so Volatile.Read is enough for lock-free visibility of ownership.
        Process? process = Volatile.Read(ref _nativeController);
        // HasExited can throw ObjectDisposedException when the watchdog
        // disposes the process concurrently. Keep every process member
        // access inside one guarded block.
        if (_usesNativeLayer && process is not null)
        {
            try
            {
                if (process.HasExited)
                {
                    return;
                }
                process.StandardInput.WriteLine(
                    $"{(ScreenRecordManager.IsPaused ? "paused" : "recording")} {FormatElapsed(ScreenRecordManager.Elapsed)}");
                process.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                DebugHelper.WriteLine($"Recording controller state update failed: {ex.Message}");
            }
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

    private bool TryStartNativeController(ImageRectangle captureRectangle)
    {
        if (!OperatingSystem.IsLinux() || !SnapX.Core.Utils.Native.LinuxAPI.IsWayland())
        {
            return false;
        }

        string? helper = ResolveHelperPath();
        if (helper is null)
        {
            DebugHelper.WriteLine("Recording controller native helper is unavailable.");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = helper,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // These four arguments are the REAL capture geometry, not the old
            // 0 0 1 1 placeholder: the helper uses them to affix the control
            // tile immediately outside the recorded region's edge, aligned
            // with the red outline. Output-local coordinates are required
            // because layer-shell margins are relative to the selected output.
            // If the output cannot be resolved, the global rectangle is passed
            // through; outside-only mode makes the helper decline an
            // off-output or degenerate placement rather than covering the
            // recording.
            int regionX = captureRectangle.X;
            int regionY = captureRectangle.Y;
            string? outputName = null;
            int outputLogicalW = 0;
            int outputLogicalH = 0;
            int workAreaTop = 0;
            int workAreaBottom = 0;
            if (RecordingRegionOutline.TryGetCaptureOutputRegion(captureRectangle,
                    out string? resolvedOutputName, out int localX, out int localY,
                    out int resolvedLogicalW, out int resolvedLogicalH,
                    out int resolvedWorkTop, out int resolvedWorkBottom))
            {
                regionX = localX;
                regionY = localY;
                outputName = resolvedOutputName;
                outputLogicalW = resolvedLogicalW;
                outputLogicalH = resolvedLogicalH;
                workAreaTop = resolvedWorkTop;
                workAreaBottom = resolvedWorkBottom;
            }

            // Argument order is fixed and must stay stable:
            //   <x> <y> <w> <h> --controller [--output NAME] [--logical-w W --logical-h H]
            //   [--work-top T --work-bottom B]
            startInfo.ArgumentList.Add(regionX.ToString());
            startInfo.ArgumentList.Add(regionY.ToString());
            startInfo.ArgumentList.Add(captureRectangle.Width.ToString());
            startInfo.ArgumentList.Add(captureRectangle.Height.ToString());
            startInfo.ArgumentList.Add("--controller");
            if (!string.IsNullOrWhiteSpace(outputName))
            {
                startInfo.ArgumentList.Add("--output");
                startInfo.ArgumentList.Add(outputName);
            }

            // The helper cannot compute the output's logical size on its own:
            // wl_output.scale is an integer, so a fractional-scaled output
            // (e.g. 2560x1440 @ 1.6) advertises scale 2 and would be read as
            // 1280x720 instead of the real 1600x900 space that both the region
            // coordinates above and the compositor's layer margins use. Pass
            // the true logical size so edge placement is decided against it.
            if (outputLogicalW > 0 && outputLogicalH > 0)
            {
                startInfo.ArgumentList.Add("--logical-w");
                startInfo.ArgumentList.Add(outputLogicalW.ToString());
                startInfo.ArgumentList.Add("--logical-h");
                startInfo.ArgumentList.Add(outputLogicalH.ToString());
            }

            // Reserved insets (panels, docks) make the layer-shell work area
            // shorter than the output. Without them the helper clamps a card
            // for a bottom-of-screen region against the full output height and
            // parks it underneath the dock, so pass the usable band explicitly.
            if (workAreaBottom > workAreaTop && workAreaTop >= 0)
            {
                startInfo.ArgumentList.Add("--work-top");
                startInfo.ArgumentList.Add(workAreaTop.ToString());
                startInfo.ArgumentList.Add("--work-bottom");
                startInfo.ArgumentList.Add(workAreaBottom.ToString());
            }

            Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // A protocol/setup failure happens synchronously. Avoid claiming a
            // native controller exists when it already exited, then retain the
            // overlay as a best-effort fallback on unsupported compositors.
            if (process.WaitForExit(150))
            {
                string error = process.StandardError.ReadToEnd();
                DebugHelper.WriteLine($"Recording controller helper exited during startup: {error.Trim()}");
                process.Dispose();
                return false;
            }

            _nativeController = process;
            _ = Task.Run(() => ReadNativeControllerCommands(process));
            // If the native helper crashes or the compositor drops it, do not
            // leave the recording UI pretending to be alive. Log the failure
            // and tear down this controller's managed state; the recording
            // itself can continue through the tray/hotkeys, but a restarting
            // recording must not stack a second helper.
            _ = Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    DebugHelper.WriteLine($"Recording controller watchdog wait failed: {ex.Message}");
                    return;
                }


                // Only the thread that removes this process from ownership may
                // dispose/log it. CompareExchange returns the old value (the
                // process if we won, null if StopNativeController already took
                // it), so a controlled close never logs as unexpected.
                Process? owned = Interlocked.CompareExchange(ref _nativeController, null, process);
                if (!ReferenceEquals(owned, process))
                {
                    return;
                }

                DebugHelper.WriteLine("Recording controller native helper exited unexpectedly.");
                try
                {
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    DebugHelper.WriteLine($"Recording controller watchdog dispose failed: {ex.Message}");
                }
            });
            DebugHelper.WriteLine("Native recording controller layer opened.");
            return true;
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine($"Unable to start native recording controller: {ex.Message}");
            return false;
        }
    }

    private void ReadNativeControllerCommands(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                string? command = process.StandardOutput.ReadLine();
                if (command is null)
                {
                    break;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_nativeController, process))
                    {
                        return;
                    }

                    switch (command.Trim())
                    {
                        case "pause":
                            TaskHelpers.PauseScreenRecording();
                            break;
                        case "stop":
                            TaskHelpers.StopScreenRecording();
                            break;
                        case "abort":
                            TaskHelpers.AbortScreenRecording();
                            break;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine($"Native recording controller command loop failed: {ex.Message}");
        }
    }

    private void StopNativeController()
    {
        // Atomically take ownership before stopping, so a watchdog that sees
        // the exit cannot double-dispose or log this controlled close as
        // unexpected.
        Process? process = Interlocked.Exchange(ref _nativeController, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("quit");
                process.StandardInput.Flush();
                if (!process.WaitForExit(1000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine($"Unable to close native recording controller: {ex.Message}");
            try { process.Kill(entireProcessTree: true); }
            catch { /* Process already exited. */ }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string? ResolveHelperPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            SystemPath.Combine(baseDir, "snapx-outline"),
            SystemPath.Combine(baseDir, "native", "snapx-outline"),
            SystemPath.Combine(baseDir, "lib", "snapx", "snapx-outline"),
            "snapx-outline"
        ];

        foreach (string candidate in candidates)
        {
            if (SystemPath.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(SystemPath.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate = SystemPath.Combine(directory, "snapx-outline");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
