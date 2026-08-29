using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Media;
using FluentAvalonia.UI.Windowing;
using SixLabors.ImageSharp;
using SnapX.Avalonia.ViewModels;
using SnapX.Avalonia.Views.Controls;
using SnapX.Core;
using SnapX.Core.Job;
using SnapX.Core.Upload;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Native;
using SnapX.Core.Utils.Cryptographic;
using Color = Avalonia.Media.Color;
using Size = SixLabors.ImageSharp.Size;
namespace SnapX.Avalonia.Views;

public partial class MainWindow : FAAppWindow
{
    public static string MainWindowName => Core.SnapXL.Title + " " + Core.SnapXL.VersionText;

    public static string LogoResourcePath =>
        OperatingSystem.IsWindows() ? "/Assets/SnapX_Icon.ico" : "avares://snapx-ui/SnapX_Logo.png";

    public MainWindow(MainViewModel vm)
    {
        DataContext = vm;

        // Avalonia ToolTips are Popup-backed. A history card's declared
        // ToolTip.Tip can therefore remain as a native xdg_popup while a
        // region capture hands focus to slurp. The native Wayland EGL popup
        // surface is then redrawn on return and can fail eglMakeCurrent on
        // NVIDIA/Hyprland. Disable automatic tooltips for this window before
        // its content/template is materialized; the attached property is
        // inherited by MainView, FANavigationView and all history cards.
        // Context menus and explicit Flyouts are unaffected.
        if (OperatingSystem.IsLinux() && LinuxAPI.IsWayland())
        {
            ToolTip.SetServiceEnabled(this, false);
        }

        var config = App.SnapX.GetConfiguration();
        if (config.RememberMainFormSize && !config.MainFormSize.IsEmpty)
        {
            Width = config.MainFormSize.Width;
            Height = config.MainFormSize.Height;
        }
        else
        {
            var activeScreen = Screens.ScreenFromWindow(this);
            var screenWidth = activeScreen?.Bounds.Width ?? 1920;
            var screenHeight = activeScreen?.Bounds.Height ?? 1080;
            Width = screenWidth / 2.07;
            Height = screenHeight / 2.2;
            if (config.RememberMainFormSize)
            {
                config.MainFormSize = new Size((int)Width, (int)Height);
            }
        }

        if (config.RememberMainFormPosition && !config.MainFormPosition.IsEmpty &&
            CaptureHelpers.GetScreenBounds()
                .IntersectsWith(new Rectangle(config.MainFormPosition, config.MainFormSize)))
        {
            Position = new PixelPoint(config.MainFormPosition.X, config.MainFormPosition.Y);
        }

        InitializeComponent();
        ListenForEvents();
    }

    public MainWindow() : this(new MainViewModel())
    {
    }

