using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SnapX.Core.CLI;
using SnapX.Core.Utils.Extensions;

namespace SnapX.Core.Media;

public class FFmpegCLIManager : ExternalCLIManager
{
    public const int x264_min = 0;
    public const int x264_max = 51;
    public const int x265_min = 0;
    public const int x265_max = 51;
    public const int vp8_min = 4;
    public const int vp8_max = 63;
    public const int vp9_min = 0;
    public const int vp9_max = 63;
    public const int av1_min = 0;
    public const int av1_max = 63;
    public const int xvid_min = 1;
    public const int xvid_max = 31;
    public const int mp3_min = 0;
    public const int mp3_max = 9;

    public delegate void EncodeStartedEventHandler();
    public event EncodeStartedEventHandler EncodeStarted;

    public delegate void EncodeProgressChangedEventHandler(float percentage);
    public event EncodeProgressChangedEventHandler EncodeProgressChanged;

    public string? FFmpegPath { get; private set; }
    public StringBuilder Output { get; private set; }
    public bool IsEncoding { get; set; }
    public bool ShowError { get; set; }
    public bool StopRequested { get; set; }
    public bool TrackEncodeProgress { get; set; }
    public TimeSpan VideoDuration { get; set; }
    public TimeSpan EncodeTime { get; set; }
    public float EncodePercentage { get; set; }

    private int closeTryCount = 0;

    public FFmpegCLIManager(string? ffmpegPath)
    {
        FFmpegPath = ffmpegPath;
        Output = new StringBuilder();
        OutputDataReceived += FFmpeg_DataReceived;
        ErrorDataReceived += FFmpeg_DataReceived;
    }

    public bool Run(string args)
    {
        return Run(FFmpegPath, args);
    }

    protected bool Run(string? path, string args)
    {
        StopRequested = false;
        int errorCode = Open(path, args);
        IsEncoding = false;
        bool result = errorCode == 0;
        if (!result && ShowError)
        {
            DebugHelper.WriteLine($"FFmpeg Error: {errorCode}");
        }
        return result;
    }

    public override void Close()
    {
        StopRequested = true;

        if (IsProcessRunning && process != null)
        {
            if (closeTryCount >= 2)
            {
                process.Kill();
            }
            else
            {
                WriteInput("q");

                closeTryCount++;
            }
        }
    }

