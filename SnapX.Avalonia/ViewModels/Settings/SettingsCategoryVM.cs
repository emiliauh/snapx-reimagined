using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnapX.Core;
using SnapX.Core.Hotkey;
using SnapX.Core.Job;
using SnapX.Core.ScreenCapture;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Extensions;
using SnapX.Core.Utils.Miscellaneous;

namespace SnapX.Avalonia.ViewModels.Settings;

/// <summary>
/// Shared editor for the settings categories which do not need a service-specific
/// configuration editor.  ShareX has a large settings surface; keeping these
/// bindings in one VM makes every navigation item useful while the individual
/// controls still write to the real ApplicationConfig/TaskSettings objects.
/// </summary>
public sealed partial class SettingsCategoryVM : ViewModelBase
{
    private ApplicationConfig Config => SnapXL.Settings ?? throw new InvalidOperationException("SnapX settings are not loaded.");
    private TaskSettings Task => Config.DefaultTaskSettings;
    private TaskSettingsImage Image => Task.ImageSettings;
    private TaskSettingsCapture Capture => Task.CaptureSettings;
    private TaskSettingsUpload Upload => Task.UploadSettings;
    private RegionCaptureOptions Region => Capture.SurfaceOptions;
    private OCROptions OCR => Capture.OCROptions;
    private ProxyInfo Proxy => Config.ProxySettings;

    private string _pageKey = string.Empty;
    private string _categoryKey = string.Empty;
    private string _pageTitle = "Settings";
    private string _pageDescription = "Configure SnapX for the way you capture, edit, and upload.";
    private string _jpegQualityText = string.Empty;
    private string _thumbnailWidthText = string.Empty;
    private string _thumbnailHeightText = string.Empty;
    private string _screenshotDelayText = string.Empty;
    private string _ocrScaleText = string.Empty;
    private string _maxUploadRetryText = string.Empty;
    private string _largeFileWarningText = string.Empty;
    private string _uploadLimitText = string.Empty;
    private string _proxyPortText = string.Empty;
    private string _hotkeyRepeatLimitText = string.Empty;
    private IReadOnlyList<HotkeyEditorRow> _hotkeyRows = [];

