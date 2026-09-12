
// SPDX-License-Identifier: GPL-3.0-or-later


namespace SnapX.Core.ScreenCapture.ScreenRecording;

public class FFmpegCaptureDevice
{
    public string Value { get; set; }
    public string Title { get; set; }

    public FFmpegCaptureDevice(string value, string title)
    {
        Value = value;
        Title = title;
    }

    public static FFmpegCaptureDevice None { get; } = new FFmpegCaptureDevice("", "None");
    public static FFmpegCaptureDevice GDIGrab { get; } = new FFmpegCaptureDevice("gdigrab", "gdigrab (Graphics Device Interface)");
    public static FFmpegCaptureDevice X11Grab { get; } = new FFmpegCaptureDevice("x11grab", "x11grab (X11 display)");
    public static FFmpegCaptureDevice AVFoundation { get; } = new("avfoundation", "macOS screen capture (AVFoundation)");
    public static FFmpegCaptureDevice DefaultMicrophone { get; } = new("default", "Default audio input");
    public static FFmpegCaptureDevice DDAGrab { get; } = new FFmpegCaptureDevice("ddagrab", "ddagrab (Desktop Duplication API)");
    public static FFmpegCaptureDevice ScreenCaptureRecorder { get; } = new FFmpegCaptureDevice("screen-capture-recorder", "dshow (screen-capture-recorder)");
    public static FFmpegCaptureDevice VirtualAudioCapturer { get; } = new FFmpegCaptureDevice("virtual-audio-capturer", "dshow (virtual-audio-capturer)");

    public static bool IsLikelySystemAudioLoopback(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string[] indicators =
        [
            "BlackHole",
            "Loopback Audio",
            "Soundflower",
            "Background Music",
            "iShowU Audio Capture",
            "VB-Cable",
            "Sound Siphon",
            "SWB Audio Capture"
        ];
        return indicators.Any(indicator =>
            name.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }

    public static FFmpegCaptureDevice SystemAudioLoopback(string name) =>
        new(name, $"System audio — {name}");

    public override string ToString()
    {
        return Title;
    }
}
