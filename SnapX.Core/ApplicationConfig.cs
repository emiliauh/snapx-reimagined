using System.ComponentModel;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SnapX.Core.History;
using SnapX.Core.ImageEffects;
using SnapX.Core.Job;
using SnapX.Core.Hotkey;
using SnapX.Core.ScreenCapture;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Converters;
using SnapX.Core.Utils.Miscellaneous;
using YamlDotNet.Serialization;

namespace SnapX.Core;

public class WindowState
{
    [JsonConverter(typeof(JsonPointConverter))]
    public Point Location { get; set; }
    [JsonConverter(typeof(JsonSizeConverter))]
    public Size Size { get; set; }
    public bool IsMaximized { get; set; }
}
[YamlSerializable]
public class ApplicationConfig : SettingsBase<ApplicationConfig>
{
    public TaskSettings DefaultTaskSettings { get; set; } = new();
    public DateTime FirstTimeRunDate { get; set; } = DateTime.Now;
    public string FileUploadDefaultDirectory { get; set; } = "";
    public int NameParserAutoIncrementNumber { get; set; } = 0;
    public List<QuickTaskInfo> QuickTaskPresets
    {
        get => field;
        set => field = value ?? QuickTaskInfo.DefaultPresets;
    } = QuickTaskInfo.DefaultPresets;
    // Main window
    public bool FirstTimeMinimizeToTray { get; set; } = true;
    public List<int> TaskListViewColumnWidths { get; set; } = [];
    public int PreviewSplitterDistance { get; set; } = 335;
    public SupportedLanguage Language { get; set; } = SupportedLanguage.Automatic;
    public bool ShowTray { get; set; } = true;
    public bool SilentRun { get; set; } = false;
    public bool MacOSLoginPromptDismissed { get; set; }
    public bool MacOSPermissionSetupDismissed { get; set; }
    public bool LegacyAutomaticUploadDefaultMigrated { get; set; }
    public bool TrayIconProgressEnabled { get; set; } = true;
    public bool TaskbarProgressEnabled { get; set; } = true;
    public bool UseWhiteShareXIcon { get; set; } = false;
    public bool RememberMainFormSize { get; set; } = false;
    public bool RememberMainFormPosition { get; set; } = true;
    [JsonConverter(typeof(JsonPointConverter))]
    public Point MainFormPosition { get; set; } = Point.Empty;
    [JsonConverter(typeof(JsonSizeConverter))]
    public Size MainFormSize { get; set; } = Size.Empty;
    public HotkeyType TrayLeftClickAction { get; set; } = HotkeyType.RectangleRegion;
    public HotkeyType TrayLeftDoubleClickAction { get; set; } = HotkeyType.OpenMainWindow;
    public HotkeyType TrayMiddleClickAction { get; set; } = HotkeyType.ClipboardUploadWithContentViewer;
    public bool AutoCheckUpdate { get; set; } = true;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Release;
    // TEMP: For backward compatibility
    public bool CheckPreReleaseUpdates { get; set; } = false;
    public bool UseCustomTheme { get; set; }

    public List<Theme> Themes
    {
        get => field ??= Theme.GetDefaultThemes();
        set;
    } = null!;

