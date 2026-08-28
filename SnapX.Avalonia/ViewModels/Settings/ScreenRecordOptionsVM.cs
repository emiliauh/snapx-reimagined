using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SnapX.Core;
using SnapX.Core.Job;
using SnapX.Core.ScreenCapture;
using SnapX.Core.ScreenCapture.ScreenRecording;

namespace SnapX.Avalonia.ViewModels.Settings;

/// <summary>
/// Settings editor for the FFmpeg screen recorder. The capture runtime reads
/// these same TaskSettingsCapture values, so edits made here are used by both
/// the screen-recorder hotkey and the interactive capture workflow.
/// </summary>
public sealed partial class ScreenRecordOptionsVM : ViewModelBase
{
    private TaskSettingsCapture Capture => (SnapXL.Settings ?? throw new InvalidOperationException("SnapX settings are not loaded.")).DefaultTaskSettings.CaptureSettings;
    private FFmpegOptions FFmpeg => Capture.FFmpegOptions ??= new();

    private string _startDelayText = string.Empty;
    private string _durationText = string.Empty;

    public IReadOnlyList<FFmpegCaptureDevice> VideoSources { get; }
    public IReadOnlyList<FFmpegCaptureDevice> AudioSources { get; }
    public FFmpegVideoCodec[] VideoCodecs { get; } = Enum.GetValues<FFmpegVideoCodec>();
    public FFmpegAudioCodec[] AudioCodecs { get; } = Enum.GetValues<FFmpegAudioCodec>();
    public FFmpegPreset[] Presets { get; } = Enum.GetValues<FFmpegPreset>();

    public int FramesPerSecond
    {
        get => Capture.ScreenRecordFPS;
        set { var next = Math.Clamp(value, 1, 240); if (Capture.ScreenRecordFPS == next) return; Capture.ScreenRecordFPS = next; OnPropertyChanged(); }
    }
    public int GifFramesPerSecond
    {
        get => Capture.GIFFPS;
        set { var next = Math.Clamp(value, 1, 60); if (Capture.GIFFPS == next) return; Capture.GIFFPS = next; OnPropertyChanged(); }
    }
    public bool ShowCursor
    {
        get => Capture.ScreenRecordShowCursor;
        set { if (Capture.ScreenRecordShowCursor == value) return; Capture.ScreenRecordShowCursor = value; OnPropertyChanged(); }
    }
    public bool AutoStart
    {
        get => Capture.ScreenRecordAutoStart;
        set { if (Capture.ScreenRecordAutoStart == value) return; Capture.ScreenRecordAutoStart = value; OnPropertyChanged(); }
    }
    public bool FixedDuration
    {
        get => Capture.ScreenRecordFixedDuration;
        set { if (Capture.ScreenRecordFixedDuration == value) return; Capture.ScreenRecordFixedDuration = value; OnPropertyChanged(); }
    }
    public bool TwoPassEncoding
    {
        get => Capture.ScreenRecordTwoPassEncoding;
        set { if (Capture.ScreenRecordTwoPassEncoding == value) return; Capture.ScreenRecordTwoPassEncoding = value; OnPropertyChanged(); }
    }
    public bool AskConfirmationOnAbort
    {
        get => Capture.ScreenRecordAskConfirmationOnAbort;
        set { if (Capture.ScreenRecordAskConfirmationOnAbort == value) return; Capture.ScreenRecordAskConfirmationOnAbort = value; OnPropertyChanged(); }
    }
    public bool TransparentRegion
    {
        get => Capture.ScreenRecordTransparentRegion;
        set { if (Capture.ScreenRecordTransparentRegion == value) return; Capture.ScreenRecordTransparentRegion = value; OnPropertyChanged(); }
    }

