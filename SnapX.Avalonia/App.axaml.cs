using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SnapX.Avalonia.ViewModels;
using SnapX.Avalonia.ViewModels.Settings;
// using SnapX.Avalonia.ViewModels.Settings;
using SnapX.Avalonia.Views;
using SnapX.Avalonia.Views.Settings;
using SnapX.Core.Media;
using SnapX.Core.ScreenCapture;
using SnapX.Avalonia.Views.Settings.Views;
using SnapX.Avalonia.Utils;
using SnapX.Core;
using SnapX.Core.Capture;
using SnapX.Core.Job;
using SnapX.Core.Upload;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Extensions;
using SnapX.Core.Utils.Native;
using SixLabors.ImageSharp.Formats.Png;

namespace SnapX.Avalonia;

public partial class App : Application
{
    public App()
    {
        DataContext = this;
    }

    public static SnapXAvalonia SnapX { get; private set; } = null!;
    public static MainWindow? MyMainWindow { get; private set; }

    // There is no limit of what chaos could occur if two settings windows exist.
    // We must keep track of it.
    public static SettingsWindow? MySettingsWindow { get; set; }
    public static string TrayTitle => $"SnapX v{SimpleVersion()}";

    private static Lock _windowLock = new();
    private static readonly Lock ClipboardBitmapLock = new();
    private static readonly Lock WaylandClipboardProcessLock = new();
    private static readonly SemaphoreSlim ClipboardWriteGate = new(1, 1);
    // Avalonia's native clipboard backends can encode a Bitmap lazily, after
    // SetBitmapAsync returns. Keep the current bitmap alive until another
    // clipboard write replaces it (or the application exits).
    private static Bitmap? _clipboardBitmap;
    // wl-copy stays alive to own a Wayland selection. Keep the current owner
    // process handle so it cannot be collected while SnapX is running; the
    // compositor releases the previous owner when a later copy replaces it.
    private static Process? _waylandClipboardProcess;
    private int _shutdownStarted;
    // Do not invoke Core shutdown if startup failed before SnapX.start completed.
    private bool _coreStarted;
    private RecordingTrayController? _recordingTrayController;
    private static DesktopNotificationService? DesktopNotifications { get; set; }
    private SingleInstanceManager? _singleInstance;
    private readonly List<IDisposable> _signalRegistrations = [];

    private static string SimpleVersion()
    {
        var version = Version.Parse(Helpers.GetApplicationVersion());
        var versionString = $"{version.Major}.{version.Minor}.{version.Revision}";
        if (version.Build > 0)
            versionString += $".{version.Build}";
        return versionString;
    }

    public override void Initialize()
    {
        SnapX = new SnapXAvalonia();
        // SnapX.setQualifier(" UI");
        AvaloniaXamlLoader.Load(this);
        AppDomain.CurrentDomain.UnhandledException += (Sender, Args) =>
        {
            ShowErrorDialog(Lang.UnhandledException, Args.ExceptionObject as Exception);
        };
#if DEBUG
        // Avalonia 12 removed the in-process AttachDevTools() extension; dev
        // tooling now runs as a separate process via AvaloniaUI.DiagnosticsSupport.
        // Keep this hook empty so Debug builds compile without the legacy API.
#endif

        // Default logic doesn't auto-detect windows theme anymore in designer
        if (Design.IsDesignMode)
        {
            RequestedThemeVariant = ThemeVariant.Dark;
        }
    }