    public int SelectedTheme { get; set; }
    public bool UseCustomScreenshotsPath { get; set; } = false;
    public string? CustomScreenshotsPath { get; set; } = "";
    public string? SaveImageSubFolderPattern { get; set; } = "%y-%mo";
    public string? SaveImageSubFolderPatternWindow { get; set; } = "";
    public bool ShowMenu { get; set; } = true;
    public TaskViewMode TaskViewMode { get; set; } = TaskViewMode.ThumbnailView;
    public bool ShowThumbnailTitle { get; set; } = true;
    [JsonConverter(typeof(JsonSizeConverter))]
    public Size ThumbnailSize { get; set; } = new(200, 150);
    public ThumbnailViewClickAction ThumbnailClickAction { get; set; } = ThumbnailViewClickAction.Default;
    public bool ShowColumns { get; set; } = true;
    public ImagePreviewVisibility ImagePreview { get; set; } = ImagePreviewVisibility.Automatic;
    public ImagePreviewLocation ImagePreviewLocation { get; set; } = ImagePreviewLocation.Side;
    public bool AutoCleanupBackupFiles { get; set; } = false;
    public bool AutoCleanupLogFiles { get; set; } = false;
    public int CleanupKeepFileCount { get; set; } = 10;
    public ProxyInfo ProxySettings { get; set; } = new();
    public int UploadLimit { get; set; } = 5;
    public int BufferSizePower { get; set; } = 5;
    public List<string> ClipboardContentFormats { get; set; } = [];
    public int MaxUploadFailRetry { get; set; } = 1;
    public bool UseSecondaryUploaders { get; set; } = false;
    public List<Upload.ImageDestination> SecondaryImageUploaders { get; set; } = [];
    public List<Upload.TextDestination> SecondaryTextUploaders { get; set; } = [];
    public List<Upload.FileDestination> SecondaryFileUploaders { get; set; } = [];
    public bool HistorySaveTasks { get; set; } = true;
    public bool HistoryCheckURL { get; set; } = false;
    public HistorySettings HistorySettings { get; set; } = new();
    public ImageHistorySettings ImageHistorySettings { get; set; } = new();
    public bool DontShowPrintSettingsDialog { get; set; }
    // public PrintSettings PrintSettings { get; set; }
    [JsonConverter(typeof(JsonRectangleConverter))]
    public Rectangle AutoCaptureRegion { get; set; } = Rectangle.Empty;
    public decimal AutoCaptureRepeatTime { get; set; } = 60;
    public bool AutoCaptureMinimizeToTray { get; set; } = true;
    public bool AutoCaptureWaitUpload { get; set; } = true;
    [JsonConverter(typeof(JsonRectangleConverter))]
    public Rectangle ScreenRecordRegion { get; set; } = Rectangle.Empty;
    public List<HotkeyType> ActionsToolbarList { get; set; } = [ HotkeyType.RectangleRegion, HotkeyType.PrintScreen, HotkeyType.ScreenRecorder,
        HotkeyType.None, HotkeyType.FileUpload, HotkeyType.ClipboardUploadWithContentViewer ];
    public bool ActionsToolbarRunAtStartup { get; set; } = false;
    [JsonConverter(typeof(JsonPointConverter))]
    public Point ActionsToolbarPosition { get; set; } = Point.Empty;
    public bool ActionsToolbarLockPosition { get; set; } = false;
    public bool ActionsToolbarStayTopMost { get; set; } = true;
    [Category("Application"), DefaultValue(true), Description("Use the GPU to show the user interface. This option can increase memory use.")]
    public bool HardwareAccelerated { get; set; } = true;
    public List<Color> RecentColors { get; set; } = [];
    [Category("Application"), DefaultValue(false), Description("Show file sizes in binary units, such as KiB and MiB.")]
    public bool BinaryUnits { get; set; }
    //
    [Category("Application"), DefaultValue(false), Description("Show the most recent task first in the main window.")]
    public bool ShowMostRecentTaskFirst { get; set; }
    //
    [Category("Application"), DefaultValue(false), Description("Show only edited workflows in the main window.")]
    public bool WorkflowsOnlyShowEdited { get; set; }
    //
    [Category("Application"), DefaultValue(false), Description("Expand the capture menu when you open the tray menu.")]
    public bool TrayAutoExpandCaptureMenu { get; set; }
    [Category("Application"), DefaultValue(false), Description("Do not save logs to a file.")]
    public bool DisableLogging { get; set; }

