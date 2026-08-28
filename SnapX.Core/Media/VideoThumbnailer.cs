
// SPDX-License-Identifier: GPL-3.0-or-later


using System.Diagnostics;
using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Extensions;
using SnapX.Core.Utils.Random;

namespace SnapX.Core.Media;

public class VideoThumbnailer
{
    public delegate void ProgressChangedEventHandler(int current, int length);
    public event ProgressChangedEventHandler ProgressChanged;

    public string? FFmpegPath { get; private set; }
    public VideoThumbnailOptions Options { get; private set; }
    public string MediaPath { get; private set; }
    public VideoInfo VideoInfo { get; private set; }

    public VideoThumbnailer(string? ffmpegPath, VideoThumbnailOptions options)
    {
        FFmpegPath = ffmpegPath;
        Options = options;
    }

    private void UpdateVideoInfo()
    {
        using (FFmpegCLIManager ffmpeg = new FFmpegCLIManager(FFmpegPath))
        {
            VideoInfo = ffmpeg.GetVideoInfo(MediaPath);
        }
    }

    public List<VideoThumbnailInfo> TakeThumbnails(string mediaPath)
    {
        MediaPath = mediaPath;

        if (Options.ThumbnailCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Options.ThumbnailCount), "At least one video thumbnail is required.");
        }

        UpdateVideoInfo();

        if (VideoInfo == null || VideoInfo.Duration == TimeSpan.Zero)
        {
            return null;
        }

        List<VideoThumbnailInfo> tempThumbnails = [];

        for (int i = 0; i < Options.ThumbnailCount; i++)
        {
            string mediaFileName = Path.GetFileNameWithoutExtension(MediaPath);

            int timeSliceElapsed;

            if (Options.RandomFrame)
            {
                timeSliceElapsed = GetRandomTimeSlice(i);
            }
            else
            {
                timeSliceElapsed = GetTimeSlice(Options.ThumbnailCount) * (i + 1);
            }

            string fileName = string.Format("{0}-{1}.{2}", mediaFileName, timeSliceElapsed, Options.ImageFormat.GetDescription());
            string? tempThumbnailPath = Path.Combine(GetOutputDirectory(), fileName);

            using (Process process = new Process())
            {
                ProcessStartInfo psi = new ProcessStartInfo()
                {
                    FileName = FFmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add(timeSliceElapsed.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(MediaPath);
                psi.ArgumentList.Add("-frames:v");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-update");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add(tempThumbnailPath);

                process.StartInfo = psi;
                process.Start();
                process.WaitForExit(1000 * 30);
            }

            if (System.IO.File.Exists(tempThumbnailPath))
            {
                VideoThumbnailInfo screenshotInfo = new VideoThumbnailInfo(tempThumbnailPath)
                {
                    Timestamp = TimeSpan.FromSeconds(timeSliceElapsed)
                };

                tempThumbnails.Add(screenshotInfo);
            }

            OnProgressChanged(i + 1, Options.ThumbnailCount);
        }

        return Finish(tempThumbnails);
    }

    private List<VideoThumbnailInfo> Finish(List<VideoThumbnailInfo> tempThumbnails)
    {
        List<VideoThumbnailInfo> thumbnails = [];

        if (tempThumbnails != null && tempThumbnails.Count > 0)
        {
            if (Options.CombineScreenshots)
            {
                VideoThumbnailInfo? combined = CombineThumbnails(tempThumbnails);
                if (combined is not null)
                {
                    thumbnails.Add(combined);
                }
            }
            else
            {
                thumbnails.AddRange(tempThumbnails);
            }

            if (Options.OpenDirectory && thumbnails.Count > 0)
            {
                FileHelpers.OpenFolderWithFile(thumbnails[0].FilePath);
            }
        }

        return thumbnails;
    }

    /// <summary>
    /// Extracts a single still for a completed recording. The caller owns the
    /// returned image. This is intentionally a result preview, not a live
    /// recorder preview, so it works for any FFmpeg output supported by the
    /// configured encoder.
    /// </summary>
    public static Image? TryCreatePreviewImage(string? ffmpegPath, string? mediaPath, int maximumWidth = 640)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath) || string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return null;
        }

        string temporaryPath = Path.Combine(Path.GetTempPath(), $"snapx-video-preview-{Guid.NewGuid():N}.png");
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            process.StartInfo.ArgumentList.Add("-hide_banner");
            process.StartInfo.ArgumentList.Add("-loglevel");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-ss");
            process.StartInfo.ArgumentList.Add("0.5");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(mediaPath);
            process.StartInfo.ArgumentList.Add("-frames:v");
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add(temporaryPath);

            if (!process.Start() || !process.WaitForExit(10_000))
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(temporaryPath))
            {
                return null;
            }

            using var source = Image.Load(temporaryPath);
            if (maximumWidth > 0 && source.Width > maximumWidth)
            {
                return source.Clone(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(maximumWidth, 0),
                    Mode = ResizeMode.Max
                }));
            }

            return source.CloneAs<Rgba32>();
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine($"Unable to create video preview for '{mediaPath}': {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The preview is optional; a delayed temporary-file cleanup is harmless.
            }
        }
    }

    private VideoThumbnailInfo? CombineThumbnails(IReadOnlyList<VideoThumbnailInfo> thumbnails)
    {
        var sourcePaths = thumbnails
            .Select(thumbnail => thumbnail.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Cast<string>()
            .ToList();
        if (sourcePaths.Count == 0)
        {
            return null;
        }

        int columns = Math.Max(1, Options.ColumnCount);
        int padding = Math.Max(0, Options.Padding);
        int spacing = Math.Max(0, Options.Spacing);
        var images = new List<Image<Rgba32>>();
        try
        {
            foreach (string path in sourcePaths)
            {
                var image = Image.Load<Rgba32>(path);
                if (Options.MaxThumbnailWidth > 0 && image.Width > Options.MaxThumbnailWidth)
                {
                    image.Mutate(context => context.Resize(new ResizeOptions
                    {
                        Size = new Size(Options.MaxThumbnailWidth, 0),
                        Mode = ResizeMode.Max
                    }));
                }

                images.Add(image);
            }

            int cellWidth = images.Max(image => image.Width);
            int cellHeight = images.Max(image => image.Height);
            int rows = (int)Math.Ceiling(images.Count / (double)columns);
            int width = (padding * 2) + (columns * cellWidth) + ((columns - 1) * spacing);
            int height = (padding * 2) + (rows * cellHeight) + ((rows - 1) * spacing);
            using var canvas = new Image<Rgba32>(width, height, Color.Black);
            for (int index = 0; index < images.Count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                Image<Rgba32> image = images[index];
                int x = padding + (column * (cellWidth + spacing)) + ((cellWidth - image.Width) / 2);
                int y = padding + (row * (cellHeight + spacing)) + ((cellHeight - image.Height) / 2);
                canvas.Mutate(context => context.DrawImage(image, new Point(x, y), 1f));
            }

            string filename = Path.GetFileNameWithoutExtension(MediaPath) + Options.FilenameSuffix + "." + Options.ImageFormat.GetDescription();
            string outputPath = Path.Combine(GetOutputDirectory()!, filename);
            canvas.Save(outputPath);

            if (!Options.KeepScreenshots)
            {
                foreach (string sourcePath in sourcePaths)
                {
                    if (!string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
                    {
                        FileHelpers.DeleteFile(sourcePath);
                    }
                }
            }

            return new VideoThumbnailInfo(outputPath);
        }
        finally
        {
            foreach (Image<Rgba32> image in images)
            {
                image.Dispose();
            }
        }
    }

    protected void OnProgressChanged(int current, int length)
    {
        ProgressChanged?.Invoke(current, length);
    }

    private string GetOutputDirectory()
    {
        string? directory;

        switch (Options.OutputLocation)
        {
            default:
            case ThumbnailLocationType.DefaultFolder:
                directory = Options.DefaultOutputDirectory;
                break;
            case ThumbnailLocationType.ParentFolder:
                directory = Path.GetDirectoryName(MediaPath);
                break;
            case ThumbnailLocationType.CustomFolder:
                directory = FileHelpers.ExpandFolderVariables(Options.CustomOutputDirectory);
                break;
        }

        directory ??= Path.GetDirectoryName(MediaPath);
        directory ??= Path.GetTempPath();
        FileHelpers.CreateDirectory(directory);

        return directory;
    }

    private int GetTimeSlice(int count)
    {
        return (int)(VideoInfo.Duration.TotalSeconds / count);
    }

    private int GetRandomTimeSlice(int start)
    {
        List<int> mediaSeekTimes = [];

        for (int i = 1; i < Options.ThumbnailCount + 2; i++)
        {
            mediaSeekTimes.Add(GetTimeSlice(Options.ThumbnailCount + 2) * i);
        }

        return (int)((RandomFast.NextDouble() * (mediaSeekTimes[start + 1] - mediaSeekTimes[start])) + mediaSeekTimes[start]);
    }
}