    public string PageKey => _pageKey;
    public string CategoryKey => _categoryKey;
    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }
    public string PageDescription
    {
        get => _pageDescription;
        private set => SetProperty(ref _pageDescription, value);
    }

    public bool IsApplicationUploadPage => _pageKey == "Upload" &&
        (_categoryKey.Equals("Application", StringComparison.OrdinalIgnoreCase) || _categoryKey.Length == 0);
    public bool IsApplicationPage =>
        (_categoryKey.Equals("Application", StringComparison.OrdinalIgnoreCase) && !IsApplicationUploadPage) ||
        (_categoryKey.Length == 0 && (_pageKey is "Application" or "General" or "Theme" or "Paths" or "Housekeeping" or "View" or "History" or "Print" or "Proxy" or "Advanced"));
    public bool IsTaskPage => _categoryKey.Equals("Tasks", StringComparison.OrdinalIgnoreCase) ||
        (_categoryKey.Length == 0 && (_pageKey is "Tasks" or "Image" or "Effects" or "Thumbnail" or "Capture" or "Region" or "OCR" or "FileNaming" or "Clipboard" or "Filters" or "Actions" or "WatchFolders" or "Tools"));
    public bool IsHotkeyPage => _pageKey.Equals("Hotkeys", StringComparison.OrdinalIgnoreCase);
    public bool IsImagePage => _pageKey is "Image" or "Effects" or "Thumbnail";
    public bool IsEffectsPage => _pageKey == "Effects";
    public bool IsThumbnailPage => _pageKey == "Thumbnail";
    public bool IsCapturePage => _pageKey is "Capture" or "Region";
    public bool IsRegionPage => _pageKey == "Region";
    public bool IsOcrPage => _pageKey == "OCR";
    public bool IsTaskUploadPage => _categoryKey.Equals("Tasks", StringComparison.OrdinalIgnoreCase) &&
        _pageKey is "Upload" or "FileNaming" or "Clipboard" or "Filters";
    public bool IsHistoryPage => _pageKey == "History";
    public bool IsViewPage => _pageKey == "View";
    public bool IsProxyPage => _pageKey == "Proxy";
    public bool IsAdvancedPage => _pageKey == "Advanced";
    public bool IsWatchFolderPage => _pageKey == "WatchFolders";
    public bool IsActionsPage => _pageKey == "Actions";
    public bool IsToolsPage => _pageKey == "Tools";
    public bool IsIntegrationPage => _pageKey == "Integration";
    public bool IsInformationPage => _pageKey is "Destinations" or "Print" or "Housekeeping" or "ConfigFolder";
    public bool IsConfigFolderPage => _pageKey == "ConfigFolder";
    public bool IsWaylandSession
    {
        get
        {
            string? sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            return OperatingSystem.IsLinux() &&
                (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase) ||
                 (!string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase) &&
                  !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))));
        }
    }
    public bool IsElevated => SnapXL.IsAdmin;
    public string SessionDescription => IsWaylandSession
        ? "Wayland session detected. SnapX uses desktop portals where available. On Hyprland, shortcuts from the settings page are also kept in a SnapX-managed section of your user bindings file; no administrator access is needed."
        : "Wayland is not active. SnapX uses the platform capture and shortcut path.";
    public string HotkeyPlatformHelp => IsWaylandSession
        ? "On Hyprland, Apply also writes a clearly marked user binding in ~/.config/hypr/bindings.lua and reloads it. Clear removes only SnapX's managed entry. No administrator access is needed. Other Wayland desktops use the portal."
        : "X11 shortcuts are registered directly with the active X server. Apply grabs the key and Clear releases it.";
    public string ElevationStatus { get; private set; } =
        "Administrator mode is optional. It does not bypass Wayland portal permissions.";

    public string ConfigFolderPath => SnapXL.ConfigFolder;
    public string PersonalFolderPath => SnapXL.PersonalFolder;

    public SupportedLanguage[] Languages { get; } = Enum.GetValues<SupportedLanguage>();
    public UpdateChannel[] UpdateChannels { get; } = Enum.GetValues<UpdateChannel>();
    public EImageFormat[] ImageFormats { get; } = Enum.GetValues<EImageFormat>();
    public ThumbnailViewClickAction[] ThumbnailActions { get; } = Enum.GetValues<ThumbnailViewClickAction>();
    public ProxyMethod[] ProxyMethods { get; } = Enum.GetValues<ProxyMethod>();
    public IReadOnlyList<WaylandCaptureMode> WaylandCaptureModes { get; } = GetWaylandCaptureModes();
    public IReadOnlyList<HotkeyBackendPreference> HotkeyBackendPreferences { get; } = GetHotkeyBackendPreferences();

    public SupportedLanguage SelectedLanguage
    {
        get => Config.Language;
        set { if (Config.Language == value) return; Config.Language = value; OnPropertyChanged(); SaveSettings(); }
    }
    public UpdateChannel SelectedUpdateChannel
    {
        get => Config.UpdateChannel;
        set { if (Config.UpdateChannel == value) return; Config.UpdateChannel = value; OnPropertyChanged(); SaveSettings(); }
    }
    public EImageFormat ImageFormat
    {
        get => Image.ImageFormat;
        set { if (Image.ImageFormat == value) return; Image.ImageFormat = value; OnPropertyChanged(); SaveSettings(); }
    }
    public ThumbnailViewClickAction ThumbnailAction
    {
        get => Config.ThumbnailClickAction;
        set { if (Config.ThumbnailClickAction == value) return; Config.ThumbnailClickAction = value; OnPropertyChanged(); SaveSettings(); }
    }
    public ProxyMethod SelectedProxyMethod
    {
        get => Proxy.ProxyMethod;
        set { if (Proxy.ProxyMethod == value) return; Proxy.ProxyMethod = value; OnPropertyChanged(); SaveSettings(); }
    }
    public WaylandCaptureMode SelectedWaylandCaptureMode
    {
        get => Config.WaylandCaptureMode;
        set
        {
            if (!WaylandCaptureModes.Contains(value)) value = WaylandCaptureMode.Automatic;
            if (Config.WaylandCaptureMode == value) return;
            Config.WaylandCaptureMode = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }
    public HotkeyBackendPreference SelectedHotkeyBackendPreference
    {
        get => Config.HotkeyBackendPreference;
        set
        {
            if (Config.HotkeyBackendPreference == value) return;
            Config.HotkeyBackendPreference = value;
            if (value == HotkeyBackendPreference.Disabled)
                Config.DisableHotkeys = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeysEnabled));
            SaveSettings();
        }
    }

    public bool ShowTray { get => Config.ShowTray; set => SetApplicationBool(Config.ShowTray, x => Config.ShowTray = x, value, nameof(ShowTray)); }
    public bool SilentRun { get => Config.SilentRun; set => SetApplicationBool(Config.SilentRun, x => Config.SilentRun = x, value, nameof(SilentRun)); }
    public bool TrayIconProgressEnabled { get => Config.TrayIconProgressEnabled; set => SetApplicationBool(Config.TrayIconProgressEnabled, x => Config.TrayIconProgressEnabled = x, value, nameof(TrayIconProgressEnabled)); }
    public bool TaskbarProgressEnabled { get => Config.TaskbarProgressEnabled; set => SetApplicationBool(Config.TaskbarProgressEnabled, x => Config.TaskbarProgressEnabled = x, value, nameof(TaskbarProgressEnabled)); }
    public bool RememberMainWindowPosition { get => Config.RememberMainFormPosition; set => SetApplicationBool(Config.RememberMainFormPosition, x => Config.RememberMainFormPosition = x, value, nameof(RememberMainWindowPosition)); }
    public bool RememberMainWindowSize { get => Config.RememberMainFormSize; set => SetApplicationBool(Config.RememberMainFormSize, x => Config.RememberMainFormSize = x, value, nameof(RememberMainWindowSize)); }
    public bool AutoCheckUpdate { get => Config.AutoCheckUpdate; set => SetApplicationBool(Config.AutoCheckUpdate, x => Config.AutoCheckUpdate = x, value, nameof(AutoCheckUpdate)); }
    public bool UseCustomTheme { get => Config.UseCustomTheme; set => SetApplicationBool(Config.UseCustomTheme, x => Config.UseCustomTheme = x, value, nameof(UseCustomTheme)); }
    public bool DisableTelemetry { get => Config.DisableTelemetry; set => SetApplicationBool(Config.DisableTelemetry, x => Config.DisableTelemetry = x, value, nameof(DisableTelemetry)); }
    public bool DisableLogging { get => Config.DisableLogging; set => SetApplicationBool(Config.DisableLogging, x => Config.DisableLogging = x, value, nameof(DisableLogging)); }
    public bool HardwareAccelerated { get => Config.HardwareAccelerated; set => SetApplicationBool(Config.HardwareAccelerated, x => Config.HardwareAccelerated = x, value, nameof(HardwareAccelerated)); }
    public bool BinaryUnits { get => Config.BinaryUnits; set => SetApplicationBool(Config.BinaryUnits, x => Config.BinaryUnits = x, value, nameof(BinaryUnits)); }
    public bool ShowMostRecentTaskFirst { get => Config.ShowMostRecentTaskFirst; set => SetApplicationBool(Config.ShowMostRecentTaskFirst, x => Config.ShowMostRecentTaskFirst = x, value, nameof(ShowMostRecentTaskFirst)); }
    public bool WorkflowsOnlyShowEdited { get => Config.WorkflowsOnlyShowEdited; set => SetApplicationBool(Config.WorkflowsOnlyShowEdited, x => Config.WorkflowsOnlyShowEdited = x, value, nameof(WorkflowsOnlyShowEdited)); }
    public bool TrayAutoExpandCaptureMenu { get => Config.TrayAutoExpandCaptureMenu; set => SetApplicationBool(Config.TrayAutoExpandCaptureMenu, x => Config.TrayAutoExpandCaptureMenu = x, value, nameof(TrayAutoExpandCaptureMenu)); }
    public bool ShowMenu { get => Config.ShowMenu; set => SetApplicationBool(Config.ShowMenu, x => Config.ShowMenu = x, value, nameof(ShowMenu)); }
    public bool ShowMainWindowTip { get => Config.ShowMainWindowTip; set => SetApplicationBool(Config.ShowMainWindowTip, x => Config.ShowMainWindowTip = x, value, nameof(ShowMainWindowTip)); }
    public bool SaveSettingsAfterTaskCompleted { get => Config.SaveSettingsAfterTaskCompleted; set => SetApplicationBool(Config.SaveSettingsAfterTaskCompleted, x => Config.SaveSettingsAfterTaskCompleted = x, value, nameof(SaveSettingsAfterTaskCompleted)); }
    public bool AutoSelectLastCompletedTask { get => Config.AutoSelectLastCompletedTask; set => SetApplicationBool(Config.AutoSelectLastCompletedTask, x => Config.AutoSelectLastCompletedTask = x, value, nameof(AutoSelectLastCompletedTask)); }

    public bool ShowThumbnailTitle { get => Config.ShowThumbnailTitle; set => SetApplicationBool(Config.ShowThumbnailTitle, x => Config.ShowThumbnailTitle = x, value, nameof(ShowThumbnailTitle)); }
    public bool ShowColumns { get => Config.ShowColumns; set => SetApplicationBool(Config.ShowColumns, x => Config.ShowColumns = x, value, nameof(ShowColumns)); }
    public bool AutoCleanupBackupFiles { get => Config.AutoCleanupBackupFiles; set => SetApplicationBool(Config.AutoCleanupBackupFiles, x => Config.AutoCleanupBackupFiles = x, value, nameof(AutoCleanupBackupFiles)); }
    public bool AutoCleanupLogFiles { get => Config.AutoCleanupLogFiles; set => SetApplicationBool(Config.AutoCleanupLogFiles, x => Config.AutoCleanupLogFiles = x, value, nameof(AutoCleanupLogFiles)); }
    public bool HistorySaveTasks { get => Config.HistorySaveTasks; set => SetApplicationBool(Config.HistorySaveTasks, x => Config.HistorySaveTasks = x, value, nameof(HistorySaveTasks)); }
    public bool HistoryCheckUrl { get => Config.HistoryCheckURL; set => SetApplicationBool(Config.HistoryCheckURL, x => Config.HistoryCheckURL = x, value, nameof(HistoryCheckUrl)); }

    public bool DisableUpload { get => Config.DisableUpload; set => SetApplicationBool(Config.DisableUpload, x => Config.DisableUpload = x, value, nameof(DisableUpload)); }
    public bool UrlEncodeIgnoreEmoji { get => Config.URLEncodeIgnoreEmoji; set => SetApplicationBool(Config.URLEncodeIgnoreEmoji, x => Config.URLEncodeIgnoreEmoji = x, value, nameof(UrlEncodeIgnoreEmoji)); }
    public bool ShowUploadWarning { get => Config.ShowUploadWarning; set => SetApplicationBool(Config.ShowUploadWarning, x => Config.ShowUploadWarning = x, value, nameof(ShowUploadWarning)); }
    public bool ShowMultiUploadWarning { get => Config.ShowMultiUploadWarning; set => SetApplicationBool(Config.ShowMultiUploadWarning, x => Config.ShowMultiUploadWarning = x, value, nameof(ShowMultiUploadWarning)); }
    public string UploadLimitText
    {
        get => _uploadLimitText;
        set
        {
            if (!SetProperty(ref _uploadLimitText, value)) return;
            SetApplicationInt(value, number => Config.UploadLimit = number, 1, 1000);
        }
    }

    public bool UseDefaultAfterCaptureJob { get => Task.UseDefaultAfterCaptureJob; set => SetTaskBool(Task.UseDefaultAfterCaptureJob, x => Task.UseDefaultAfterCaptureJob = x, value, nameof(UseDefaultAfterCaptureJob)); }
    public bool UseDefaultAfterUploadJob { get => Task.UseDefaultAfterUploadJob; set => SetTaskBool(Task.UseDefaultAfterUploadJob, x => Task.UseDefaultAfterUploadJob = x, value, nameof(UseDefaultAfterUploadJob)); }
    public bool UseDefaultDestinations { get => Task.UseDefaultDestinations; set => SetTaskBool(Task.UseDefaultDestinations, x => Task.UseDefaultDestinations = x, value, nameof(UseDefaultDestinations)); }
    public bool UseDefaultImageSettings { get => Task.UseDefaultImageSettings; set => SetTaskBool(Task.UseDefaultImageSettings, x => Task.UseDefaultImageSettings = x, value, nameof(UseDefaultImageSettings)); }
    public bool UseDefaultCaptureSettings { get => Task.UseDefaultCaptureSettings; set => SetTaskBool(Task.UseDefaultCaptureSettings, x => Task.UseDefaultCaptureSettings = x, value, nameof(UseDefaultCaptureSettings)); }
    public bool UseDefaultUploadSettings { get => Task.UseDefaultUploadSettings; set => SetTaskBool(Task.UseDefaultUploadSettings, x => Task.UseDefaultUploadSettings = x, value, nameof(UseDefaultUploadSettings)); }
    public bool UseDefaultActions { get => Task.UseDefaultActions; set => SetTaskBool(Task.UseDefaultActions, x => Task.UseDefaultActions = x, value, nameof(UseDefaultActions)); }
    public bool UseDefaultToolsSettings { get => Task.UseDefaultToolsSettings; set => SetTaskBool(Task.UseDefaultToolsSettings, x => Task.UseDefaultToolsSettings = x, value, nameof(UseDefaultToolsSettings)); }
    public bool UseDefaultAdvancedSettings { get => Task.UseDefaultAdvancedSettings; set => SetTaskBool(Task.UseDefaultAdvancedSettings, x => Task.UseDefaultAdvancedSettings = x, value, nameof(UseDefaultAdvancedSettings)); }

    public bool ImageAutoUseJpeg { get => Image.ImageAutoUseJPEG; set => SetTaskImageBool(Image.ImageAutoUseJPEG, x => Image.ImageAutoUseJPEG = x, value, nameof(ImageAutoUseJpeg)); }
    public bool ImageAutoJpegQuality { get => Image.ImageAutoJPEGQuality; set => SetTaskImageBool(Image.ImageAutoJPEGQuality, x => Image.ImageAutoJPEGQuality = x, value, nameof(ImageAutoJpegQuality)); }
    public bool ShowImageEffectsWindowAfterCapture { get => Image.ShowImageEffectsWindowAfterCapture; set => SetTaskImageBool(Image.ShowImageEffectsWindowAfterCapture, x => Image.ShowImageEffectsWindowAfterCapture = x, value, nameof(ShowImageEffectsWindowAfterCapture)); }
    public bool ImageEffectOnlyRegionCapture { get => Image.ImageEffectOnlyRegionCapture; set => SetTaskImageBool(Image.ImageEffectOnlyRegionCapture, x => Image.ImageEffectOnlyRegionCapture = x, value, nameof(ImageEffectOnlyRegionCapture)); }
    public bool UseRandomImageEffect { get => Image.UseRandomImageEffect; set => SetTaskImageBool(Image.UseRandomImageEffect, x => Image.UseRandomImageEffect = x, value, nameof(UseRandomImageEffect)); }
    public bool ThumbnailCheckSize { get => Image.ThumbnailCheckSize; set => SetTaskImageBool(Image.ThumbnailCheckSize, x => Image.ThumbnailCheckSize = x, value, nameof(ThumbnailCheckSize)); }
    public string ThumbnailName { get => Image.ThumbnailName; set { Image.ThumbnailName = value ?? string.Empty; OnPropertyChanged(); } }

    public bool ShowCursor { get => Capture.ShowCursor; set => SetCaptureBool(Capture.ShowCursor, x => Capture.ShowCursor = x, value, nameof(ShowCursor)); }
    public bool CaptureTransparent { get => Capture.CaptureTransparent; set => SetCaptureBool(Capture.CaptureTransparent, x => Capture.CaptureTransparent = x, value, nameof(CaptureTransparent)); }
    public bool CaptureShadow { get => Capture.CaptureShadow; set => SetCaptureBool(Capture.CaptureShadow, x => Capture.CaptureShadow = x, value, nameof(CaptureShadow)); }
    public bool CaptureClientArea { get => Capture.CaptureClientArea; set => SetCaptureBool(Capture.CaptureClientArea, x => Capture.CaptureClientArea = x, value, nameof(CaptureClientArea)); }
    public bool CaptureAutoHideTaskbar { get => Capture.CaptureAutoHideTaskbar; set => SetCaptureBool(Capture.CaptureAutoHideTaskbar, x => Capture.CaptureAutoHideTaskbar = x, value, nameof(CaptureAutoHideTaskbar)); }
    public bool QuickCrop { get => Region.QuickCrop; set => SetRegionBool(Region.QuickCrop, x => Region.QuickCrop = x, value, nameof(QuickCrop)); }
    public bool DetectWindows { get => Region.DetectWindows; set => SetRegionBool(Region.DetectWindows, x => Region.DetectWindows = x, value, nameof(DetectWindows)); }
    public bool DetectControls { get => Region.DetectControls; set => SetRegionBool(Region.DetectControls, x => Region.DetectControls = x, value, nameof(DetectControls)); }
    public bool UseDimming { get => Region.UseDimming; set => SetRegionBool(Region.UseDimming, x => Region.UseDimming = x, value, nameof(UseDimming)); }
    public bool UseCustomInfoText { get => Region.UseCustomInfoText; set => SetRegionBool(Region.UseCustomInfoText, x => Region.UseCustomInfoText = x, value, nameof(UseCustomInfoText)); }
    public bool ShowInfo { get => Region.ShowInfo; set => SetRegionBool(Region.ShowInfo, x => Region.ShowInfo = x, value, nameof(ShowInfo)); }
    public bool ShowMagnifier { get => Region.ShowMagnifier; set => SetRegionBool(Region.ShowMagnifier, x => Region.ShowMagnifier = x, value, nameof(ShowMagnifier)); }
    public bool ShowCrosshair { get => Region.ShowCrosshair; set => SetRegionBool(Region.ShowCrosshair, x => Region.ShowCrosshair = x, value, nameof(ShowCrosshair)); }
    public bool EnableAnimations { get => Region.EnableAnimations; set => SetRegionBool(Region.EnableAnimations, x => Region.EnableAnimations = x, value, nameof(EnableAnimations)); }
    public bool IsFixedSize { get => Region.IsFixedSize; set => SetRegionBool(Region.IsFixedSize, x => Region.IsFixedSize = x, value, nameof(IsFixedSize)); }

    public bool SingleLineOcr { get => OCR.SingleLine; set => SetOcrBool(OCR.SingleLine, x => OCR.SingleLine = x, value, nameof(SingleLineOcr)); }
    public bool SilentOcr { get => OCR.Silent; set => SetOcrBool(OCR.Silent, x => OCR.Silent = x, value, nameof(SilentOcr)); }
    public bool AutoCopyOcr { get => OCR.AutoCopy; set => SetOcrBool(OCR.AutoCopy, x => OCR.AutoCopy = x, value, nameof(AutoCopyOcr)); }
    public bool CloseOcrWindowAfterLink { get => OCR.CloseWindowAfterOpeningServiceLink; set => SetOcrBool(OCR.CloseWindowAfterOpeningServiceLink, x => OCR.CloseWindowAfterOpeningServiceLink = x, value, nameof(CloseOcrWindowAfterLink)); }

    public bool FileUploadUseNamePattern { get => Upload.FileUploadUseNamePattern; set => SetUploadBool(Upload.FileUploadUseNamePattern, x => Upload.FileUploadUseNamePattern = x, value, nameof(FileUploadUseNamePattern)); }
    public bool FileUploadReplaceProblematicCharacters { get => Upload.FileUploadReplaceProblematicCharacters; set => SetUploadBool(Upload.FileUploadReplaceProblematicCharacters, x => Upload.FileUploadReplaceProblematicCharacters = x, value, nameof(FileUploadReplaceProblematicCharacters)); }
    public bool UrlRegexReplace { get => Upload.URLRegexReplace; set => SetUploadBool(Upload.URLRegexReplace, x => Upload.URLRegexReplace = x, value, nameof(UrlRegexReplace)); }
    public bool ClipboardUploadUrlContents { get => Upload.ClipboardUploadURLContents; set => SetUploadBool(Upload.ClipboardUploadURLContents, x => Upload.ClipboardUploadURLContents = x, value, nameof(ClipboardUploadUrlContents)); }
    public bool ClipboardUploadShortenUrl { get => Upload.ClipboardUploadShortenURL; set => SetUploadBool(Upload.ClipboardUploadShortenURL, x => Upload.ClipboardUploadShortenURL = x, value, nameof(ClipboardUploadShortenUrl)); }
    public bool ClipboardUploadShareUrl { get => Upload.ClipboardUploadShareURL; set => SetUploadBool(Upload.ClipboardUploadShareURL, x => Upload.ClipboardUploadShareURL = x, value, nameof(ClipboardUploadShareUrl)); }
    public bool ClipboardUploadAutoIndexFolder { get => Upload.ClipboardUploadAutoIndexFolder; set => SetUploadBool(Upload.ClipboardUploadAutoIndexFolder, x => Upload.ClipboardUploadAutoIndexFolder = x, value, nameof(ClipboardUploadAutoIndexFolder)); }

    public bool WatchFolderEnabled
    {
        get => Task.WatchFolderEnabled;
        set { if (Task.WatchFolderEnabled == value) return; Task.WatchFolderEnabled = value; OnPropertyChanged(); SaveSettings(); }
    }

    public string NameFormatPattern
    {
        get => Upload.NameFormatPattern ?? string.Empty;
        set { Upload.NameFormatPattern = value ?? string.Empty; OnPropertyChanged(); }
    }
    public string NameFormatPatternActiveWindow
    {
        get => Upload.NameFormatPatternActiveWindow ?? string.Empty;
        set { Upload.NameFormatPatternActiveWindow = value ?? string.Empty; OnPropertyChanged(); }
    }
    public string UrlRegexReplacePattern { get => Upload.URLRegexReplacePattern; set { Upload.URLRegexReplacePattern = value ?? string.Empty; OnPropertyChanged(); } }
    public string UrlRegexReplaceReplacement { get => Upload.URLRegexReplaceReplacement; set { Upload.URLRegexReplaceReplacement = value ?? string.Empty; OnPropertyChanged(); } }
    public string RegionCustomInfoText { get => Region.CustomInfoText; set { Region.CustomInfoText = value ?? string.Empty; OnPropertyChanged(); } }
    public string OcrLanguage { get => OCR.Language; set { OCR.Language = value ?? "en"; OnPropertyChanged(); } }
    public string ConfigNote => IsConfigFolderPage ? $"Configuration files are stored in {ConfigFolderPath}." :
        "Changes are applied to the active default task and saved when you press Save settings.";

    public string JpegQualityText
    {
        get => _jpegQualityText;
        set { if (SetProperty(ref _jpegQualityText, value)) SetInt(value, x => Image.ImageJPEGQuality = x, 1, 100); }
    }
    public string ThumbnailWidthText
    {
        get => _thumbnailWidthText;
        set { if (SetProperty(ref _thumbnailWidthText, value)) SetInt(value, x => Image.ThumbnailWidth = x, 1, 10000); }
    }
    public string ThumbnailHeightText
    {
        get => _thumbnailHeightText;
        set { if (SetProperty(ref _thumbnailHeightText, value)) SetInt(value, x => Image.ThumbnailHeight = x, 0, 10000); }
    }
    public string ScreenshotDelayText
    {
        get => _screenshotDelayText;
        set { if (SetProperty(ref _screenshotDelayText, value) && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0) Capture.ScreenshotDelay = number; }
    }
    public string OcrScaleText
    {
        get => _ocrScaleText;
        set { if (SetProperty(ref _ocrScaleText, value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0) OCR.ScaleFactor = number; }
    }
    public string MaxUploadRetryText
    {
        get => _maxUploadRetryText;
        set { if (SetProperty(ref _maxUploadRetryText, value)) SetApplicationInt(value, x => Config.MaxUploadFailRetry = x, 0, 20); }
    }
    public string LargeFileWarningText
    {
        get => _largeFileWarningText;
        set { if (SetProperty(ref _largeFileWarningText, value)) SetApplicationInt(value, x => Config.ShowLargeFileSizeWarning = x, 0, 100000); }
    }
    public string ProxyPortText
    {
        get => _proxyPortText;
        set { if (SetProperty(ref _proxyPortText, value)) SetInt(value, x => Proxy.Port = x, 0, 65535); }
    }
    public string HotkeyRepeatLimitText
    {
        get => _hotkeyRepeatLimitText;
        set { if (SetProperty(ref _hotkeyRepeatLimitText, value) && int.TryParse(value, out var number)) Config.HotkeyRepeatLimit = Math.Max(200, number); }
    }

    public string ProxyHost { get => Proxy.Host; set { Proxy.Host = value ?? string.Empty; OnPropertyChanged(); } }
    public string ProxyUsername { get => Proxy.Username; set { Proxy.Username = value ?? string.Empty; OnPropertyChanged(); } }
    public string ProxyPassword { get => Proxy.Password; set { Proxy.Password = value ?? string.Empty; OnPropertyChanged(); } }

    public bool HotkeysEnabled
    {
        get => !Config.DisableHotkeys;
        set
        {
            if (Config.DisableHotkeys == !value) return;
            Config.DisableHotkeys = !value;
            if (value && Config.HotkeyBackendPreference == HotkeyBackendPreference.Disabled)
            {
                Config.HotkeyBackendPreference = HotkeyBackendPreference.Automatic;
                OnPropertyChanged(nameof(SelectedHotkeyBackendPreference));
            }
            OnPropertyChanged();
            SaveSettings();
        }
    }
    public bool DisableHotkeysOnFullscreen { get => Config.DisableHotkeysOnFullscreen; set => SetApplicationBool(Config.DisableHotkeysOnFullscreen, x => Config.DisableHotkeysOnFullscreen = x, value, nameof(DisableHotkeysOnFullscreen)); }

    public IReadOnlyList<HotkeyEditorRow> HotkeyRows => _hotkeyRows;

    public async Task ApplyHotkeyAsync(HotkeyEditorRow? row)
    {
        if (row?.Setting.HotkeyInfo is null || SnapXL.HotkeysConfig is null)
        {
            DebugHelper.WriteLine(
                $"ApplyHotkeyAsync: aborted early (row null: {row is null}, HotkeyInfo null: {row?.Setting.HotkeyInfo is null}, HotkeysConfig null: {SnapXL.HotkeysConfig is null}).");
            return;
        }
        DebugHelper.WriteLine($"ApplyHotkeyAsync: parsing '{row.ShortcutText}' for job {row.Setting.TaskSettings?.Job}.");
        if (!HotkeyParser.TryParse(row.ShortcutText, out var key, out var win, out var error))
        {
            DebugHelper.WriteLine($"ApplyHotkeyAsync: parse failed - {error}");
            row.ErrorText = error;
            return;
        }

        DebugHelper.WriteLine($"ApplyHotkeyAsync: parsed to {key} (Win={win}). Applying.");
        row.SetError(string.Empty);
        Keys previousKey = row.Setting.HotkeyInfo.Hotkey;
        bool previousWin = row.Setting.HotkeyInfo.Win;
        string requestedShortcutText = row.ShortcutText;
        row.SetHotkey(key, win);
        HyprlandHotkeySyncResult hyprlandResult = await HyprlandHotkeyBindingManager.ApplyAsync(row.Setting);
        if (!hyprlandResult.IsSuccess)
        {
            // The bindings file still has the previous stable-ID entry after a
            // failed validation. Keep the in-memory model in sync with it so a
            // later generic settings save cannot persist an unapplied shortcut.
            row.SetHotkey(previousKey, previousWin);
            row.ShortcutText = requestedShortcutText;
            row.SetError(hyprlandResult.Message ?? "Could not apply the Hyprland hotkey.");
            return;
        }
        DebugHelper.WriteLine($"ApplyHotkeyAsync: Setting.HotkeyInfo.Hotkey is now {row.Setting.HotkeyInfo.Hotkey} immediately after SetHotkey.");
        await SnapXL.ReloadHotkeysAsync().ConfigureAwait(true);
        DebugHelper.WriteLine(
            $"ApplyHotkeyAsync: Setting.HotkeyInfo.Hotkey is now {row.Setting.HotkeyInfo.Hotkey} (Status={row.Setting.HotkeyInfo.Status}) after ReloadHotkeysAsync.");
        SettingManager.SaveHotkeysConfigAsync();
        DebugHelper.WriteLine($"ApplyHotkeyAsync: Setting.HotkeyInfo.Hotkey is now {row.Setting.HotkeyInfo.Hotkey} after SaveHotkeysConfigAsync.");
        Config.SaveAsync();
        RefreshHotkeyRows();
    }

    public async Task ClearHotkeyAsync(HotkeyEditorRow? row)
    {
        if (row?.Setting.HotkeyInfo is null || SnapXL.HotkeysConfig is null) return;
        HyprlandHotkeySyncResult hyprlandResult = await HyprlandHotkeyBindingManager.ClearAsync(row.Setting);
        if (!hyprlandResult.IsSuccess)
        {
            row.SetError(hyprlandResult.Message ?? "Could not clear the Hyprland hotkey.");
            return;
        }
        row.SetHotkey(Keys.None, false);
        row.SetError(string.Empty);
        await SnapXL.ReloadHotkeysAsync().ConfigureAwait(true);
        SettingManager.SaveHotkeysConfigAsync();
        Config.SaveAsync();
        RefreshHotkeyRows();
    }

    [RelayCommand]
    private void LaunchElevated()
    {
        if (SnapXL.IsAdmin)
        {
            ElevationStatus = "SnapX already runs with administrator rights.";
        }
        else if (!TaskHelpers.TryRunShareXAsAdmin(out var message))
        {
            ElevationStatus = message;
        }
        else
        {
            ElevationStatus = message + " Wayland permissions can still block capture or shortcuts.";
        }

        OnPropertyChanged(nameof(ElevationStatus));
    }

    public SettingsCategoryVM()
    {
        RefreshTextValues();
        RefreshHotkeyRows();
    }

    public void Configure(string? categoryTag, string destinationTag)
    {
        _categoryKey = categoryTag ?? string.Empty;
        _pageKey = destinationTag.TrimStart('!');
        PageTitle = Humanize(_pageKey);
        PageDescription = DescriptionFor(_pageKey);
        RefreshTextValues();
        NormalizePlatformPreferences();
        RefreshHotkeyRows();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void SaveSettings()
    {
        Config.SaveAsync();
        if (IsHotkeyPage)
        {
            SettingManager.SaveHotkeysConfigAsync();
            _ = SnapXL.ReloadHotkeysAsync();
        }
    }

    [RelayCommand]
    private async Task ResetHotkeys()
    {
        if (SnapXL.HotkeysConfig is null) return;
        foreach (HotkeySettings previous in SnapXL.HotkeysConfig.Hotkeys)
        {
            HyprlandHotkeySyncResult result = await HyprlandHotkeyBindingManager.ClearAsync(previous);
            if (!result.IsSuccess)
            {
                DebugHelper.WriteException(result.Message ?? "Could not remove a managed Hyprland hotkey.");
            }
        }
        SnapXL.HotkeysConfig.Hotkeys = HotkeyManager.GetDefaultHotkeyList();
        foreach (HotkeySettings setting in SnapXL.HotkeysConfig.Hotkeys)
        {
            HyprlandHotkeySyncResult result = await HyprlandHotkeyBindingManager.ApplyAsync(setting);
            if (!result.IsSuccess)
            {
                DebugHelper.WriteException(result.Message ?? "Could not apply a default Hyprland hotkey.");
            }
        }
        await SnapXL.ReloadHotkeysAsync();
        SettingManager.SaveHotkeysConfigAsync();
        RefreshHotkeyRows();
    }

    [RelayCommand]
    private void OpenConfigFolder() => FileHelpers.OpenFolder(ConfigFolderPath);

    private void RefreshTextValues()
    {
        if (SnapXL.Settings is null) return;
        _jpegQualityText = Image.ImageJPEGQuality.ToString(CultureInfo.InvariantCulture);
        _thumbnailWidthText = Image.ThumbnailWidth.ToString(CultureInfo.InvariantCulture);
        _thumbnailHeightText = Image.ThumbnailHeight.ToString(CultureInfo.InvariantCulture);
        _screenshotDelayText = Capture.ScreenshotDelay.ToString(CultureInfo.InvariantCulture);
        _ocrScaleText = OCR.ScaleFactor.ToString(CultureInfo.InvariantCulture);
        _maxUploadRetryText = Config.MaxUploadFailRetry.ToString(CultureInfo.InvariantCulture);
        _largeFileWarningText = Config.ShowLargeFileSizeWarning.ToString(CultureInfo.InvariantCulture);
        _uploadLimitText = Config.UploadLimit.ToString(CultureInfo.InvariantCulture);
        _proxyPortText = Proxy.Port.ToString(CultureInfo.InvariantCulture);
        _hotkeyRepeatLimitText = Config.HotkeyRepeatLimit.ToString(CultureInfo.InvariantCulture);
    }

    private void RefreshHotkeyRows()
    {
        var settings = SnapXL.HotkeysConfig?.Hotkeys ?? [];
        var previous = _hotkeyRows;
        _hotkeyRows = settings.Select(setting =>
        {
            var row = previous.FirstOrDefault(candidate => ReferenceEquals(candidate.Setting, setting));
            if (row is null) return new HotkeyEditorRow(setting);
            row.RefreshStatus();
            return row;
        }).ToArray();
        OnPropertyChanged(nameof(HotkeyRows));
    }

    private static IReadOnlyList<WaylandCaptureMode> GetWaylandCaptureModes()
    {
        if (!OperatingSystem.IsLinux()) return [WaylandCaptureMode.Automatic];
        return IsWaylandEnvironment()
            ? Enum.GetValues<WaylandCaptureMode>()
            : [WaylandCaptureMode.Automatic, WaylandCaptureMode.X11Fallback];
    }

    private static IReadOnlyList<HotkeyBackendPreference> GetHotkeyBackendPreferences()
    {
        var values = new List<HotkeyBackendPreference> { HotkeyBackendPreference.Automatic };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            if (IsWaylandEnvironment())
            {
                // A Wayland session often exposes DISPLAY for Xwayland too,
                // but X11 grabs there cannot reliably see native Wayland
                // keyboard input. Keep the portal as the only explicit Linux
                // choice in this session.
                values.Add(HotkeyBackendPreference.WaylandPortal);
            }
            else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                values.Add(HotkeyBackendPreference.X11);
            }
        }

        values.Add(HotkeyBackendPreference.Disabled);

        return values;
    }

    private void NormalizePlatformPreferences()
    {
        if (SnapXL.Settings is null) return;

        if (!WaylandCaptureModes.Contains(Config.WaylandCaptureMode))
            Config.WaylandCaptureMode = WaylandCaptureMode.Automatic;

        if (Config.HotkeyBackendPreference != HotkeyBackendPreference.Disabled &&
            !HotkeyBackendPreferences.Contains(Config.HotkeyBackendPreference))
        {
            Config.HotkeyBackendPreference = HotkeyBackendPreference.Automatic;
        }
    }

    private static bool IsWaylandEnvironment()
    {
        string? sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        return string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase) ||
            (!string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase) &&
             !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));
    }

    private void SetApplicationBool(bool current, Action<bool> setter, bool value, string propertyName)
    {
        if (current == value) return;
        setter(value);
        OnPropertyChanged(propertyName);
        SaveSettings();
    }

    private void SetTaskBool(bool current, Action<bool> setter, bool value, string propertyName)
    {
        if (current == value) return;
        setter(value);
        OnPropertyChanged(propertyName);
        SaveSettings();
    }

    private void SetTaskImageBool(bool current, Action<bool> setter, bool value, string propertyName)
    {
        if (current == value) return;
        setter(value);
        OnPropertyChanged(propertyName);
        SaveSettings();
    }

    private void SetCaptureBool(bool current, Action<bool> setter, bool value, string propertyName)
    {
        if (current == value) return;
        setter(value);
        OnPropertyChanged(propertyName);
        SaveSettings();
    }

    private void SetRegionBool(bool current, Action<bool> setter, bool value, string propertyName)
    {
        if (current == value) return;
        setter(value);
        OnPropertyChanged(propertyName);
        SaveSettings();
    }

    private void SetOcrBool(bool current, Action<bool> setter, bool value, string propertyName)
    {
        if (current == value) return;
        setter(value);
        OnPropertyChanged(propertyName);
        SaveSettings();
    }

    private void SetUploadBool(bool current, Action<bool> setter, bool value, string propertyName)
    {
        if (current == value) return;
        setter(value);
        OnPropertyChanged(propertyName);
        SaveSettings();
    }

    private void SetInt(string value, Action<int> setter, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return;
        setter(Math.Clamp(number, minimum, maximum));
        SaveSettings();
    }

    private void SetApplicationInt(string value, Action<int> setter, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return;
        setter(Math.Clamp(number, minimum, maximum));
        SaveSettings();
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Settings";
        return value switch
        {
            "OCR" => "OCR",
            "FileNaming" => "File naming",
            "WatchFolders" => "Watch folders",
            "ConfigFolder" => "Configuration folder",
            _ => string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()))
        };
    }

    private static string DescriptionFor(string value) => value switch
    {
        "Hotkeys" => "View and change the global shortcuts.",
        "Region" => "Set region detection, selection help, magnifier, and annotation options.",
        "OCR" => "Set the OCR language, scale, and result options.",
        "FileNaming" => "Set file-name patterns and URL replacement rules.",
        "Clipboard" => "Set clipboard upload, URL shortening, sharing, and folder index options.",
        "Filters" => "Set upload limits and upload rules.",
        "ScreenRecordOptions" => "Set the screen recorder and FFmpeg options.",
        "Integration" => "Set the Wayland capture and global shortcut backends.",
        "ConfigFolder" => "View the folder that contains SnapX configuration files.",
        _ => "Set the SnapX options for this workflow."
    };

    public sealed class HotkeyEditorRow : ObservableObject
    {
        public HotkeySettings Setting { get; }
        public string Action => Setting.TaskSettings?.Job.GetLocalizedDescription() ?? "Unassigned";
        public string Status => Setting.HotkeyInfo?.Status switch
        {
            HotkeyStatus.Registered => "Registered",
            HotkeyStatus.Failed => "Unavailable",
            _ => "Not configured"
        };
        public string StatusMessage => Setting.HotkeyInfo?.StatusMessage ?? string.Empty;

        private string _shortcutText;
        private string _errorText = string.Empty;

        public string ShortcutText
        {
            get => _shortcutText;
            set => SetProperty(ref _shortcutText, value);
        }
        public string ErrorText
        {
            get => _errorText;
            set => SetProperty(ref _errorText, value);
        }

        public HotkeyEditorRow(HotkeySettings setting)
        {
            Setting = setting;
            _shortcutText = setting.HotkeyInfo?.ToString() ?? string.Empty;
        }

        public void SetHotkey(Keys key, bool win)
        {
            if (Setting.HotkeyInfo is null) Setting.HotkeyInfo = new HotkeyInfo();
            Setting.HotkeyInfo.Hotkey = key;
            Setting.HotkeyInfo.Win = win;
            ShortcutText = Setting.HotkeyInfo.ToString();
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusMessage));
        }

        public void SetCapturedShortcut(Keys key, bool win)
        {
            ShortcutText = new HotkeyInfo(key) { Win = win }.ToString();
            SetError(string.Empty);
        }

        public void RefreshStatus()
        {
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusMessage));
        }

        public void SetError(string message) => ErrorText = message;
    }
}