    private void ShowErrorDialog(string? title, Exception ex)
    {
        var stackPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        stackPanel.Children.Add(
            new SelectableTextBlock
            {
                Text = ex.GetType() + ": " + ex.Message,
                FontWeight = FontWeight.Bold
                // Padding = new Thickness(10)
            }
        );
        stackPanel.Children.Add(
            new SelectableTextBlock
            {
                Text = ex.StackTrace,
                FontWeight = FontWeight.SemiLight
                // Padding = new Thickness(10),
            }
        );
        var innerException = ex.InnerException;
        if (innerException != null)
        {
            stackPanel.Children.Add(
                new SelectableTextBlock
                {
                    Text = innerException.GetType() + ": " + innerException.Message,
                    FontWeight = FontWeight.Bold
                    // Padding = new Thickness(10)
                }
            );
            stackPanel.Children.Add(
                new SelectableTextBlock
                {
                    Text = innerException.StackTrace,
                    FontWeight = FontWeight.SemiLight
                    // Padding = new Thickness(10),
                }
            );
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var semver = version.Major + "." + version.Minor + "." + version.Revision;
        stackPanel.Children.Add(
            new SelectableTextBlock
            {
                Text = GetType().Assembly.GetName().Name + ": " + semver,
                FontWeight = FontWeight.SemiLight,
                FontSize = 16,
                FontFamily = new FontFamily("Consolas"),
                // Padding = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Left
            }
        );

        var reportButton = new Button
        {
            Content = Lang.ReportErrorToDeveloper,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
            Background = Brushes.DodgerBlue,
            Foreground = Brushes.White,
            BorderBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            FontWeight = FontWeight.Bold,
            CornerRadius = new CornerRadius(5)
        };
        reportButton.Click += (sender, e) => OnReportErrorClicked(reportButton, ex);

        var githubButton = new Button
        {
            Content = Lang.CreateGitHubIssue,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
            Background = Brushes.Green,
            // Foreground = Brushes.White,
            // BorderBrush = Brushes.IndianRed,
            BorderThickness = new Thickness(1),
            FontSize = 16,
            Padding = new Thickness(10),
            FontWeight = FontWeight.Bold,
            CornerRadius = new CornerRadius(5)
        };
        githubButton.Click += (sender, e) => OnGitHubButtonClicked(ex);

        var copyButton = new Button
        {
            Content = Lang.CopyErrorToClipboard,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
            // Background = Brushes.Green,
            // Foreground = Brushes.White,
            // BorderBrush = Brushes.Green,
            Background = Brushes.SlateGray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            FontWeight = FontWeight.Bold,
            CornerRadius = new CornerRadius(5)
        };

        copyButton.Click += (sender, e) => CopyErrorToClipboard(copyButton, ex.ToString());

        buttonPanel.Children.Add(reportButton);
        buttonPanel.Children.Add(githubButton);
        buttonPanel.Children.Add(copyButton);
        stackPanel.Children.Add(buttonPanel);

        // Create and show the error dialog with the formatted message
        var dialog = new FAAppWindow
        {
            Title = title,
            Content = stackPanel,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 400,
            MaxWidth = 1920,
            Padding = new Thickness(6)
            // Background = new ImageBrush()
            // {
            //     Source = new Bitmap(Assembly.GetExecutingAssembly().GetManifestResourceStream("SnapX.Avalonia.SnapX_Logo.png")!),
            //     Stretch = Stretch.UniformToFill
            // }
        };

        dialog.Show();
    }

    private void OnGitHubButtonClicked(Exception ex)
    {
        var newIssueURL = Helpers.GitHubIssueReport(ex);
        if (newIssueURL == null)
            return;
        URLHelpers.OpenURL(newIssueURL);
    }

    private void CopyErrorToClipboard(Control Sender, string? errorMessage)
    {
        var topLevel = TopLevel.GetTopLevel(Sender);
        if (topLevel is null)
        {
            DebugHelper.WriteLine("TopLevel is null");
            return;
        }

        if (topLevel.Clipboard is { } clipboard && !string.IsNullOrEmpty(errorMessage))
        {
            _ = SetClipboardTextAsync(clipboard, errorMessage);
        }
    }

    private async void OnReportErrorClicked(Button button, Exception ex)
    {
        var originalButtonContent = CreateContentCopy(button.Content!);

        try
        {
            if (!FeatureFlags.DisableTelemetry && Core.SnapXL.TelemetryHandler is null)
            {
                Core.SnapXL.InitTelemetryServices();
                SentrySdk.CaptureException(ex);

                DebugHelper.WriteLine("Error reported to Sentry successfully.");
            }
            else
            {
                DebugHelper.WriteLine(
                    "Error has likely already been sent to Sentry as telemetry is not disabled! :heart:"
                );
            }

            button.Content = "✓ Reported";
            button.IsEnabled = false;

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch (Exception taskEx)
        {
            DebugHelper.WriteLine($"Error during exception reporting: {taskEx.Message}");
        }
        finally
        {
            button.Content = originalButtonContent;
        }
    }

    private object CreateContentCopy(object content)
    {
        if (content == null)
            return null;

        return content switch
        {
            string str => new string(str.ToCharArray()),
            ICloneable cloneable => cloneable.Clone(),
            _ => content // For other types, we have to hope they're immutable
        };
    }

    private void Shutdown()
    {
        ShutdownCore();
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void ShutdownCore()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        // The recording outline is supplied by small native layer-shell
        // helper processes. Stop the recording UI before the dispatcher is
        // torn down so closing SnapX cannot leave a red outline (or a control
        // popup) behind on the desktop.
        try
        {
            // Stop the active encoder before stopping generic worker tasks.
            // ScreenRecordManager owns child ffmpeg/wf-recorder processes that
            // do not belong to the Avalonia window lifetime, so this explicit
            // abort is what guarantees they exit with SnapX.
            TaskHelpers.AbortScreenRecording();
            RecordingControlWindow.HideRecording();
            RecordingRegionOutline.Hide();
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to close recording UI during shutdown");
        }

        try
        {
            if (_coreStarted && SnapX != null)
            {
                _pollingCts?.Cancel();
                var shutdownTask = Task.Run(() => SnapX.shutdown());

                if (!shutdownTask.Wait(TimeSpan.FromSeconds(10)))
                {
                    Console.Error.WriteLine(
                        "SnapX shutdown timed out after 10 seconds, continuing exit."
                    );
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Console.Error.WriteLine("Error shutting down SnapX.Core, continuing shut down.");
        }

        _recordingTrayController?.Dispose();
        _recordingTrayController = null;
        if (DesktopNotifications is not null)
        {
            _ = DesktopNotifications.DisposeAsync();
            DesktopNotifications = null;
        }
        _singleInstance?.Dispose();
        _singleInstance = null;
        SingleInstanceManager.RelaunchWithoutCommandRequested -= OnRelaunchWithoutCommandRequested;
        foreach (IDisposable registration in _signalRegistrations)
        {
            registration.Dispose();
        }
        _signalRegistrations.Clear();
        // A foreground wl-copy process owns a Wayland selection after a
        // one-shot upload exits. Releasing our handle lets it survive until
        // the next clipboard owner replaces it; killing it here would clear
        // a just-copied upload URL before the user can paste it.
        ReleaseWaylandClipboardProcess();
        ReplaceClipboardBitmap(null);
        MyMainWindow = null;
    }

    /// <summary>
    /// Places a bitmap on the native clipboard and transfers its ownership to
    /// the application. X11 can request the bitmap later, so it must remain
    /// valid until a later clipboard write replaces it.
    /// </summary>
    public static async Task SetClipboardBitmapAsync(IClipboard clipboard, Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(bitmap);

        await ClipboardWriteGate.WaitAsync();
        try
        {
            // Avalonia uses its X11 clipboard implementation in this
            // application. Under a Wayland session the XWayland bridge
            // truncates a large PNG selection (a dual-monitor capture was cut
            // off at 192 KiB). Feed the compositor's native clipboard directly
            // instead.
            if (await TrySetWaylandClipboardBitmapAsync(bitmap))
            {
                bitmap.Dispose();
                ReplaceClipboardBitmap(null);
                return;
            }

            StopWaylandClipboardProcess();
            await clipboard.SetBitmapAsync(bitmap);
            ReplaceClipboardBitmap(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            ClipboardWriteGate.Release();
        }
    }

    private static async Task SetClipboardImageSharpAsync(
        IClipboard clipboard,
        SixLabors.ImageSharp.Image image)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(image);

        await ClipboardWriteGate.WaitAsync();
        try
        {
            if (await TrySetWaylandClipboardImageAsync(image))
            {
                ReplaceClipboardBitmap(null);
                return;
            }

            Bitmap bitmap = SnapX.ConvertImageSharpImgToAvalonia(image);
            try
            {
                StopWaylandClipboardProcess();
                await clipboard.SetBitmapAsync(bitmap);
                ReplaceClipboardBitmap(bitmap);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        finally
        {
            ClipboardWriteGate.Release();
        }
    }

    private static async Task<bool> TrySetWaylandClipboardImageAsync(SixLabors.ImageSharp.Image image)
    {
        if (!IsWaylandSession()) return false;

        await using var png = new MemoryStream();
        image.Save(png, new PngEncoder());
        png.Position = 0;
        return await TrySetWaylandClipboardPngAsync(png);
    }

    private static async Task<bool> TrySetWaylandClipboardBitmapAsync(Bitmap bitmap)
    {
        if (!IsWaylandSession()) return false;

        await using var png = new MemoryStream();
        bitmap.Save(png);
        png.Position = 0;
        return await TrySetWaylandClipboardPngAsync(png);
    }

    private static bool IsWaylandSession() =>
        OperatingSystem.IsLinux()
        && string.Equals(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            "wayland",
            StringComparison.OrdinalIgnoreCase
        );

    private static async Task<bool> TrySetWaylandClipboardPngAsync(Stream png)
    {
        try
        {
            Process? process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = "wl-copy",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    ArgumentList = { "--foreground", "--type", "image/png" }
                }
            );

            if (process is null)
            {
                throw new InvalidOperationException("Could not start wl-copy.");
            }

            await png.CopyToAsync(process.StandardInput.BaseStream);
            await process.StandardInput.DisposeAsync();

            // In --foreground mode a successful wl-copy remains alive to own
            // the selection, so waiting for its exit would make every capture
            // time out. Give immediate startup failures a small window, then
            // retain the live owner process for the selection lifetime.
            Task exited = process.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(100)) == exited)
            {
                string error = await process.StandardError.ReadToEndAsync();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"wl-copy exited with status {process.ExitCode}: {error.Trim()}"
                    );
                }

                process.Dispose();
                return true;
            }

            RetainWaylandClipboardProcess(process);
            return true;
        }
        catch (Win32Exception)
        {
            // wl-copy is optional. X11 and non-Wayland sessions retain the
            // existing Avalonia clipboard implementation.
            return false;
        }
    }

    private static async Task<bool> TrySetWaylandClipboardTextAsync(string text)
    {
        if (!IsWaylandSession()) return false;

        try
        {
            Process? process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = "wl-copy",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    ArgumentList = { "--foreground", "--type", "text/plain;charset=utf-8" }
                }
            );

            if (process is null)
            {
                throw new InvalidOperationException("Could not start wl-copy.");
            }

            await process.StandardInput.WriteAsync(text);
            await process.StandardInput.DisposeAsync();

