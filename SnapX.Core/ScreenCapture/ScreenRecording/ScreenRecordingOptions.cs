
// SPDX-License-Identifier: GPL-3.0-or-later


using System.Globalization;
using System.Text;
using SixLabors.ImageSharp;
#if WINDOWS
using Vortice.DXGI;
#endif

namespace SnapX.Core.ScreenCapture.ScreenRecording;

public class ScreenRecordingOptions
{
    public bool IsRecording { get; set; }
    public bool IsLossless { get; set; }
    public string InputPath { get; set; }
    public string? OutputPath { get; set; }
    public int FPS { get; set; }
    public Rectangle CaptureArea { get; set; }
    public float Duration { get; set; }
    public bool DrawCursor { get; set; }
    public FFmpegOptions FFmpeg { get; set; } = new();

    public string GetFFmpegCommands()
    {
        string commands;

        if (IsRecording && !string.IsNullOrEmpty(FFmpeg.VideoSource) &&
            FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.ScreenCaptureRecorder.Value, StringComparison.OrdinalIgnoreCase))
        {
            // https://github.com/rdp/screen-capture-recorder-to-video-windows-free
            // string registryPath = "Software\\screen-capture-recorder";
            // RegistryHelpers.CreateRegistry(registryPath, "start_x", CaptureArea.X);
            // RegistryHelpers.CreateRegistry(registryPath, "start_y", CaptureArea.Y);
            // RegistryHelpers.CreateRegistry(registryPath, "capture_width", CaptureArea.Width);
            // RegistryHelpers.CreateRegistry(registryPath, "capture_height", CaptureArea.Height);
            // RegistryHelpers.CreateRegistry(registryPath, "default_max_fps", 60);
            // RegistryHelpers.CreateRegistry(registryPath, "capture_mouse_default_1", DrawCursor ? 1 : 0);
        }