    [Category("Application"), DefaultValue(false),
     Description("Do not send anonymous crash and usage data.")]
    public bool DisableTelemetry { get; set; } = false;
    //
    [Category("Application"), DefaultValue(true), Description("Show tips and keyboard shortcuts when the task list is empty.")]
    public bool ShowMainWindowTip { get; set; }
    //
    [Category("Application"), DefaultValue(""),
     Description("Set the browser path for the SnapX browser extension.")]
    public string BrowserPath = "";
    //
    //
    [Category("Application"), DefaultValue(false),
     Description("Save settings after all active tasks are complete.")]
    public bool SaveSettingsAfterTaskCompleted { get; set; } = false;
    //
    [Category("Application"), DefaultValue(false),
     Description("Select the last completed task in the main window.")]
    public bool AutoSelectLastCompletedTask { get; set; } = false;
    //
    [Category("Application"), DefaultValue(false), Description("Enable developer functions.")]
    public bool DevMode
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
    //
    [Category("Hotkey"), DefaultValue(false), Description("Disable keyboard shortcuts.")]
    public bool DisableHotkeys { get; set; }
    [Category("Hotkey"), DefaultValue(HotkeyBackendPreference.Automatic), Description("Select the global keyboard shortcut service.")]
    public HotkeyBackendPreference HotkeyBackendPreference { get; set; } = HotkeyBackendPreference.Automatic;
    //
    [Category("Hotkey"), DefaultValue(false), Description("Disable keyboard shortcuts when the active window is full screen.")]
    public bool DisableHotkeysOnFullscreen { get; set; }
    //
    private int hotkeyRepeatLimit;
    //
    [Category("Hotkey"), DefaultValue(500), Description("Set the minimum time between repeated keyboard shortcut actions, in milliseconds.")]
    public int HotkeyRepeatLimit
    {
        get
        {
            return hotkeyRepeatLimit;
        }
        set
        {
            hotkeyRepeatLimit = Math.Max(value, 200);
        }
    }
    [Category("Integration"), DefaultValue(WaylandCaptureMode.Automatic), Description("Select the screen capture method for Wayland sessions.")]
    public WaylandCaptureMode WaylandCaptureMode { get; set; } = WaylandCaptureMode.Automatic;
    [Category("Clipboard"), DefaultValue(true), Description("Show the clipboard content before an upload from the main window.")]
    public bool ShowClipboardContentViewer { get; set; }
    //
    [Category("Image"), DefaultValue(false), Description("Remove color space information from PNG images.")]
    public bool PNGStripColorSpaceInformation { get; set; }
    //
    [Category("Image"), DefaultValue(true), Description("Use JPEG EXIF orientation data to rotate images.")]
    public bool RotateImageByExifOrientationData { get; set; }
    //
    [Category("Upload"), DefaultValue(false), Description("Disable all uploads.")]
    public bool DisableUpload { get; set; }
    //
    [Category("Upload"), DefaultValue(false), Description("Allow invalid TLS certificates during uploads.")]
    public bool AcceptInvalidSSLCertificates { get; set; }
    //
    [Category("Upload"), DefaultValue(true), Description("Do not encode emoji in upload result URLs.")]
    public bool URLEncodeIgnoreEmoji { get; set; }
    //
    [Category("Upload"), DefaultValue(true), Description("Show a warning before the first upload.")]
    public bool ShowUploadWarning { get; set; }
    //
    [Category("Upload"), DefaultValue(true), Description("Show a warning before an upload of more than 10 files.")]
    public bool ShowMultiUploadWarning { get; set; }
    //
    [Category("Upload"), DefaultValue(100), Description("Set the large file limit in MB. SnapX shows a warning before it uploads a larger file. Enter 0 to disable the warning.")]
    public int ShowLargeFileSizeWarning { get; set; }
    //
    [Category("Paths"),
     Description(
         "Set the custom uploader configuration path. If another device uses this path, make a backup before you change it.")]
    public string? CustomUploadersConfigPath { get; set; } = "";
    //
    [Category("Paths"), Description("Set the keyboard shortcut configuration path. If another device uses this path, make a backup before you change it.")]
    public string? CustomHotkeysConfigPath { get; set; } = "";
    [Category("Paths"), Description("Set a second screenshot path. SnapX uses this path when the primary path is not available. Use a local path.")]
    public string? CustomScreenshotsPath2 { get; set; } = "";
    //
    [Category("Drag and drop window"), DefaultValue(150), Description("Set the size of the drop window.")]
    public int DropSize { get; set; }

    [Category("Drag and drop window"), DefaultValue(5), Description("Set the position offset of the drop window.")]
    public int DropOffset { get; set; }
    [Category("Drag and drop window"), DefaultValue(100), Description("Set the opacity of the drop window.")]
    public int DropOpacity { get; set; }

    [Category("Drag and drop window"), DefaultValue(255), Description("Set the opacity while you drag a file to the drop window.")]
    public int DropHoverOpacity { get; set; }
    [Category("Drag and drop window"), DefaultValue(ContentAlignment.BottomRight), Description("Set the location of the drop window.")]
    public ContentAlignment DropAlignment { get; set; }

    public string? SQLitePath { get; set; }

    public bool MigrateLegacyAutomaticUploadDefault()
    {
        if (LegacyAutomaticUploadDefaultMigrated)
            return false;

        LegacyAutomaticUploadDefaultMigrated = true;

        const AfterCaptureTasks legacyDefault = AfterCaptureTasks.CopyImageToClipboard |
            AfterCaptureTasks.SaveImageToFile |
            AfterCaptureTasks.UploadImageToHost;

        if (DefaultTaskSettings.AfterCaptureJob != legacyDefault)
            return false;

        DefaultTaskSettings.AfterCaptureJob &= ~AfterCaptureTasks.UploadImageToHost;
        return true;
    }
}