    /// <summary>
    /// Force-stops the current encoder after its normal <c>q</c> shutdown path
    /// has exceeded a bounded grace period. This is reserved for application
    /// shutdown so a stuck child cannot outlive SnapX.
    /// </summary>
    public void ForceClose()
    {
        StopRequested = true;

        try
        {
            if (IsProcessRunning && process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or SystemException)
        {
            DebugHelper.WriteException(ex, "Unable to force-stop FFmpeg");
        }
    }

    private void FFmpeg_DataReceived(object sender, DataReceivedEventArgs e)
    {
        lock (this)
        {
            string data = e.Data;

            if (!string.IsNullOrEmpty(data))
            {
                Output.AppendLine(data);

                if (!IsEncoding && data.Contains("Press [q] to stop", StringComparison.OrdinalIgnoreCase))
                {
                    IsEncoding = true;

                    OnEncodeStarted();
                }

                if (TrackEncodeProgress)
                {
                    UpdateEncodeProgress(data);
                }
            }
        }
    }

    private void UpdateEncodeProgress(string data)
    {
        if (VideoDuration.Ticks == 0)
        {
            //  Duration: 00:00:15.32, start: 0.000000, bitrate: 1095 kb/s
            Match match = Regex.Match(data, @"Duration:\s*(\d+:\d+:\d+\.\d+),\s*start:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (match.Success && TimeSpan.TryParse(match.Groups[1].Value, out TimeSpan duration))
            {
                VideoDuration = duration;
            }
        }
        else
        {
            //frame=  942 fps=187 q=35.0 size=    3072kB time=00:00:38.10 bitrate= 660.5kbits/s speed=7.55x
            Match match = Regex.Match(data, @"time=\s*(\d+:\d+:\d+\.\d+)\s*bitrate=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (match.Success && TimeSpan.TryParse(match.Groups[1].Value, out TimeSpan time))
            {
                EncodeTime = time;
                EncodePercentage = ((float)EncodeTime.Ticks / VideoDuration.Ticks) * 100;

                OnEncodeProgressChanged(EncodePercentage);
            }
        }
    }

    protected void OnEncodeStarted()
    {
        EncodeStarted?.Invoke();
    }

    protected void OnEncodeProgressChanged(float percentage)
    {
        EncodeProgressChanged?.Invoke(percentage);
    }

    public VideoInfo GetVideoInfo(string videoPath)
    {
        VideoInfo videoInfo = new VideoInfo();
        videoInfo.FilePath = videoPath;

        Run($"-i \"{videoPath}\"");
        string output = Output.ToString();

        Match matchInput = Regex.Match(output, @"Duration: (?<Duration>\d{2}:\d{2}:\d{2}\.\d{2}),.+?start: (?<Start>\d+\.\d+),.+?bitrate: (?<Bitrate>\d+) kb/s",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (matchInput.Success)
        {
            videoInfo.Duration = TimeSpan.Parse(matchInput.Groups["Duration"].Value);
            //videoInfo.Start = TimeSpan.Parse(match.Groups["Start"].Value);
            videoInfo.Bitrate = int.Parse(matchInput.Groups["Bitrate"].Value);
        }
        else
        {
            return null;
        }

        Match matchVideoStream = Regex.Match(output, @"Stream #\d+:\d+(?:\(.+?\))?: Video: (?<Codec>.+?) \(.+?,.+?, (?<Width>\d+)x(?<Height>\d+).+?, (?<FPS>\d+(?:\.\d+)?) fps",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (matchVideoStream.Success)
        {
            videoInfo.VideoCodec = matchVideoStream.Groups["Codec"].Value;
            videoInfo.VideoResolution = new Size(int.Parse(matchVideoStream.Groups["Width"].Value), int.Parse(matchVideoStream.Groups["Height"].Value));
            videoInfo.VideoFPS = double.Parse(matchVideoStream.Groups["FPS"].Value, CultureInfo.InvariantCulture);
        }

        Match matchAudioStream = Regex.Match(output, @"Stream #\d+:\d+(?:\(.+?\))?: Audio: (?<Codec>.+?)(?: \(|,)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (matchAudioStream.Success)
        {
            videoInfo.AudioCodec = matchAudioStream.Groups["Codec"].Value;
        }

        return videoInfo;
    }

    public Devices GetDirectShowDevices()
    {
        var devices = new Devices();

        Run("-list_devices true -f dshow -i dummy");

        string output = Output.ToString();
        string[] lines = output.Lines();
        bool isAudio = false;
        Regex regex = new Regex(@"\[dshow @ \w+\] +""(.+)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        foreach (string line in lines)
        {
            if (line.Contains("] DirectShow video devices"))
            {
                isAudio = false;
                continue;
            }
            else if (line.Contains("] DirectShow audio devices"))
            {
                isAudio = true;
                continue;
            }

            Match match = regex.Match(line);

            if (match.Success)
            {
                if (line.EndsWith("\" (video)"))
                {
                    isAudio = false;
                }
                else if (line.EndsWith("\" (audio)"))
                {
                    isAudio = true;
                }

                string deviceName = match.Groups[1].Value;

                if (isAudio)
                {
                    devices.AudioDevices.Add(deviceName);
                }
                else
                {
                    devices.VideoDevices.Add(deviceName);
                }
            }
        }

        return devices;
    }

    public async Task<IReadOnlyList<string>> GetAVFoundationAudioDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(FFmpegPath))
        {
            return [];
        }

        using var discovery = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        discovery.StartInfo.ArgumentList.Add("-hide_banner");
        discovery.StartInfo.ArgumentList.Add("-f");
        discovery.StartInfo.ArgumentList.Add("avfoundation");
        discovery.StartInfo.ArgumentList.Add("-list_devices");
        discovery.StartInfo.ArgumentList.Add("true");
        discovery.StartInfo.ArgumentList.Add("-i");
        discovery.StartInfo.ArgumentList.Add(string.Empty);

        if (!discovery.Start())
        {
            throw new IOException("Unable to start FFmpeg audio-device discovery.");
        }

        Task<string> stdout = discovery.StandardOutput.ReadToEndAsync();
        Task<string> stderr = discovery.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await discovery.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!discovery.HasExited) discovery.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The discovery process exited between the status check and kill.
            }
            await discovery.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException("FFmpeg audio-device discovery timed out after 5 seconds.");
        }

        string output = await stdout.ConfigureAwait(false) + Environment.NewLine +
            await stderr.ConfigureAwait(false);
        // FFmpeg deliberately returns a non-zero code after listing devices
        // because no capture was requested. The parsed section is the result.
        return ParseAVFoundationAudioDevices(output);
    }

    internal static IReadOnlyList<string> ParseAVFoundationAudioDevices(string output)
    {
        var devices = new List<string>();
        bool audioSection = false;
        foreach (string line in output.Lines())
        {
            if (line.Contains("AVFoundation audio devices:", StringComparison.OrdinalIgnoreCase))
            {
                audioSection = true;
                continue;
            }
            if (audioSection && line.Contains("AVFoundation video devices:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (!audioSection) continue;

            Match match = Regex.Match(
                line,
                @"\]\s+\[\d+\]\s+(?<name>.+?)\s*$",
                RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            string name = match.Groups["name"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name) &&
                !devices.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                devices.Add(name);
            }
        }
        return devices;
    }

    public void ConcatenateVideos(string[] inputFiles, string outputFile, bool autoDeleteInputFiles = false)
    {
        string listFile = outputFile + ".txt";
        string contents = string.Join(Environment.NewLine, inputFiles.Select(inputFile => $"file '{inputFile}'"));
        File.WriteAllText(listFile, contents);

        try
        {
            bool result = Run($"-f concat -safe 0 -i \"{listFile}\" -c copy \"{outputFile}\"");

            if (result && autoDeleteInputFiles)
            {
                foreach (string inputFile in inputFiles)
                {
                    if (File.Exists(inputFile))
                    {
                        File.Delete(inputFile);
                    }
                }
            }
        }
        finally
        {
            if (File.Exists(listFile))
            {
                File.Delete(listFile);
            }
        }
    }
}