            // wl-copy stays alive while it owns the Wayland selection. Keep
            // it after the application exits so a one-shot recording upload
            // remains pasteable, just as image clipboard writes do.
            Task exited = process.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(100)) == exited)
            {
                string error = await process.StandardError.ReadToEndAsync();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"wl-copy exited with status {process.ExitCode}: {error.Trim()}"
                    );
                }

                process.Dispose();
                return true;
            }

            RetainWaylandClipboardProcess(process);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static void RetainWaylandClipboardProcess(Process process)
    {
        Process? previous;
        lock (WaylandClipboardProcessLock)
        {
            previous = _waylandClipboardProcess;
            _waylandClipboardProcess = process;
        }

        // Do not kill the old owner: Wayland selection replacement tells it to
        // exit cleanly. Releasing our process handle is sufficient.
        previous?.Dispose();
    }

    private static void StopWaylandClipboardProcess()
    {
        Process? process;
        lock (WaylandClipboardProcessLock)
        {
            process = _waylandClipboardProcess;
            _waylandClipboardProcess = null;
        }

        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException)
        {
            // The compositor already released the selection and wl-copy exited.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void ReleaseWaylandClipboardProcess()
    {
        Process? process;
        lock (WaylandClipboardProcessLock)
        {
            process = _waylandClipboardProcess;
            _waylandClipboardProcess = null;
        }

        // Do not signal the process. The compositor will end it when a later
        // clipboard write replaces this selection.
        process?.Dispose();
    }

    public static async Task SetClipboardDataObjectAsync(
        IClipboard clipboard,
        DataTransfer dataTransfer,
        Bitmap? retainedBitmap = null)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(dataTransfer);

        await ClipboardWriteGate.WaitAsync();
        try
        {
            StopWaylandClipboardProcess();
            await clipboard.SetDataAsync(dataTransfer);
            ReplaceClipboardBitmap(retainedBitmap);
        }
        catch
        {
            retainedBitmap?.Dispose();
            throw;
        }
        finally
        {
            ClipboardWriteGate.Release();
        }
    }

    public static async Task SetClipboardTextAsync(IClipboard clipboard, string text)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(text);

        await ClipboardWriteGate.WaitAsync();
        try
        {
            if (await TrySetWaylandClipboardTextAsync(text))
            {
                ReplaceClipboardBitmap(null);
                return;
            }

            StopWaylandClipboardProcess();
            await clipboard.SetTextAsync(text);
            ReplaceClipboardBitmap(null);
        }
        finally
        {
            ClipboardWriteGate.Release();
        }
    }

    private static void ReplaceClipboardBitmap(Bitmap? bitmap)
    {
        Bitmap? previous;
        lock (ClipboardBitmapLock)
        {
            previous = _clipboardBitmap;
            _clipboardBitmap = bitmap;
        }

        if (!ReferenceEquals(previous, bitmap)) previous?.Dispose();
    }

    public void ListenForEvents()
    {
        Core.SnapXL.EventAggregator.Subscribe<NeedClipboardCopyEvent>(HandleClipboardCopyEvent);
        Core.SnapXL.EventAggregator.Subscribe<ErrorMessageEvent>(HandleErrorMessageEvent);
        Core.SnapXL.EventAggregator.Subscribe<NeedOCRWindowEvent>(HandleOCRWindowRequestEvent);
        Core.SnapXL.EventAggregator.Subscribe<NeedScanQRCodeEvent>(HandleScanQRCodeEvent);
        Core.SnapXL.EventAggregator.Subscribe<NeedToastNotificationEvent>(HandleToastNotificationEvent);
        Core.SnapXL.EventAggregator.Subscribe<NeedScrollCaptureResultEvent>(HandleScrollCaptureResultEvent);
    }
    void HandleScrollCaptureResultEvent(NeedScrollCaptureResultEvent @event)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // The result window takes ownership of the image clone and
                // disposes it (and its display bitmap) on close.
                var window = new ScrollingCaptureWindow(@event.Image, @event.TaskSettings, @event.Options);
                window.Show();
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Failed to open the scrolling capture window");
                try { @event.Image?.Dispose(); } catch { /* already released */ }
            }
        });
    }
     void HandleOCRWindowRequestEvent(NeedOCRWindowEvent @event)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var OCR = new OCR(@event.Image, @event.TaskSettings);
            OCR.Show();
        });
    }
    void HandleScanQRCodeEvent(NeedScanQRCodeEvent @event)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var qrView = new QRCodeView();
            qrView.Show();
            if (@event.HasImage) qrView.ScanImage(@event.Image);
            else qrView.QRText.Text = @event.Text;
        });
    }

    void HandlePinToScreenEvent(NeedPinToScreenEvent @event)
    {
        try
        {
            if (@event.CloseAll)
            {
                PinToScreenWindowManager.CloseAll();
                @event.MarkAsHandled();
                return;
            }

            if (@event.Image is not { } source)
            {
                @event.MarkAsFailed();
                return;
            }

            // Convert to an Avalonia bitmap on the dispatcher before completing
            // the event. The worker owns the source Image and disposes it as soon
            // as the event completes, so the pixels must be copied out first. The
            // window manager takes ownership of the resulting bitmap.
            Bitmap bitmap = Dispatcher.UIThread.Invoke(() => SnapX.ConvertImageSharpImgToAvalonia(source));
            PinToScreenWindowManager.Pin(bitmap, @event.TaskSettings);
            @event.MarkAsHandled();
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to pin image to screen");
            @event.MarkAsFailed();
        }
    }

    void HandleEditImageEvent(NeedEditImageEvent @event)
    {
        try
        {
            if (@event.Image is not { } source)
            {
                @event.Complete(null);
                return;
            }

            // Convert on the dispatcher before completing the request. The
            // worker owns the cloned source image and the editor must be able
            // to composite onto it, so use the event's clone directly. The
            // editor takes responsibility for closing the window and completing
            // the request with either an edited image or null (cancel).
            Dispatcher.UIThread.Post(() =>
            {
                var editor = new CapturedImageEditorWindow(@event, SnapX.ConvertImageSharpImgToAvalonia(source));
                editor.Show();
            });
            // The worker awaits Completion; it is completed when the editor
            // window is saved or cancelled.
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to open the image editor");
            @event.Complete(null);
        }
    }

    void HandleToastNotificationEvent(NeedToastNotificationEvent @event)
    {
        // WorkerTask releases its source Image as soon as task completion
        // handlers return. Convert the preview while it is still owned by the
        // event, rather than dereferencing a disposed Image later from the UI
        // dispatcher. This is especially visible for completed recordings,
        // whose preview is an FFmpeg-extracted frame.
        bool nativeWayland = OperatingSystem.IsLinux() && LinuxAPI.IsWayland();
        Bitmap? thumbnail = !nativeWayland && @event.Image is not null
            ? SnapX.ConvertImageSharpImgToAvalonia(@event.Image)
            : null;

        Dispatcher.UIThread.Post(() =>
        {
            Action? onClick = @event.ClickAction switch
            {
                ToastClickAction.OpenUrl when !string.IsNullOrEmpty(@event.Url) => () => URLHelpers.OpenURL(@event.Url),
                ToastClickAction.OpenFile when !string.IsNullOrEmpty(@event.FilePath) => () => FileHelpers.OpenFile(@event.FilePath),
                ToastClickAction.OpenFolder when !string.IsNullOrEmpty(@event.FilePath) => () => FileHelpers.OpenFolderWithFile(@event.FilePath),
                ToastClickAction.CopyUrl when !string.IsNullOrEmpty(@event.Url) => () =>
                    Core.SnapXL.EventAggregator.Publish(new NeedClipboardCopyEvent(@event.Url)),
                ToastClickAction.CopyFile when !string.IsNullOrEmpty(@event.FilePath) => () =>
                    Core.SnapXL.EventAggregator.Publish(new NeedClipboardCopyEvent(new[] { @event.FilePath })),
                ToastClickAction.CopyFilePath when !string.IsNullOrEmpty(@event.FilePath) => () =>
                    Core.SnapXL.EventAggregator.Publish(new NeedClipboardCopyEvent(@event.FilePath)),
                ToastClickAction.CopyImageToClipboard when @event.Image is not null => () =>
                    Core.SnapXL.EventAggregator.Publish(new NeedClipboardCopyEvent(@event.Image)),
                ToastClickAction.CloseNotification => null,
                _ => null
            };

            SendDesktopNotification(@event.Title, @event.Message);
            if (!nativeWayland)
            {
                ToastNotificationWindow.ShowToast(thumbnail, @event.Title, @event.Message, onClick);
            }
        });
    }

    internal static void SendDesktopNotification(string title, string message)
    {
        // Fire-and-forget the freedesktop notification. The D-Bus call is
        // isolated so a busy or missing notification daemon never blocks the
        // capture workflow or surfaces an error to the user.
        if (DesktopNotifications is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DesktopNotifications.NotifyAsync(title, message);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Failed to send desktop notification");
            }
        });
    }

    private void HandleClipboardCopyEvent(NeedClipboardCopyEvent @event)
    {
        _ = HandleClipboardCopyEventAsync(@event);
    }

    private async Task HandleClipboardCopyEventAsync(NeedClipboardCopyEvent @event)
    {
        try
        {
            // The worker that publishes capture results is not Avalonia's UI
            // thread. X11 and Wayland clipboard implementations share the UI
            // dispatcher's native connection, so run the complete operation on
            // that dispatcher and only then complete the producer's event.
            await Dispatcher.UIThread.InvokeAsync(() => CopyToClipboardAsync(@event));
            @event.MarkAsHandled();
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to copy data to the native clipboard");
            SendDesktopNotification("SnapX", "Clipboard copy failed: " + ex.Message);
            @event.MarkAsFailed();
        }
    }

    private static async Task CopyToClipboardAsync(NeedClipboardCopyEvent @event)
    {
        DebugHelper.WriteLine("HandleClipboardCopyEvent called");
        var clipboard = await GetClipboardAsync();

        bool hasAdditionalFormats = @event.AdditionalFormats.Count > 0 || @event.CustomData != null;

        if (@event.HasFiles && !hasAdditionalFormats && !@event.HasImage)
        {
                // Places real file objects on the clipboard, matching the
                // pattern already used successfully by the history view's own
                // "copy file" action: a DataObject with DataFormats.Files
                // backed by StorageProvider.TryGetFileFromPathAsync is the
                // format file managers, chat apps, and editors actually
                // recognize on a paste, unlike a bare text path.
            var storageProvider = TopLevel.GetTopLevel(MyMainWindow)?.StorageProvider;
            var storageItems = new List<IStorageItem>();
            if (storageProvider is not null)
            {
                foreach (string path in @event.FilePaths!.Where(File.Exists))
                {
                    var item = await storageProvider.TryGetFileFromPathAsync(path);
                    if (item is not null) storageItems.Add(item);
                }
            }

            var fileDataTransfer = new DataTransfer();
            var fileItem = new DataTransferItem();
            if (storageItems.Count > 0)
            {
                foreach (var storageItem in storageItems)
                {
                    fileItem.SetFile(storageItem);
                }
            }
                // Always also offer the path(s) as text: some paste targets
                // (terminals, plain text fields) only understand text, and a
                // file-manager StorageItem set that ends up empty (e.g. the
                // storage provider is unavailable) must not silently copy
                // nothing at all.
            fileItem.SetText(string.Join(Environment.NewLine, @event.FilePaths!));
            fileDataTransfer.Add(fileItem);
            await SetClipboardDataObjectAsync(clipboard, fileDataTransfer);
        }
        else if (@event.HasImage && !hasAdditionalFormats)
        {
                // SetBitmapAsync is the reliable cross-platform (X11/Wayland) path for
                // placing an image on the clipboard. A DataObject + SetDataObjectAsync
                // with DataFormat.Bitmap does not translate to the native image formats
                // on every backend (notably Wayland), so a paste into another app
                // produced nothing even though this call reported success. This is the
                // primary path for every screenshot capture entry point's after-capture
                // "copy to clipboard" task, so it must actually place image bytes that
                // other applications can paste.
            await SetClipboardImageSharpAsync(clipboard, @event.Image!);

            if (@event.HasText)
            {
                    // Every Set*Async call clears the clipboard (including
                    // SetDataObjectAsync), so writing text after the native bitmap
                    // would silently discard the image. There are currently no
                    // image-and-text producers; preserve the image if one is added
                    // later, because it is the explicit after-capture request.
                DebugHelper.WriteLine("Clipboard event contained both image and text; copied the image.");
            }
        }
        else if (@event.HasText && !hasAdditionalFormats && !@event.HasImage)
        {
            await SetClipboardTextAsync(clipboard, @event.Text!);
        }
        else
        {
            var dataTransfer = new DataTransfer();
            var dataItem = new DataTransferItem();

            if (@event.HasText)
            {
                dataItem.SetText(@event.Text!);
            }

            Bitmap? bitmap = null;
            if (@event.HasImage)
            {
                bitmap = SnapX.ConvertImageSharpImgToAvalonia(@event.Image!);
                dataItem.SetBitmap(bitmap);
            }

            foreach (var format in @event.AdditionalFormats)
            {
                var item = new DataTransferItem();
                if (format.Value is string strValue)
                {
                    item.SetText(strValue);
                }
                else if (format.Value is IImage imageValue)
                {
                    if (imageValue is Bitmap bmp)
                    {
                        item.SetBitmap(bmp);
                    }
                    else
                    {
                        // Render an arbitrary IImage to a Bitmap so it can be
                        // placed on the clipboard. RenderTargetBitmap re-draws
                        // the source image into a skia-backed bitmap.
                        var size = imageValue.Size;
                        var rtb = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
                        using (var ctx = rtb.CreateDrawingContext())
                        {
                            ctx.DrawImage(imageValue, new Rect(size));
                        }
                        item.SetBitmap(rtb);
                    }
                }
                else
                {
                    item.SetText(format.Value.ToString());
                }
                dataTransfer.Add(item);
            }

            if (@event.CustomData != null)
            {
                var customItem = new DataTransferItem();
                if (@event.CustomData is string customString)
                {
                    customItem.SetText(customString);
                }
                else
                {
                    var json = JsonHelpers.SerializeToString(@event.CustomData);
                    customItem.SetText(json);
                }
                dataTransfer.Add(customItem);
            }

            dataTransfer.Add(dataItem);
            await SetClipboardDataObjectAsync(clipboard, dataTransfer, bitmap);
        }
    }

    private async void HandleErrorMessageEvent(ErrorMessageEvent @event)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // FAContentDialog is implemented with Avalonia's popup/overlay
            // machinery. On native Wayland that creates the transient EGL
            // WSI surface which can fail while a capture or recording is
            // completing or reporting an error. Use the compositor-native
            // notification path instead, matching the toast guard above.
            if (OperatingSystem.IsLinux() && LinuxAPI.IsWayland())
            {
                TaskHelpers.PlayNotificationSoundAsync(NotificationSound.Error);
                SendDesktopNotification(
                    $"Error in {@event.Context}",
                    @event.Exception.Message);
                return;
            }

            try
            {
                var textBlock = new SelectableTextBlock
                {
                    Text = @event.FullError
                        ? @event.Exception.ToString()
                        : @event.Exception.Message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 600
                };

                var scrollViewer = new ScrollViewer
                {
                    Content = textBlock,
                    MaxHeight = 400,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };

                var dialog = new FAContentDialog
                {
                    Title = $"Error in {@event.Context}",
                    Content = scrollViewer,
                    CloseButtonText = "Close",
                    DefaultButton = FAContentDialogButton.Close,
                    PrimaryButtonText = @event.FullError ? "Copy" : null
                };
                TaskHelpers.PlayNotificationSoundAsync(NotificationSound.Error);
                var result = await dialog.ShowAsync();

                if (result == FAContentDialogResult.Primary)
                {
                    var topLevel = TopLevel.GetTopLevel(
                        MyMainWindow is not null ? MyMainWindow : dialog
                    );
                    if (topLevel?.Clipboard is { } clipboard)
                    {
                        await SetClipboardTextAsync(clipboard, @event.Exception.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                // Fallback to console if the UI is in a state where dialogs can't open
                DebugHelper.Logger?.Error("Critical: Could not open FluentAvalonia FAContentDialog.");
                DebugHelper.Logger?.Error(ex.ToString());
            }
        });
    }

    private static async Task<IClipboard> GetClipboardAsync()
    {
        // A live window owns the native clipboard selection on X11/Wayland. A
        // screen recording runs with the main window hidden (and possibly
        // closed) so the after-upload "copy URL" can arrive with no reusable
        // window; the old fallback re-showed MyMainWindow, which throws
        // "Cannot re-show a closed window" and silently dropped the URL.
        // RestoreAndFocusMainWindow re-creates a closed window or re-shows a
        // hidden one, then we read the clipboard from that live window.
        if (MyMainWindow is { IsLoaded: true } liveWindow)
        {
            return liveWindow.Clipboard;
        }

        RestoreAndFocusMainWindow();
        if (MyMainWindow is { IsLoaded: true } restoredWindow)
        {
            return restoredWindow.Clipboard;
        }

        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window =
                desktop.Windows.FirstOrDefault(w => w.IsActive && w.IsLoaded)
                ?? desktop.Windows.FirstOrDefault(w => w.IsLoaded);
            if (window != null)
            {
                return window.Clipboard;
            }
        }

        return await GetOrCreateClipboardWindowAsync();
    }

    private static Task<IClipboard> GetOrCreateClipboardWindowAsync()
    {
        lock (_windowLock)
        {
            // MyMainWindow can be null (never created) or a closed window
            // object (Closed never nulls the field). Never call Show() on a
            // closed Avalonia window: it throws and the clipboard write is
            // lost. RestoreAndFocusMainWindow re-creates/re-shows a live one.
            if (MyMainWindow is null || !MyMainWindow.IsLoaded)
            {
                RestoreAndFocusMainWindow();
            }

            if (MyMainWindow?.Clipboard is { } clipboard)
            {
                return Task.FromResult(clipboard);
            }

            throw new InvalidOperationException("Failed to obtain the SnapX clipboard window.");
        }
    }
    CancellationTokenSource? _pollingCts = null;

    // ReSharper disable once AsyncVoidMethod
    public override void OnFrameworkInitializationCompleted()
    {
        // Crashes must be contained, AT ALL COSTS!
        Dispatcher.UIThread.UnhandledException += (s, e) =>
        {
            e.Handled = true;
            var ex = e.Exception;

            ex.ShowError(true, "UI Dispatcher Critical Error");
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            e.SetObserved();
            // Background integrations (notably optional Wayland portal calls)
            // must never interrupt capture with a modal dialog. The failure is
            // retained in the log for diagnosis and the task is marked observed.
            DebugHelper.WriteException(e.Exception, "Unobserved background task exception");
        };
        var locator = new ViewLocator();
        DataTemplates.Add(locator);
        var services = new ServiceCollection();
        ConfigureServices(services);

        var provider = services.BuildServiceProvider();

        Ioc.Default.ConfigureServices(provider);
        var vm = Ioc.Default.GetRequiredService<MainViewModel>();

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                {
                    var sigintReceived = false;
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    desktop.ShutdownRequested += (_, _) =>
                    {
                        DebugHelper.WriteLine("Received Shutdown from Avalonia");
                        if (sigintReceived)
                            return;
                        sigintReceived = true;
                        ShutdownCore();

                        // desktop.Shutdown();
                    };

                    Console.CancelKeyPress += (_, ea) =>
                    {
                        DebugHelper.WriteLine("Received SIGINT (Ctrl+C)");
                        if (sigintReceived)
                            return;
                        ea.Cancel = true;
                        sigintReceived = true;
                        ShutdownCore();
                        try
                        {
                            desktop.Shutdown();
                        }
                        catch
                        {
                            // Silence at once
                        }
                    };
                    // Clean up the GlobalShortcuts portal session and the tray
                    // on SIGTERM/SIGHUP too (systemd, logout, or a direct kill).
                    // Without this, an unclean termination leaves stale global
                    // shortcut registrations in Hyprland that accumulate across
                    // restarts and can make a single key press fire more than
                    // once.
                    _signalRegistrations.Add(PosixSignalRegistration.Create(
                        PosixSignal.SIGTERM, ctx =>
                        {
                            DebugHelper.WriteLine("Received SIGTERM");
                            if (!sigintReceived)
                            {
                                sigintReceived = true;
                                ShutdownCore();
                                try
                                {
                                    desktop.Shutdown();
                                }
                                catch
                                {
                                    // Silence at once
                                }
                            }
                            ctx.Cancel = true;
                        }));
                    _signalRegistrations.Add(PosixSignalRegistration.Create(
                        PosixSignal.SIGHUP, ctx =>
                        {
                            DebugHelper.WriteLine("Received SIGHUP");
                            if (!sigintReceived)
                            {
                                sigintReceived = true;
                                ShutdownCore();
                                try
                                {
                                    desktop.Shutdown();
                                }
                                catch
                                {
                                    // Silence at once
                                }
                            }
                            ctx.Cancel = true;
                        }));
                    // AppDomain.CurrentDomain.ProcessExit += (o, _) =>
                    // {
                    //     if (!sigintReceived)
                    //     {
                    //         sigintReceived = true;
                    //         DebugHelper.WriteLine("Received SIGTERM");
                    //         SnapX.shutdown();
                    //     }
                    //     else
                    //     {
                    //         DebugHelper.WriteLine("Received SIGTERM, ignoring it because already processed SIGINT");
                    //     }
                    // };
                    var errorStarting = false;
                    // Forwarding now happens before Avalonia starts, so secondary
                    // processes exit immediately without entering this lifetime.
                    _singleInstance = Program.ForwardedPrimaryInstance;
                    // A relaunch that carries no CLI command (app launcher,
                    // desktop entry, dock) must resurface this instance's
                    // window instead of silently doing nothing.
                    SingleInstanceManager.RelaunchWithoutCommandRequested -= OnRelaunchWithoutCommandRequested;
                    SingleInstanceManager.RelaunchWithoutCommandRequested += OnRelaunchWithoutCommandRequested;
                    // Drain anything received while Avalonia was starting and
                    // make future arrivals dispatch immediately.
                    _singleInstance?.MarkDispatchReady();
                    // DebugHelper.Logger.Debug($"Avalonia Args: {desktop.Args}");
                    try
                    {
                        SnapX.start(desktop.Args ?? []);
                        _coreStarted = true;
                        var CLIManager = SnapX.GetCLIManager();
                        CLIManager.UseCommandLineArgs().GetAwaiter().GetResult();
                        Program.StartOneShotExitGuard();
                    }
                    catch (Exception ex)
                    {
                        errorStarting = true;
                        DebugHelper.WriteException(ex);
                        ShowErrorDialog(Lang.SnapXFailedToStart, ex);
                    }

                    if (errorStarting)
                        return;
                    ListenForEvents();
                    DesktopNotifications ??= new DesktopNotificationService();
                    DebugHelper.WriteLine("Internal Startup time: {0} ms", SnapX.getStartupTime());

                    var logoBitmap = new Bitmap(
                        AssetLoader.Open(new Uri("avares://snapx-ui/SnapX_Logo.png"))
                    );
                    // The tray is what makes SnapX show up as an app in a
                    // Quickshell-style system bar: that widget is a
                    // StatusNotifierItem host, not a window-list or a
                    // notification-source list, so an app without a
                    // StatusNotifierItem is simply absent from it.
                    //
                    // The XEmbed crash this used to guard against belongs to
                    // the X11 backend: Avalonia.X11 carries both
                    // XEmbedTrayIconImpl and DBusTrayIconImpl and picks XEmbed
                    // when the X server has no StatusNotifier host, which on
                    // an Xwayland session aborts the process with
                    // X_GetProperty(BadAtom). Avalonia.Wayland has no XEmbed
                    // path at all - its CreateTrayIcon only ever builds a
                    // DBusTrayIconImpl - so blanket-disabling the tray for
                    // "IsWayland" also disabled the one implementation that
                    // is safe here, and did so precisely on the sessions where
                    // the native backend is in use.
                    //
                    // Gate on the backend actually in use rather than on the
                    // session type: native Wayland gets the D-Bus item, an
                    // Xwayland/X11 session keeps the previous behaviour.
                    if (SnapX.GetConfiguration().ShowTray
                        && (!LinuxAPI.IsWayland() || Program.IsNativeWaylandBackend))
                    {
                        var trayIcon = new TrayIcon
                        {
                            Icon = new WindowIcon(logoBitmap),
                            ToolTipText = Core.SnapXL.AppName
                        };
                        trayIcon.Clicked += async (_, _) =>
                        {
                            if (ScreenRecordManager.IsRecording)
                            {
                                TaskHelpers.StopScreenRecording();
                                return;
                            }

                            await TaskHelpers.ExecuteJob(HotkeyType.RectangleRegion);
                        };

                        var menu = new NativeMenu();
                        menu.Opening += NativeMenu_OnOpening;
                        menu.NeedsUpdate += NativeMenu_OnNeedsUpdate;

                        var about = new NativeMenuItem(TrayTitle)
                        {
                            Icon = logoBitmap,
                            ToolTip = Lang.AboutSnapX
                        };
                        about.Click += NativeMenuItem_SnapX_OnClick;
                        menu.Items.Add(about);
                        menu.Items.Add(new NativeMenuItemSeparator());

                        var capture = new NativeMenuItem("Capture") { Menu = new NativeMenu() };
                        var full = new NativeMenuItem(Lang.UI_Capture_Fullscreen);
                        full.Click += NativeMenuItem_Capture_Fullscreen_OnClick;
                        capture.Menu.Items.Add(full);
                        var windowMenu = new NativeMenu();
                        // Never called on Linux!
                        // @see https://github.com/AvaloniaUI/Avalonia/issues/8076
                        windowMenu.NeedsUpdate += (sender, e) => { PopulateWindowMenu(windowMenu); };
                        var windowPicker = new NativeMenuItem(Lang.UI_Dropdown_Window)
                        {
                            Menu = windowMenu
                        };
                        void StartPolling(NativeMenu windowMenu, NativeMenu screenMenu)
                        {
                            DebugHelper.WriteLine("SnapX has started polling for window changes and display changes. This is because Avalonia on Linux/FreeBSD does not support the NeedsUpdate event for the tray menu.");
                            _pollingCts?.Cancel();
                            _pollingCts = new CancellationTokenSource();

                            _ = Task.Run(async () =>
                            {
                                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

                                while (await timer.WaitForNextTickAsync(_pollingCts.Token))
                                {
                                    await Dispatcher.UIThread.InvokeAsync(() =>
                                    {

                                        PopulateWindowMenu(windowMenu);
                                        PopulateMonitorMenu(screenMenu);
                                    }, DispatcherPriority.Background);
                                }
                            }, _pollingCts.Token);
                        }

                        void PopulateWindowMenu(NativeMenu menu)
                        {
                            try
                            {
                                var windows = Methods.GetWindowList();
                                var windowsById = windows.ToDictionary(w => w.Handle, w => w);

                                for (var i = menu.Items.Count - 1; i >= 0; i--)
                                {
                                    var item = (NativeMenuItem)menu.Items[i];
                                    if (item.CommandParameter is not IntPtr handle || !windowsById.ContainsKey(handle))
                                    {
                                        menu.Items.RemoveAt(i);
                                    }
                                }

                                foreach (var window in windows)
                                {
                                    var existingItem = menu.Items.Cast<NativeMenuItem>()
                                        .FirstOrDefault(item => item.CommandParameter is IntPtr handle && handle == window.Handle);

                                    if (existingItem != null)
                                    {
                                        if (existingItem.Header != window.Title) existingItem.Header = window.Title;
                                        continue;
                                    }

                                    var nativeWindowItem = new NativeMenuItem(window.Title)
                                    {
                                        Icon = logoBitmap,
                                        ToolTip = window.ProcessName,
                                        CommandParameter = window.Handle
                                    };

                                    nativeWindowItem.Click += (Sender, EA) =>
                                    {
                                        Task.Run(async () =>
                                        {
                                            var capturedImage = await Methods.CaptureWindow(window).ConfigureAwait(false);
                                            if (capturedImage != null)
                                            {
                                                UploadManager.RunImageTask(
                                                    capturedImage,
                                                    TaskSettings.GetDefaultTaskSettings()
                                                );
                                            }
                                        });
                                    };

                                    windowMenu.Add(nativeWindowItem);
                                }
                            }
                            catch (Exception ex)
                            {
                                ShowErrorDialog(Lang.SnapXFailedToStart, ex);
                            }
                        }

                        capture.Menu.Items.Add(windowPicker);
                        var screens = SnapXResources.graphicsInfo?.Monitors;
                        var screenMenu = new NativeMenu();
                        var monitorPicker = new NativeMenuItem(Lang.UI_Dropdown_Monitor)
                        {
                            Menu = screenMenu
                        };
                        monitorPicker.Menu.NeedsUpdate += (sender, e) => { PopulateMonitorMenu(screenMenu); };

                        void PopulateMonitorMenu(NativeMenu menu)
                        {
                            try
                            {
                                var currentScreens = screens?.Select((s, idx) => (s, idx)).ToList() ?? [];
                                var screensByName = currentScreens.ToDictionary(pair => pair.s.Name, pair => pair);

                                for (var i = menu.Items.Count - 1; i >= 0; i--)
                                {
                                    var item = (NativeMenuItem)menu.Items[i];
                                    if (item.CommandParameter is not string screenName || !screensByName.ContainsKey(screenName))
                                    {
                                        menu.Items.RemoveAt(i);
                                    }
                                }

                                foreach (var (screen, i) in currentScreens)
                                {
                                    var header = $"{i}: {screen.Name} {screen.Resolution} (X: {screen.Position?.X ?? 0}, Y: {screen.Position?.Y ?? 0})";
                                    var existingItem = menu.Items.Cast<NativeMenuItem>()
                                        .FirstOrDefault(item => item.CommandParameter is string name && name == screen.Name);

                                    if (existingItem != null)
                                    {
                                        if (existingItem.Header != header)
                                        {
                                            existingItem.Header = header;
                                        }
                                        continue;
                                    }

                                    var item = new NativeMenuItem(header)
                                    {
                                        CommandParameter = screen.Name
                                    };

                                    item.Click += (s, ev) =>
                                    {
                                        Task.Run(async () =>
                                        {
                                            var capturedImage = await Methods.CaptureScreen(screen.Name).ConfigureAwait(false);

                                            if (capturedImage != null)
                                            {
                                                UploadManager.RunImageTask(
                                                    capturedImage,
                                                    TaskSettings.GetDefaultTaskSettings()
                                                );
                                            }
                                        });
                                    };

                                    menu.Items.Add(item);
                                }
                            }
                            catch (Exception ex)
                            {
                                ShowErrorDialog(Lang.SnapXFailedToStart, ex);
                            }
                        }

                        PopulateWindowMenu(windowMenu);
                        PopulateMonitorMenu(screenMenu);
                        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
                            StartPolling(windowMenu, screenMenu);
                        capture.Menu.Items.Add(monitorPicker);
                        var regionCaptureMenuItem = new NativeMenuItem(Lang.UI_Dropdown_Region);
                        regionCaptureMenuItem.Click += async (_, _) => await TaskHelpers.ExecuteJob(HotkeyType.RectangleRegion);
                        capture.Menu.Items.Add(regionCaptureMenuItem);
                        var scrollingCaptureMenuItem = new NativeMenuItem(Lang.UI_Dropdown_ScrollingCapture);
                        scrollingCaptureMenuItem.Click += async (_, _) =>
                            TaskHelpers.OpenScrollingCapture(TaskSettings.GetDefaultTaskSettings());
                        capture.Menu.Items.Add(scrollingCaptureMenuItem);
                        var annotateMenuItem = new NativeMenuItem(Lang.UI_Dropdown_Annotate);
                        annotateMenuItem.Click += async (_, _) =>
                            TaskHelpers.OpenImageEditor(TaskSettings.GetDefaultTaskSettings());
                        capture.Menu.Items.Add(annotateMenuItem);
                        // capture.Menu.Items.Add(new NativeMenuItem("Region (Light)"));
                        // capture.Menu.Items.Add(new NativeMenuItem("Region (Transparent)"));
                        menu.Items.Add(capture);
                        var uploadFile = new NativeMenuItem("Upload file");
                        uploadFile.Click += (_, _) =>
                        {
                            Core.SnapXL.EventAggregator.Publish(
                                new NeedFileOpenerEvent
                                {
                                    Title = "SnapX | Upload File",
                                    Multiselect = true
                                }
                            );
                        };
                        var uploadFolder = new NativeMenuItem("Upload folder");
                        uploadFolder.Click += (_, _) =>
                        {
                            Core.SnapXL.EventAggregator.Publish(
                                new NeedFileOpenerEvent
                                {
                                    Title = "SnapX | Upload Folder",
                                    Multiselect = false,
                                    FolderPicker = true
                                }
                            );
                        };
                        var uploadText = new NativeMenuItem("Upload text");
                        uploadText.Click += (_, _) =>
                        {
                            var textBoxWindow = new Window();
                            textBoxWindow.Title = "SnapX | Upload Text";
                            var stackPanel = new StackPanel();
                            stackPanel.Margin = new Thickness(10);
                            textBoxWindow.Content = stackPanel;
                            var textBox = new TextBox();
                            textBox.MaxWidth = 450;
                            textBox.TextWrapping = TextWrapping.Wrap;
                            textBox.MinHeight = 150;
                            stackPanel.Children.Add(textBox);
                            var uploadButton = new Button();
                            uploadButton.Content = "Upload";
                            uploadButton.VerticalAlignment = VerticalAlignment.Bottom;

                            uploadButton.Click += (_, _) =>
                            {
                                UploadManager.UploadText(textBox.Text);
                                textBoxWindow.Close();
                            };
                            stackPanel.Children.Add(uploadButton);
                            var cancelButton = new Button();
                            cancelButton.Content = "Cancel";
                            cancelButton.VerticalAlignment = VerticalAlignment.Bottom;
                            cancelButton.Click += (_, _) => textBoxWindow.Close();
                            stackPanel.Children.Add(cancelButton);

                            textBoxWindow.Width = 500;
                            textBoxWindow.Height = 800;
                            textBoxWindow.Show();
                        };
                        // new NativeMenuItem("Upload from clipboard..."),
                        var shortenURL = new NativeMenuItem("Shorten URL");
                        menu.Items.Add(
                            new NativeMenuItem("Upload")
                            {
                                Menu = new NativeMenu
                                {
                                uploadFile,
                                uploadFolder,
                                uploadText,
                                shortenURL
                                }
                            }
                        );
                        var captureFullscreenMenuItem = new NativeMenuItem(Lang.UI_Capture_Fullscreen);
                        captureFullscreenMenuItem.Click += NativeMenuItem_Capture_Fullscreen_OnClick;
                        var captureActiveWindowMenuItem = new NativeMenuItem("Capture active window");
                        captureActiveWindowMenuItem.Click +=
                            NativeMenuItem_Workflows_CaptureActiveWindow_OnClick;
                        var captureActiveScreenMenuItem = new NativeMenuItem("Capture active screen");
                        captureActiveScreenMenuItem.Click +=
                            NativeMenuItem_Workflows_CaptureActiveScreen_OnClick;
                        var workflows = new NativeMenuItem("Workflows")
                        {
                            Menu =
                            [
                                captureFullscreenMenuItem,
                            captureActiveScreenMenuItem,
                            captureActiveWindowMenuItem
                            ]
                        };

                        menu.Items.Add(workflows);

                        // State-aware recording controls. While a recording is
                        // active, Stop/Pause/Resume/Abort are prominent; while
                        // idle, the start actions are prominent. This mirrors a
                        // ShareX-style tray/task-view presence. The submenu is
                        // rebuilt each time it opens so the actions always
                        // reflect ScreenRecordManager.CurrentState.
                        var recordingMenu = new NativeMenu();
                        recordingMenu.Opening += (_, _) => RebuildRecordingMenu(recordingMenu);
                        var recordingMenuItem = new NativeMenuItem("Recording")
                        {
                            Menu = recordingMenu
                        };
                        menu.Items.Add(recordingMenuItem);
                        RebuildRecordingMenu(recordingMenu);

                        menu.Items.Add(new NativeMenuItemSeparator());

                        var historyItem = new NativeMenuItem("History");
                        historyItem.Click += (_, _) => NativeMenuItem_Open_History_OnClick();
                        menu.Items.Add(historyItem);

                        var latestImage = new NativeMenuItem("Open latest screenshot");
                        latestImage.Click += (_, _) => OpenLatestHistoryItem("Image");
                        menu.Items.Add(latestImage);

                        var latestVideo = new NativeMenuItem("Open latest video");
                        latestVideo.Click += (_, _) => OpenLatestHistoryItem("Video");
                        menu.Items.Add(latestVideo);

                        var settingsItem = new NativeMenuItem("Settings");
                        settingsItem.Click += (_, _) => OpenInAppSettings();
                        menu.Items.Add(settingsItem);

                        menu.Items.Add(new NativeMenuItemSeparator());

                        var open = new NativeMenuItem("Open");
                        open.Command = OpenSnapXCommand;
                        open.Click += NativeMenuItem_Open_OnClick;
                        menu.Items.Add(open);

                        var quit = new NativeMenuItem("Quit");
                        quit.Click += NativeMenuItem_Quit_OnClick;
                        menu.Items.Add(quit);

                        trayIcon.Menu = menu;

                        // Register on X11 (where Avalonia decides between
                        // XEmbed and D-Bus itself) and on the native Wayland
                        // backend (D-Bus StatusNotifierItem only). The one
                        // combination still skipped is an X11/Xwayland session
                        // that this process is hosting through the X11
                        // backend while a native Wayland session is present,
                        // which is the XEmbed BadAtom crash path.
                        if (!LinuxAPI.IsWayland() || Program.IsNativeWaylandBackend)
                        {
                            TrayIcon.SetIcons(Current, [trayIcon]);
                            // Avalonia.Wayland's built-in DBusTrayIconImpl owns
                            // StatusNotifierItem registration on native Wayland.
                        }
                        else
                        {
                            DebugHelper.WriteLine("Skipping incompatible XEmbed tray registration on native Wayland.");
                        }
                        _recordingTrayController?.Dispose();
                        _recordingTrayController = new RecordingTrayController(trayIcon);
                    }

                    // Recording UI and lifecycle must not depend on a tray
                    // implementation. This also covers X11 desktops without
                    // a tray host and later non-Linux platforms.
                    if (_recordingTrayController is null)
                    {
                        _recordingTrayController = new RecordingTrayController();
                    }

                    if (SnapX.isSilent())
                        return;
                    if (SnapX.GetCLIManager().IsCommandExist("video"))
                    {
                        throw new NotImplementedException("LibVLC is removed from SnapX.Avalonia");
                    }

                    var Window = new MainWindow(vm);
                    WaylandAppIdentity.Attach(Window);
                    Window.Show();
                    DebugHelper.WriteLine("MainWindow startup time: {0} ms", SnapX.getStartupTime());

                    MyMainWindow = Window;
                    desktop.MainWindow = Window;
                    // MyMainWindow.Closed += (_, _) =>
                    // {
                    //     MyMainWindow = null;
                    // };
                    break;
                }
            case ISingleViewApplicationLifetime singleView when SnapX.isSilent():
                return;
            case ISingleViewApplicationLifetime singleView:
                {
                    var mv = new MainWindow(vm);
                    mv.Show();
                    MyMainWindow = mv;
                    singleView.MainView = mv;
                    break;
                }
        }
    }

    public static void CreateOrOpenSettingsWindowStatic()
    {
        if (MySettingsWindow is null)
        {
            var settingsWindow = Design.IsDesignMode
                ? Activator.CreateInstance<SettingsWindow>()
                : Ioc.Default.GetService<SettingsWindow>();
            if (settingsWindow is null)
            {
                DebugHelper.WriteLine("Failed to create about window, got null back from IoC");
                return;
            }

            MySettingsWindow = settingsWindow;
            settingsWindow.Closed += (_, _) => MySettingsWindow = null;
        }

        if (MyMainWindow is not null && MyMainWindow.IsVisible)
        {
            MySettingsWindow.Show(MyMainWindow);
            MySettingsWindow.Focus();
            MySettingsWindow.Activate();
        }
        else
        {
            MySettingsWindow.ShowAsDialog = false;
            MySettingsWindow.Show();
            MySettingsWindow.Focus();
            MySettingsWindow.Activate();
        }
    }

    /// <summary>
    /// Opens the settings page inside the main SnapX window rather than in a
    /// detached SettingsWindow. The main window is restored to the foreground
    /// first (it may be minimized or hidden to the tray), then its current
    /// page is switched to the in-app settings host. Keeping settings inside
    /// the app avoids the separate windowed settings surface.
    /// </summary>
    public static void OpenInAppSettings()
    {
        RestoreAndFocusMainWindow();

        if (MyMainWindow?.DataContext is not MainViewModel mainViewModel)
        {
            DebugHelper.WriteLine("OpenInAppSettings: the main window has no MainViewModel data context.");
            return;
        }

        try
        {
            mainViewModel.CurrentPage = Ioc.Default.GetRequiredService<InAppSettingsHostVM>();
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to open settings inside the SnapX main window.");
        }
    }

    public static void CreateAboutWindowStatic()
    {
        var aboutWindow = Design.IsDesignMode
            ? Activator.CreateInstance<AboutWindow>()
            : Ioc.Default.GetService<AboutWindow>();
        if (aboutWindow is null)
        {
            DebugHelper.WriteLine("Failed to create about window, got null back from IoC");
            return;
        }

        if (MyMainWindow is not null && MyMainWindow.IsVisible)
        {
            aboutWindow.Show(MyMainWindow);
            aboutWindow.Focus();
            aboutWindow.Activate();
        }
        else
        {
            aboutWindow.ShowAsDialog = false;
            aboutWindow.Show();
            aboutWindow.Focus();
            aboutWindow.Activate();
        }
    }

    private void NativeMenuAboutSnapXClick(object? Sender, EventArgs E)
    {
        CreateAboutWindowStatic();
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog(dispose: true));

        services.AddTransient<MainViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<RegionSelectorViewModel>();
        services.AddTransient<RegionSelectorWindow>();
        services.AddTransient<InAppSettingsHost>();
        services.AddTransient<InAppSettingsHostVM>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<SettingsMainView>();
        services.AddTransient<SettingsMainViewVM>();
        services.AddTransient<CustomUploaderView>();
        services.AddSingleton<CustomUploaderVM>();
        services.AddSingleton<ImportExportVM>();
        services.AddTransient<ImportExportView>();
        services.AddTransient<ScreenRecordOptionsVM>();
        services.AddTransient<ScreenRecordOptionsView>();
        services.AddTransient<SettingsCategoryVM>();
        services.AddTransient<SettingsCategoryView>();
        services.AddSingleton<CoreUploaderVM>();
        services.AddTransient<BuiltInUploaderSettingsView>();
        services.AddSingleton<DatabaseVM>();
        services.AddSingleton<SqliteConnection>(sp => SnapX.GetDB());
        services.AddTransient<DatabaseView>();
        services.AddTransient<SettingsHomePageView>();
        services.AddSingleton<SettingsHomePageViewVM>();

        services.AddTransient<AboutWindow>();
        services.AddSingleton<AboutWindowViewModel>();

        services.AddTransient<HomePageView>();
        services.AddSingleton<HomePageViewModel>();
        services.AddTransient<NotImplemented>();
        services.AddSingleton<NotImplementedVM>();

        services.AddTransient<ApplicationUploadSettingsView>();
        services.AddSingleton<ApplicationUploadSettingsVM>();
        services.AddSingleton<ApplicationPathSettingsVM>();
        services.AddTransient<ApplicationPathSettingsView>();

        services.AddSingleton<GeneralSettingsVM>();
        services.AddTransient<GeneralSettingsView>();

        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
    }

    private void NativeMenuItem_Quit_OnClick(object? Sender, EventArgs E)
    {
        Shutdown();
    }

    private void NativeMenuItem_SnapX_OnClick(object? Sender, EventArgs E)
    {
        NativeMenuAboutSnapXClick(Sender, E);
    }

    private async void NativeMenuItem_Capture_Fullscreen_OnClick(object? Sender, EventArgs E)
    {
        await Task.Factory.StartNew(
            () => { new CaptureFullscreen().Capture(); },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
    }

    private async void NativeMenuItem_Workflows_CaptureActiveScreen_OnClick(
        object? Sender,
        EventArgs E
    )
    {
        await Task.Factory.StartNew(
            () => { new CaptureActiveMonitor().Capture(); },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
    }

    private async void NativeMenuItem_Workflows_CaptureActiveWindow_OnClick(
        object? Sender,
        EventArgs E
    )
    {
        await Task.Factory.StartNew(
            () => { new CaptureActiveWindow().Capture(); },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
    }

    private void NativeMenuItem_Open_OnClick(object? Sender, EventArgs E)
    {
        RestoreAndFocusMainWindow();
    }

    /// <summary>
    /// Brings the main window back from every state it can be parked in:
    /// never created, closed, hidden to the tray, or minimized. This is the
    /// single entry point used by the tray "Open" item and by a relaunch of
    /// SnapX from the app launcher, so both always end with a visible,
    /// focused, foreground window.
    /// </summary>
    public static void RestoreAndFocusMainWindow()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RestoreAndFocusMainWindow);
            return;
        }

        try
        {
            // On Hyprland a minimized window lives on the special:minimized
            // workspace, which Avalonia's Show/Activate/Focus cannot leave on
            // its own. Move the SnapX window back to a real workspace first so
            // the subsequent Activate/Focus actually lands somewhere visible.
            TryRestoreHyprlandSpecialWindow();

            if (MyMainWindow is null || !MyMainWindow.IsLoaded)
            {
                var mainWindow = Design.IsDesignMode
                    ? Activator.CreateInstance<MainWindow>()
                    : Ioc.Default.GetService<MainWindow>();
                if (mainWindow is null)
                {
                    DebugHelper.WriteLine("Failed to create main window, got null back from IoC");
                    return;
                }

                MyMainWindow = mainWindow;
                WaylandAppIdentity.Attach(MyMainWindow);
                if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.MainWindow ??= MyMainWindow;
                }

                MyMainWindow.Show();
            }

            var window = MyMainWindow;
            if (window is null || !window.IsLoaded)
            {
                return;
            }

            // Order matters: a minimized window must leave the minimized state
            // before Show/Activate, otherwise some backends re-show it still
            // iconified and the activation request is dropped.
            if (window.WindowState == global::Avalonia.Controls.WindowState.Minimized)
            {
                window.WindowState = global::Avalonia.Controls.WindowState.Normal;
            }

            if (!window.IsVisible)
            {
                try
                {
                    window.Show();
                }
                catch (Exception showEx)
                {
                    DebugHelper.WriteException(showEx, "Main window could not be shown; recreating it.");
                    MyMainWindow = null;
                    RestoreAndFocusMainWindow();
                    return;
                }
            }

            window.ShowInTaskbar = true;
            window.Activate();
            window.Focus();
            ForceForegroundWindow(window);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to restore and focus the SnapX main window.");
        }
    }

    /// <summary>
    /// Moves any SnapX toplevel parked on Hyprland's special:minimized
    /// workspace back to the currently focused real workspace. On Wayland the
    /// compositor owns window placement, so this uses hyprctl directly rather
    /// than relying on Avalonia. No-ops on non-Hyprland sessions.
    /// </summary>
    private static void TryRestoreHyprlandSpecialWindow()
    {
        if (!OperatingSystem.IsLinux() || !IsHyprlandSession())
        {
            return;
        }

        try
        {
            string appClass = "io.emiliauh.SnapXL.SnapX";
            string workspace = GetHyprlandActiveWorkspaceId();
            if (string.IsNullOrWhiteSpace(workspace) || workspace == "-99" || workspace == "-98")
            {
                workspace = "1";
            }

            // Single quotes are literal in the Hyprland Lua dispatch, avoiding
            // the need to escape double quotes embedded in the C# string.
            string dispatchExpression = string.Format(
                "hl.dsp.window.move({{ workspace = '{0}', window = 'class:{1}', follow = false }})",
                workspace,
                appClass);

            if (!TryRunHyprctlCommand(dispatchExpression))
            {
                DebugHelper.WriteLine("Failed to move SnapX out of special:minimized on Hyprland.");
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to restore SnapX from Hyprland's special workspace.");
        }
    }

    private static string GetHyprlandActiveWorkspaceId()
    {
        try
        {
            ProcessResult result = RunHyprctlCommand("activeworkspace");
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
            {
                string line = result.Output.Split('\n').FirstOrDefault(l => l.StartsWith("workspace ID")) ?? "";
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[2], out int id))
                {
                    return id.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to read the Hyprland active workspace.");
        }

        return "1";
    }

    private static bool IsHyprlandSession()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")))
        {
            return true;
        }

        string desktops = string.Join(' ',
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
            Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"));
        return desktops.Contains("Hyprland", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRunHyprctlCommand(string dispatchExpression)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "hyprctl",
                    ArgumentList = { "dispatch", dispatchExpression },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to run a Hyprland dispatch.");
            return false;
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string Output, string Error);

    private static ProcessResult RunHyprctlCommand(string argument)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "hyprctl",
                ArgumentList = { argument },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw new InvalidOperationException("hyprctl did not respond within five seconds.");
        }
        return new ProcessResult(process.ExitCode, output, error);
    }

    /// <summary>
    /// Platform assist for the case Avalonia cannot cover on its own: a window
    /// restored out of the tray still loses the foreground race on Windows,
    /// and a background macOS app needs an explicit application activation.
    /// </summary>
    private static void ForceForegroundWindow(Window window)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, SW_RESTORE);
                    SetForegroundWindow(handle);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                // Avalonia's macOS backend maps Activate() onto
                // activateIgnoringOtherApps for the owning application, so a
                // second Activate after the window is visible is what pulls a
                // background SnapX to the front.
                window.Activate();
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to force the SnapX main window to the foreground.");
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void OnRelaunchWithoutCommandRequested()
    {
        RestoreAndFocusMainWindow();
    }

    [RelayCommand]
    private void OpenSnapX()
    {
        RestoreAndFocusMainWindow();
    }

    private void NativeMenu_OnNeedsUpdate(object? Sender, EventArgs E)
    {
        DebugHelper.WriteLine("NativeMenu_OnNeedsUpdate");
    }

    private void NativeMenu_OnOpening(object? Sender, EventArgs E)
    {
        DebugHelper.WriteLine("NativeMenu_OnOpening");
    }

    private static void RebuildRecordingMenu(NativeMenu recordingMenu)
    {
        recordingMenu.Items.Clear();

        bool isRecording = ScreenRecordManager.IsRecording;
        if (isRecording)
        {
            var stop = new NativeMenuItem("Stop recording");
            stop.Click += (_, _) => TaskHelpers.StopScreenRecording();
            recordingMenu.Items.Add(stop);

            if (ScreenRecordManager.IsPaused)
            {
                var resume = new NativeMenuItem("Resume recording");
                resume.Click += (_, _) => TaskHelpers.PauseScreenRecording();
                recordingMenu.Items.Add(resume);
            }
            else
            {
                var pause = new NativeMenuItem("Pause recording");
                pause.Click += (_, _) => TaskHelpers.PauseScreenRecording();
                recordingMenu.Items.Add(pause);
            }

            var abort = new NativeMenuItem("Abort recording");
            abort.Click += (_, _) => TaskHelpers.AbortScreenRecording();
            recordingMenu.Items.Add(abort);
        }
        else
        {
            var fullscreen = new NativeMenuItem("Start fullscreen recording");
            fullscreen.Click += (_, _) =>
                TaskHelpers.StartScreenRecording(
                    ScreenRecordOutput.FFmpeg,
                    ScreenRecordStartMethod.Fullscreen);
            recordingMenu.Items.Add(fullscreen);

            var region = new NativeMenuItem("Start region recording");
            region.Click += (_, _) =>
                TaskHelpers.StartScreenRecording(
                    ScreenRecordOutput.FFmpeg,
                    ScreenRecordStartMethod.Region);
            recordingMenu.Items.Add(region);

            var lastRegion = new NativeMenuItem("Start last-region recording");
            lastRegion.Click += (_, _) =>
                TaskHelpers.StartScreenRecording(
                    ScreenRecordOutput.FFmpeg,
                    ScreenRecordStartMethod.LastRegion);
            recordingMenu.Items.Add(lastRegion);
        }
    }

    private void NativeMenuItem_Open_History_OnClick()
    {
        // Show the main window and select the history tab.
        NativeMenuItem_Open_OnClick(this, EventArgs.Empty);
        if (MyMainWindow is { } window && window.DataContext is HomePageViewModel homeVm)
        {
            _ = homeVm.RefreshTasks();
        }
    }

    private static void OpenLatestHistoryItem(string type)
    {
        try
        {
            var history = TaskManager.History?.GetHistoryItems(30);
            var latest = history?.FirstOrDefault(item =>
                !string.IsNullOrEmpty(item.FilePath) &&
                string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase));
            if (latest is null || string.IsNullOrEmpty(latest.FilePath))
            {
                SendDesktopNotification("SnapX", $"No recent {type.ToLowerInvariant()} found.");
                return;
            }

            FileHelpers.OpenFile(latest.FilePath);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, $"Failed to open latest {type} from history");
        }
    }
}