    public string StartDelayText
    {
        get => _startDelayText;
        set
        {
            if (!SetProperty(ref _startDelayText, value)) return;
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0)
                Capture.ScreenRecordStartDelay = number;
        }
    }
    public string DurationText
    {
        get => _durationText;
        set
        {
            if (!SetProperty(ref _durationText, value)) return;
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0)
                Capture.ScreenRecordDuration = number;
        }
    }

    public FFmpegCaptureDevice? SelectedVideoSource
    {
        get => VideoSources.FirstOrDefault(x => x.Value.Equals(FFmpeg.VideoSource, StringComparison.OrdinalIgnoreCase));
        set { FFmpeg.VideoSource = value?.Value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(RecorderSummary)); }
    }
    public FFmpegCaptureDevice? SelectedAudioSource
    {
        get => AudioSources.FirstOrDefault(x => x.Value.Equals(FFmpeg.AudioSource, StringComparison.OrdinalIgnoreCase));
        set { FFmpeg.AudioSource = value?.Value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(RecorderSummary)); }
    }
    public FFmpegVideoCodec VideoCodec
    {
        get => FFmpeg.VideoCodec;
        set { if (FFmpeg.VideoCodec == value) return; FFmpeg.VideoCodec = value; OnPropertyChanged(); OnPropertyChanged(nameof(RecorderSummary)); }
    }
    public FFmpegAudioCodec AudioCodec
    {
        get => FFmpeg.AudioCodec;
        set { if (FFmpeg.AudioCodec == value) return; FFmpeg.AudioCodec = value; OnPropertyChanged(); }
    }
    public FFmpegPreset VideoPreset
    {
        get => FFmpeg.x264_Preset;
        set { if (FFmpeg.x264_Preset == value) return; FFmpeg.x264_Preset = value; OnPropertyChanged(); }
    }
    public bool OverrideCliPath
    {
        get => FFmpeg.OverrideCLIPath;
        set { if (FFmpeg.OverrideCLIPath == value) return; FFmpeg.OverrideCLIPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(RecorderSummary)); }
    }
    public string CliPath
    {
        get => FFmpeg.CLIPath ?? string.Empty;
        set { FFmpeg.CLIPath = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(RecorderSummary)); }
    }
    public string UserArguments
    {
        get => FFmpeg.UserArgs;
        set { FFmpeg.UserArgs = value ?? string.Empty; OnPropertyChanged(); }
    }
    public bool UseCustomCommands
    {
        get => FFmpeg.UseCustomCommands;
        set { if (FFmpeg.UseCustomCommands == value) return; FFmpeg.UseCustomCommands = value; OnPropertyChanged(); }
    }
    public string CustomCommands
    {
        get => FFmpeg.CustomCommands;
        set { FFmpeg.CustomCommands = value ?? string.Empty; OnPropertyChanged(); }
    }
    public string RecorderSummary
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(FFmpeg.VideoSource) ? "no video source" : FFmpeg.VideoSource;
            var codec = FFmpeg.VideoCodec.ToString();
            return $"{source} → {codec}.{FFmpeg.Extension} at {Capture.ScreenRecordFPS} FPS";
        }
    }

    public ScreenRecordOptionsVM()
    {
        VideoSources = BuildVideoSources();
        AudioSources = BuildAudioSources();

        RefreshTextValues();
    }

    private static IReadOnlyList<FFmpegCaptureDevice> BuildVideoSources()
    {
        if (OperatingSystem.IsLinux())
        {
            // Wayland recording is driven by wf-recorder; X11 uses x11grab.
            // DirectShow-only devices are meaningless here and if selected they
            // would only produce a platform error at record time.
            return
            [
                FFmpegCaptureDevice.None,
                FFmpegCaptureDevice.X11Grab
            ];
        }

        return
        [
            FFmpegCaptureDevice.None,
            FFmpegCaptureDevice.GDIGrab,
            FFmpegCaptureDevice.DDAGrab,
            FFmpegCaptureDevice.ScreenCaptureRecorder
        ];
    }

    private static IReadOnlyList<FFmpegCaptureDevice> BuildAudioSources()
    {
        if (OperatingSystem.IsLinux())
        {
            return
            [
                FFmpegCaptureDevice.None,
                FFmpegCaptureDevice.SystemAudioMonitor,
                FFmpegCaptureDevice.PulseAudioDefault
            ];
        }

        return
        [
            FFmpegCaptureDevice.None,
            FFmpegCaptureDevice.VirtualAudioCapturer,
            FFmpegCaptureDevice.SystemAudioMonitor
        ];
    }

    [RelayCommand]
    private void SaveSettings()
    {
        SnapXL.Settings?.SaveAsync();
    }

    [RelayCommand]
    private void ResetSettings()
    {
        var defaults = new TaskSettingsCapture();
        var current = Capture;
        current.ScreenRecordFPS = defaults.ScreenRecordFPS;
        current.GIFFPS = defaults.GIFFPS;
        current.ScreenRecordShowCursor = defaults.ScreenRecordShowCursor;
        current.ScreenRecordAutoStart = defaults.ScreenRecordAutoStart;
        current.ScreenRecordStartDelay = defaults.ScreenRecordStartDelay;
        current.ScreenRecordFixedDuration = defaults.ScreenRecordFixedDuration;
        current.ScreenRecordDuration = defaults.ScreenRecordDuration;
        current.ScreenRecordTwoPassEncoding = defaults.ScreenRecordTwoPassEncoding;
        current.ScreenRecordAskConfirmationOnAbort = defaults.ScreenRecordAskConfirmationOnAbort;
        current.ScreenRecordTransparentRegion = defaults.ScreenRecordTransparentRegion;
        current.FFmpegOptions = new FFmpegOptions();
        RefreshTextValues();
        OnPropertyChanged(string.Empty);
        SaveSettings();
    }

    private void RefreshTextValues()
    {
        if (SnapXL.Settings is null) return;
        _startDelayText = Capture.ScreenRecordStartDelay.ToString(CultureInfo.InvariantCulture);
        _durationText = Capture.ScreenRecordDuration.ToString(CultureInfo.InvariantCulture);
    }
}