        if (!IsLossless && FFmpeg.UseCustomCommands && !string.IsNullOrEmpty(FFmpeg.CustomCommands))
        {
            commands = FFmpeg.CustomCommands.
                Replace("$fps$", FPS.ToString(), StringComparison.OrdinalIgnoreCase).
                Replace("$area_x$", CaptureArea.X.ToString(), StringComparison.OrdinalIgnoreCase).
                Replace("$area_y$", CaptureArea.Y.ToString(), StringComparison.OrdinalIgnoreCase).
                Replace("$area_width$", CaptureArea.Width.ToString(), StringComparison.OrdinalIgnoreCase).
                Replace("$area_height$", CaptureArea.Height.ToString(), StringComparison.OrdinalIgnoreCase).
                Replace("$cursor$", DrawCursor ? "1" : "0", StringComparison.OrdinalIgnoreCase).
                Replace("$duration$", Duration.ToString("0.0", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase).
                Replace("$output$", Path.ChangeExtension(OutputPath, FFmpeg.Extension), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            commands = GetFFmpegArgs();
        }

        return commands.Trim();
    }

    public string GetFFmpegArgs(bool isCustom = false)
    {
        if (IsRecording && !FFmpeg.IsVideoSourceSelected && !FFmpeg.IsAudioSourceSelected)
        {
            return null;
        }

        if (IsRecording && FFmpeg.IsVideoSourceSelected && OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Generated FFmpeg desktop-capture commands are not available on macOS. Configure custom FFmpeg commands that use avfoundation.");
        }

        StringBuilder args = new StringBuilder();

        string framerate = isCustom ? "$fps$" : FPS.ToString();

        if (IsRecording)
        {
            if (FFmpeg.IsVideoSourceSelected)
            {
                if (FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.GDIGrab.Value, StringComparison.OrdinalIgnoreCase)
                    && !OperatingSystem.IsLinux()
                    && !OperatingSystem.IsMacOS())
                {
                    if (FFmpeg.IsAudioSourceSelected)
                    {
                        AppendAudioInput(args);
                    }

                    string x = isCustom ? "$area_x$" : CaptureArea.X.ToString();
                    string y = isCustom ? "$area_y$" : CaptureArea.Y.ToString();
                    string width = isCustom ? "$area_width$" : CaptureArea.Width.ToString();
                    string height = isCustom ? "$area_height$" : CaptureArea.Height.ToString();
                    string cursor = isCustom ? "$cursor$" : DrawCursor ? "1" : "0";

                    // https://ffmpeg.org/ffmpeg-devices.html#gdigrab
                    AppendInputDevice(args, "gdigrab", false);
                    args.Append($"-framerate {framerate} ");
                    args.Append($"-offset_x {x} ");
                    args.Append($"-offset_y {y} ");
                    args.Append($"-video_size {width}x{height} ");
                    args.Append($"-draw_mouse {cursor} ");
                    args.Append("-i desktop ");
                }
                else if (FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.X11Grab.Value, StringComparison.OrdinalIgnoreCase)
                    || OperatingSystem.IsLinux()
                    && FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.GDIGrab.Value, StringComparison.OrdinalIgnoreCase))
                {
                    string x = isCustom ? "$area_x$" : CaptureArea.X.ToString(CultureInfo.InvariantCulture);
                    string y = isCustom ? "$area_y$" : CaptureArea.Y.ToString(CultureInfo.InvariantCulture);
                    string width = isCustom ? "$area_width$" : CaptureArea.Width.ToString(CultureInfo.InvariantCulture);
                    string height = isCustom ? "$area_height$" : CaptureArea.Height.ToString(CultureInfo.InvariantCulture);
                    string display = Environment.GetEnvironmentVariable("DISPLAY") ?? string.Empty;

                    if (!isCustom && string.IsNullOrWhiteSpace(display))
                    {
                        throw new PlatformNotSupportedException("X11 recording requires the DISPLAY environment variable.");
                    }

                    // The audio input must be declared before the video input
                    // so ffmpeg maps input 0 to audio and input 1 to video the
                    // same way the Windows dshow path does.
                    AppendAudioInput(args);

                    AppendInputDevice(args, "x11grab", false);
                    args.Append($"-framerate {framerate} ");
                    args.Append($"-video_size {width}x{height} ");
                    args.Append($"-draw_mouse {(DrawCursor ? 1 : 0)} ");
                    string input = isCustom
                        ? $"{display}+$area_x$,$area_y$"
                        : $"{display}{(CaptureArea.X >= 0 ? "+" : string.Empty)}{x},{y}";
                    args.Append($"-i {Core.Utils.Helpers.EscapeCLIText(input)} ");
                }
                else if (FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.DDAGrab.Value, StringComparison.OrdinalIgnoreCase))
                {
                    DdaCaptureTarget? target = TryResolveDdaCaptureTarget(CaptureArea);

                    if (target is not null)
                    {
                        // Experimental: A Windows run must confirm DDA behavior on mixed-GPU and rotated-monitor systems.
                        args.Append($"-init_hw_device d3d11va=snapx_dda:{target.AdapterIndex} ");
                        args.Append("-filter_hw_device snapx_dda ");
                    }

                    if (FFmpeg.IsAudioSourceSelected)
                    {
                        AppendAudioInput(args);
                    }

                    // https://ffmpeg.org/ffmpeg-filters.html#ddagrab
                    AppendInputDevice(args, "lavfi", false);
                    args.Append("-i ddagrab=");
                    args.Append($"output_idx={target?.OutputIndex ?? 0}:"); // Select the output on the D3D11 adapter.
                    args.Append($"draw_mouse={DrawCursor.ToString().ToLowerInvariant()}:"); // Whether to draw the mouse cursor.
                    args.Append($"framerate={framerate}:"); // Framerate at which the desktop will be captured.

                    if (target is not null)
                    {
                        args.Append($"offset_x={target.LocalRectangle.X}:");
                        args.Append($"offset_y={target.LocalRectangle.Y}:");
                        args.Append($"video_size={target.LocalRectangle.Width}x{target.LocalRectangle.Height}:");
                    }

                    args.Append("output_fmt=bgra"); // Desired filter output format.

                    if (FFmpeg.VideoCodec != FFmpegVideoCodec.h264_nvenc && FFmpeg.VideoCodec != FFmpegVideoCodec.hevc_nvenc)
                    {
                        args.Append(",hwdownload");
                        args.Append(",format=bgra");
                    }

                    args.Append(" ");
                }
                else
                {
                    // A dshow device pair only exists on Windows. On Linux the
                    // audio device belongs to the sound server, so it must be
                    // opened as its own pulse input instead of being appended
                    // to the video device specifier.
                    if (OperatingSystem.IsLinux() && FFmpeg.IsAudioSourceSelected)
                    {
                        AppendAudioInput(args);
                        AppendInputDevice(args, "dshow", false);
                        args.Append($"-framerate {framerate} ");
                        args.Append($"-i video={Core.Utils.Helpers.EscapeCLIText(FFmpeg.VideoSource)} ");
                    }
                    else
                    {
                        // https://ffmpeg.org/ffmpeg-devices.html#dshow
                        AppendInputDevice(args, "dshow", FFmpeg.IsAudioSourceSelected);
                        args.Append($"-framerate {framerate} ");
                        args.Append($"-i video={Core.Utils.Helpers.EscapeCLIText(FFmpeg.VideoSource)}");

                        if (FFmpeg.IsAudioSourceSelected)
                        {
                            args.Append($":audio={Core.Utils.Helpers.EscapeCLIText(FFmpeg.AudioSource)} ");
                        }
                        else
                        {
                            args.Append(" ");
                        }
                    }
                }
            }
            else if (FFmpeg.IsAudioSourceSelected)
            {
                AppendAudioInput(args);
            }
        }
        else
        {
            args.Append($"-i \"{InputPath}\" ");
        }

        if (!string.IsNullOrEmpty(FFmpeg.UserArgs))
        {
            args.Append(FFmpeg.UserArgs + " ");
        }

        if (FFmpeg.IsVideoSourceSelected)
        {
            if (IsLossless || FFmpeg.VideoCodec != FFmpegVideoCodec.apng)
            {
                string videoCodec;

                if (IsLossless)
                {
                    videoCodec = FFmpegVideoCodec.libx264.ToString();
                }
                else if (FFmpeg.VideoCodec == FFmpegVideoCodec.libvpx_vp9)
                {
                    videoCodec = "libvpx-vp9";
                }
                else
                {
                    videoCodec = FFmpeg.VideoCodec.ToString();
                }

                args.Append($"-c:v {videoCodec} ");
                args.Append($"-r {framerate} "); // output FPS
            }

            if (IsLossless)
            {
                args.Append($"-preset {FFmpegPreset.ultrafast} ");
                args.Append($"-tune {FFmpegTune.zerolatency} ");
                args.Append("-qp 0 ");
            }
            else
            {
                switch (FFmpeg.VideoCodec)
                {
                    case FFmpegVideoCodec.libx264: // https://trac.ffmpeg.org/wiki/Encode/H.264
                    case FFmpegVideoCodec.libx265: // https://trac.ffmpeg.org/wiki/Encode/H.265
                        args.Append($"-preset {FFmpeg.x264_Preset} ");
                        if (IsRecording) args.Append($"-tune {FFmpegTune.zerolatency} ");
                        if (FFmpeg.x264_Use_Bitrate)
                        {
                            args.Append($"-b:v {FFmpeg.x264_Bitrate}k ");
                        }
                        else
                        {
                            args.Append($"-crf {FFmpeg.x264_CRF} ");
                        }
                        args.Append("-pix_fmt yuv420p "); // -pix_fmt yuv420p required otherwise can't stream in Chrome
                        args.Append("-movflags +faststart "); // This will move some information to the beginning of your file and allow the video to begin playing before it is completely downloaded by the viewer
                        break;
                    case FFmpegVideoCodec.libvpx: // https://trac.ffmpeg.org/wiki/Encode/VP8
                    case FFmpegVideoCodec.libvpx_vp9: // https://trac.ffmpeg.org/wiki/Encode/VP9
                        if (IsRecording) args.Append("-deadline realtime ");
                        args.Append($"-b:v {FFmpeg.VPx_Bitrate}k ");
                        args.Append("-pix_fmt yuv420p "); // -pix_fmt yuv420p required otherwise causing issues in Chrome related to WebM transparency support
                        break;
                    case FFmpegVideoCodec.libxvid: // https://trac.ffmpeg.org/wiki/Encode/MPEG-4
                        args.Append($"-qscale:v {FFmpeg.XviD_QScale} ");
                        break;
                    case FFmpegVideoCodec.h264_nvenc: // https://trac.ffmpeg.org/wiki/HWAccelIntro#NVENC
                    case FFmpegVideoCodec.hevc_nvenc:
                        args.Append($"-preset {FFmpeg.NVENC_Preset} ");
                        args.Append($"-tune {FFmpeg.NVENC_Tune} ");
                        args.Append($"-b:v {FFmpeg.NVENC_Bitrate}k ");
                        args.Append("-movflags +faststart "); // This will move some information to the beginning of your file and allow the video to begin playing before it is completely downloaded by the viewer
                        break;
                    case FFmpegVideoCodec.h264_amf:
                    case FFmpegVideoCodec.hevc_amf:
                        args.Append($"-usage {FFmpeg.AMF_Usage} ");
                        args.Append($"-quality {FFmpeg.AMF_Quality} ");
                        args.Append($"-b:v {FFmpeg.AMF_Bitrate}k ");
                        args.Append("-pix_fmt yuv420p ");
                        break;
                    case FFmpegVideoCodec.h264_qsv: // https://trac.ffmpeg.org/wiki/Hardware/QuickSync
                    case FFmpegVideoCodec.hevc_qsv:
                        args.Append($"-preset {FFmpeg.QSV_Preset} ");
                        args.Append($"-b:v {FFmpeg.QSV_Bitrate}k ");
                        break;
                    case FFmpegVideoCodec.libwebp: // https://www.ffmpeg.org/ffmpeg-codecs.html#libwebp
                        args.Append("-lossless 0 ");
                        args.Append("-preset default ");
                        args.Append("-loop 0 ");
                        break;
                    case FFmpegVideoCodec.apng:
                        args.Append("-f apng ");
                        args.Append("-plays 0 ");
                        break;
                }
            }
        }

        if (FFmpeg.IsAudioSourceSelected)
        {
            // The Wayland wf-recorder path writes an AAC/MP4 intermediate.
            // Keep the final MP4 compatible with that capture path even when
            // a legacy Windows configuration still selects MP3. This is a
            // finalization-only override; it does not alter the user's saved
            // codec preference or non-Wayland recording behavior.
            FFmpegAudioCodec audioCodec = !IsRecording && OperatingSystem.IsLinux()
                && SnapX.Core.Utils.Native.LinuxAPI.IsWayland()
                ? FFmpegAudioCodec.aac
                : FFmpeg.AudioCodec;

            switch (audioCodec)
            {
                case FFmpegAudioCodec.aac:
                    args.Append($"-c:a aac -ac 2 -b:a {FFmpeg.AAC_Bitrate}k "); // -ac 2 required otherwise failing with 7.1
                    break;
                case FFmpegAudioCodec.libopus: // https://www.ffmpeg.org/ffmpeg-codecs.html#libopus-1
                    args.Append($"-c:a libopus -b:a {FFmpeg.Opus_Bitrate}k ");
                    break;
                case FFmpegAudioCodec.libvorbis: // http://trac.ffmpeg.org/wiki/TheoraVorbisEncodingGuide
                    args.Append($"-c:a libvorbis -qscale:a {FFmpeg.Vorbis_QScale} ");
                    break;
                case FFmpegAudioCodec.libmp3lame: // http://trac.ffmpeg.org/wiki/Encode/MP3
                    args.Append($"-c:a libmp3lame -qscale:a {FFmpeg.MP3_QScale} ");
                    break;
            }
        }

        if (Duration > 0)
        {
            string duration = isCustom ? "$duration$" : Duration.ToString("0.0", CultureInfo.InvariantCulture);
            args.Append($"-t {duration} "); // duration limit
        }

        args.Append("-y "); // overwrite file

        string output = isCustom ? "$output$" : Path.ChangeExtension(OutputPath, IsLossless ? "mp4" : FFmpeg.Extension);
        args.Append($"\"{output}\"");

        return args.ToString();
    }

    private sealed record DdaOutput(
        int AdapterIndex,
        int OutputIndex,
        string Name,
        Rectangle Bounds);

    private sealed record DdaCaptureTarget(
        int AdapterIndex,
        int OutputIndex,
        Rectangle LocalRectangle);

    private DdaCaptureTarget? TryResolveDdaCaptureTarget(Rectangle requested)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            List<DdaOutput> outputs = EnumerateDdaOutputs();
            if (outputs.Count == 0)
            {
                DebugHelper.WriteLine(
                    "DDA output resolution found no attached DXGI outputs; using adapter 0, output 0 without a crop.");
                return null;
            }

            int centerX = requested.X + requested.Width / 2;
            int centerY = requested.Y + requested.Height / 2;
            var candidates = outputs
                .Select(output => new
                {
                    Output = output,
                    Intersection = Rectangle.Intersect(requested, output.Bounds)
                })
                .Select(candidate => new
                {
                    candidate.Output,
                    candidate.Intersection,
                    Area = (long)Math.Max(0, candidate.Intersection.Width) *
                        Math.Max(0, candidate.Intersection.Height),
                    ContainsCenter = candidate.Output.Bounds.Contains(centerX, centerY)
                })
                .Where(candidate => candidate.Area > 0)
                .OrderByDescending(candidate => candidate.Area)
                .ThenByDescending(candidate => candidate.ContainsCenter)
                .ThenBy(candidate => candidate.Output.AdapterIndex)
                .ThenBy(candidate => candidate.Output.OutputIndex)
                .ToList();

            DdaOutput chosen;
            Rectangle clipped;
            long overlapArea;
            if (candidates.Count > 0)
            {
                chosen = candidates[0].Output;
                clipped = candidates[0].Intersection;
                overlapArea = candidates[0].Area;
            }
            else
            {
                chosen = outputs[0];
                clipped = chosen.Bounds;
                overlapArea = 0;
                DebugHelper.WriteLine(
                    $"DDA capture area {FormatDdaRectangle(requested)} did not intersect an attached output; " +
                    $"using adapter={chosen.AdapterIndex}, output={chosen.OutputIndex} ({chosen.Name}).");
            }

            Rectangle local = new(
                clipped.X - chosen.Bounds.X,
                clipped.Y - chosen.Bounds.Y,
                clipped.Width,
                clipped.Height);
            if (FFmpeg.IsEvenSizeRequired)
            {
                local.Width -= local.Width & 1;
                local.Height -= local.Height & 1;
            }

            if (local.Width <= 0 || local.Height <= 0)
            {
                throw new InvalidOperationException("The DDA crop is empty after applying encoder size constraints.");
            }

            bool wasClipped = clipped != requested || local.Size != clipped.Size;
            DebugHelper.WriteLine(
                $"DDA capture target (experimental): requested={FormatDdaRectangle(requested)}, " +
                $"adapter={chosen.AdapterIndex}, output={chosen.OutputIndex} ({chosen.Name}), " +
                $"bounds={FormatDdaRectangle(chosen.Bounds)}, overlap={FormatDdaRectangle(clipped)}, " +
                $"localCrop={FormatDdaRectangle(local)}, overlapArea={overlapArea}, clipped={wasClipped}.");

            return new DdaCaptureTarget(
                chosen.AdapterIndex,
                chosen.OutputIndex,
                local);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine(
                $"DDA output resolution failed: {ex.Message}; using adapter 0, output 0 without a crop.");
            return null;
        }
