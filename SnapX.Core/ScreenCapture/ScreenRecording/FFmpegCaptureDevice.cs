
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
    public static FFmpegCaptureDevice DDAGrab { get; } = new FFmpegCaptureDevice("ddagrab", "ddagrab (Desktop Duplication API)");
    public static FFmpegCaptureDevice ScreenCaptureRecorder { get; } = new FFmpegCaptureDevice("screen-capture-recorder", "dshow (screen-capture-recorder)");
    public static FFmpegCaptureDevice VirtualAudioCapturer { get; } = new FFmpegCaptureDevice("virtual-audio-capturer", "dshow (virtual-audio-capturer)");

    /// <summary>
    /// The default PulseAudio/PipeWire capture source. On PipeWire this is the
    /// default recording source (normally a microphone), so it is intentionally
    /// labelled as such and never used for desktop audio.
    /// </summary>
    public static FFmpegCaptureDevice PulseAudioDefault { get; } = new FFmpegCaptureDevice("default", "Microphone (pulse default source)");

    /// <summary>
    /// The monitor of the default output sink. This captures the audio being
    /// played by the desktop/system instead of the microphone. PipeWire's
    /// PulseAudio compatibility layer expands the token at connect time, so it
    /// follows the user's current output device without hardcoding a name.
    /// </summary>
    public static FFmpegCaptureDevice SystemAudioMonitor { get; } = new FFmpegCaptureDevice("@DEFAULT_MONITOR@", "System audio (default output monitor)");

    /// <summary>
    /// The audio source stored in settings is a portable placeholder, but the
    /// name FFmpeg can actually open is platform specific: Windows needs a
    /// DirectShow device friendly name, Linux needs a PulseAudio/PipeWire
    /// source name. "virtual-audio-capturer" only exists as a DirectShow
    /// filter on Windows, so on Linux it must be mapped to a source the local
    /// sound server can open, otherwise FFmpeg fails to open the input and the
    /// recording silently ends up with no audio track.
    /// </summary>
    public static string ResolveAudioSource(string? audioSource)
    {
        if (string.IsNullOrWhiteSpace(audioSource))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsLinux())
        {
            return audioSource;
        }

        // The Windows "virtual-audio-capturer" placeholder means desktop/system
        // loopback, not a microphone. A bare PulseAudio device name silently
        // falls back to the default source on PipeWire, so it must be mapped to
        // the default sink's monitor explicitly.
        return audioSource.Equals(VirtualAudioCapturer.Value, StringComparison.OrdinalIgnoreCase)
            ? SystemAudioMonitor.Value
            : audioSource;
    }

    /// <summary>
    /// The FFmpeg input format used to open <see cref="ResolveAudioSource"/>.
    /// </summary>
    public static string GetAudioInputFormat() => OperatingSystem.IsLinux() ? "pulse" : "dshow";

    public override string ToString()
    {
        return Title;
    }
}