    public void ListenForEvents()
    {
        Core.SnapXL.EventAggregator.Subscribe<NeedFileOpenerEvent>(HandleFileSelectionRequested);
        Core.SnapXL.EventAggregator.Subscribe<NeedMainWindowHandle>(HandleMainWindowHandleRequested);

        void HandleMainWindowHandleRequested(NeedMainWindowHandle Obj)
        {
            Obj.ResultHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
    }

    private async void HandleFileSelectionRequested(NeedFileOpenerEvent @event)
    {
        var topLevel = GetTopLevel(this);
        IEnumerable<IStorageItem> items;

        if (@event.FolderPicker)
        {
            items = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = @event.Title,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(@event.Directory),
                AllowMultiple = @event.Multiselect,
                SuggestedFileName = @event.FileName,
            });
        }
        else
        {
            items = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = @event.Title,
                AllowMultiple = @event.Multiselect,
                SuggestedFileName = @event.FileName,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(@event.Directory),
                FileTypeFilter = @event.AcceptedExtensions is { Count: > 0 }
                    ? [new FilePickerFileType("Accepted files") { Patterns = @event.AcceptedExtensions }]
                    : null
            });
        }

        if (items.Any())
        {
            var itemPaths = items.Select(item => item.Path.LocalPath).ToArray();
            var itemPathsAsString = string.Join(", ", itemPaths);
            DebugHelper.WriteLine(itemPathsAsString);

            if (@event.IndexFolder)
            {
                UploadManager.IndexFolder(itemPaths.FirstOrDefault(), @event.TaskSettings);
            }
            else if (@event.HashCheck)
            {
                string? filePath = itemPaths.FirstOrDefault(File.Exists);
                if (!string.IsNullOrEmpty(filePath))
                {
                    var checker = new HashChecker();
                    string? checksum = await checker.Start(filePath, HashType.SHA256);
                    if (!string.IsNullOrEmpty(checksum))
                    {
                        Core.SnapXL.EventAggregator.Publish(
                            new NeedClipboardCopyEvent($"{checksum}  {Path.GetFileName(filePath)}"));
                    }
                }
            }
            else if (@event.VideoThumbnailer)
            {
                foreach (string filePath in itemPaths.Where(File.Exists))
                {
                    try
                    {
                        await Task.Run(() => TaskHelpers.CreateVideoThumbnails(filePath, @event.TaskSettings));
                    }
                    catch (Exception ex)
                    {
                        DebugHelper.WriteException(ex, $"Unable to create video thumbnails for {filePath}");
                        Core.SnapXL.EventAggregator.Publish(new ErrorMessageEvent(ex, "Video thumbnail generation failed", true));
                    }
                }
            }
            else if (@event.VideoConverter)
            {
                foreach (string filePath in itemPaths.Where(File.Exists))
                {
                    try
                    {
                        string outputPath = await Task.Run(() => TaskHelpers.ConvertVideo(filePath, @event.TaskSettings));
                        ToastNotificationWindow.ShowToast(
                            null,
                            "Video converted",
                            outputPath,
                            () => FileHelpers.OpenFile(outputPath));
                    }
                    catch (Exception ex)
                    {
                        DebugHelper.WriteException(ex, $"Unable to convert video {filePath}");
                        Core.SnapXL.EventAggregator.Publish(new ErrorMessageEvent(ex, "Video conversion failed", true));
                    }
                }
            }
            else if (@event.PinToScreen)
            {
                string? filePath = itemPaths.FirstOrDefault(File.Exists);
                if (!string.IsNullOrEmpty(filePath))
                {
                    await Task.Run(() =>
                    {
                        SixLabors.ImageSharp.Image image = SixLabors.ImageSharp.Image.Load(filePath);
                        // PublishPinToScreen clones the image and hands ownership
                        // of that clone to the event, so this caller can dispose
                        // its local copy immediately after publishing.
                        TaskHelpers.PublishPinToScreen(image, @event.TaskSettings);
                        image.Dispose();
                    });
                }
            }
            else
            {
                UploadManager.UploadFile(itemPaths, @event.TaskSettings);
            }
        }
        else
        {
            DebugHelper.WriteLine("Got no files/folders back!");
        }
    }


    // Event handler for the button click
    private void OnDemoTestButtonClick(object sender, RoutedEventArgs e)
    {
        DebugHelper.WriteLine("Upload Demo Image triggered");

        // try
        // {
        //     var imageUrl = ImageURLTextBox.Text ?? ImageURLTextBox.Watermark;
        //     UploadManager.DownloadAndUploadFile(imageUrl!);
        // }
        // catch (Exception ex)
        // {
        //     DebugHelper.Logger.Error(ex.ToString());
        // }
    }

    private void ApplicationActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (IsWindows11 && ActualThemeVariant != FluentAvaloniaTheme.HighContrastTheme)
        {
            TryEnableMicaEffect();
        }
        else if (ActualThemeVariant != FluentAvaloniaTheme.HighContrastTheme)
        {
            SetValue(BackgroundProperty, AvaloniaProperty.UnsetValue);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        Application.Current!.ActualThemeVariantChanged += ApplicationActualThemeVariantChanged;
        var thm = ActualThemeVariant;
        if (IsWindows11 && thm != FluentAvaloniaTheme.HighContrastTheme)
        {
            TransparencyBackgroundFallback = Brushes.Transparent;
            TransparencyLevelHint = new[]
                { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None };

            TryEnableMicaEffect();
        }

        TaskManager.InitHistoryManager();
    }

    private void TryEnableMicaEffect()
    {
        if (ActualThemeVariant == ThemeVariant.Dark)
        {
            var color = this.TryFindResource("SolidBackgroundFillColorBase",
                ThemeVariant.Dark, out var value)
                ? (Color2)(Color)value!
                : new Color2(32, 32, 32);

            color = color.LightenPercent(-0.8f);

            Background = new ImmutableSolidColorBrush(color, 0.78);
        }
        else if (ActualThemeVariant == ThemeVariant.Light)
        {
            // Similar effect here
            var color = this.TryFindResource("SolidBackgroundFillColorBase",
                ThemeVariant.Light, out var value)
                ? (Color2)(Color)value!
                : new Color2(243, 243, 243);

            color = color.LightenPercent(0.5f);

            Background = new ImmutableSolidColorBrush(color, 0.9);
        }
    }

    private void TopLevel_OnOpened(object? Sender, EventArgs E)
    {
        DebugHelper.WriteLine("MainWindow Opened");
        // FAContentDialog is hosted through Avalonia's popup/overlay path.
        // Do not create that additional transient surface when this top-level
        // is remapped in a native Wayland session.
        if (OperatingSystem.IsLinux() && LinuxAPI.IsWayland()) return;

        if (Core.SnapXL.Settings.FirstTimeRunDate != DateTime.MinValue &&
            Core.SnapXL.Settings.FirstTimeRunDate != null) return;
        var changelogDialog = new FAContentDialog
        {
            Title = Title,
            Content = new ChangelogControl()
        };
        changelogDialog.ShowAsync(this);
        // changelogWindow.LostFocus += (_, _) => changelogWindow.CloseButtonCommand.Execute(null);
        // PointerEntered += (_, _) => changelogWindow.CloseButtonCommand.Execute(null);
        // GotFocus += (_, _) => changelogWindow.CloseButtonCommand.Execute(null);
    }
}