#else
        DebugHelper.WriteLine(
            "DDA output resolution is available only in Windows builds; using output 0 without a crop.");
        return null;
#endif
    }

#if WINDOWS
    private static List<DdaOutput> EnumerateDdaOutputs()
    {
        var outputs = new List<DdaOutput>();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint adapterIndex = 0;
             factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success;
             adapterIndex++)
        {
            using (adapter)
            {
                for (uint outputIndex = 0;
                     adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Success;
                     outputIndex++)
                {
                    using (output)
                    {
                        OutputDescription description = output.Description;
                        Rectangle bounds = new(
                            description.DesktopCoordinates.Left,
                            description.DesktopCoordinates.Top,
                            description.DesktopCoordinates.Right - description.DesktopCoordinates.Left,
                            description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top);
                        if (description.AttachedToDesktop && bounds.Width > 0 && bounds.Height > 0)
                        {
                            outputs.Add(new DdaOutput(
                                (int)adapterIndex,
                                (int)outputIndex,
                                description.DeviceName,
                                bounds));
                        }
                    }
                }
            }
        }

        return outputs;
    }
#endif

    private static string FormatDdaRectangle(Rectangle rectangle) =>
        $"{rectangle.X},{rectangle.Y} {rectangle.Width}x{rectangle.Height}";

    private void AppendInputDevice(StringBuilder args, string inputDevice, bool audioSource)
    {
        args.Append($"-f {inputDevice} ");
        args.Append("-thread_queue_size 1024 "); // This option sets the maximum number of queued packets when reading from the file or device.
        args.Append("-rtbufsize 256M "); // Default real time buffer size is 3041280 (3M)

        if (audioSource)
        {
            args.Append("-audio_buffer_size 80 "); // Set audio device buffer size in milliseconds (which can directly impact latency, depending on the device).
        }
    }

    /// <summary>
    /// Appends the selected audio capture device as its own FFmpeg input.
    /// The stored audio source is a portable placeholder, so it is resolved to
    /// a device name the local platform's capture backend can actually open
    /// (DirectShow on Windows, PulseAudio/PipeWire on Linux).
    /// </summary>
    private void AppendAudioInput(StringBuilder args)
    {
        if (!FFmpeg.IsAudioSourceSelected)
        {
            return;
        }

        string inputFormat = FFmpegCaptureDevice.GetAudioInputFormat();
        string audioSource = FFmpegCaptureDevice.ResolveAudioSource(FFmpeg.AudioSource);

        // -audio_buffer_size is a DirectShow-only option. Passing it to the
        // pulse demuxer makes FFmpeg reject the whole argument list, which
        // would fail the recording outright rather than just drop the audio.
        bool isPulse = inputFormat.Equals("pulse", StringComparison.Ordinal);
        AppendInputDevice(args, inputFormat, audioSource: !isPulse);

        // The pulse demuxer takes the source name directly as the input URL,
        // whereas dshow requires the "audio=" device-type prefix.
        args.Append(isPulse
            ? $"-i {Core.Utils.Helpers.EscapeCLIText(audioSource)} "
            : $"-i audio={Core.Utils.Helpers.EscapeCLIText(audioSource)} ");
    }
}
