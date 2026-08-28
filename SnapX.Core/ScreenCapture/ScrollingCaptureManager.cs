// SPDX-License-Identifier: GPL-3.0-or-later

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SnapX.Core.Media;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Native;

namespace SnapX.Core.ScreenCapture;

/// <summary>
/// Ports ShareX's scrolling capture algorithm to the cross-platform ImageSharp
/// pipeline. It repeatedly captures the selected rectangle, scrolls the page by
/// the configured method, and stitches frames by matching the overlapping
/// bottom edge. Image stitching is platform-neutral; input injection and region
/// capture are delegated to the platform backends.
/// </summary>
public sealed class ScrollingCaptureManager : IDisposable
{
    public ScrollingCaptureOptions Options { get; }
    public Image? Result { get; private set; }
    public bool IsCapturing { get; private set; }
    public ScrollingCaptureStatus Status { get; private set; }

    private Image? lastScreenshot;
    private Image? previousScreenshot;
    private bool stopRequested;
    private int bestMatchCount;
    private int bestMatchIndex;
    private int bestIgnoreBottomOffset;
    private readonly Rectangle selectedRectangle;

    /// <summary>Maximum frames captured before giving up.</summary>
    private const int MaxFrames = 100;

    /// <summary>Frames of unchanged stitched height before the capture is deemed complete.</summary>
    private const int StagnancyLimit = 3;

    /// <summary>
    /// Shared options instance so a capture's Options dialog persists the tuned
    /// values to the next capture, even though hotkey dispatch hands a fresh
    /// task-settings clone each time.
    /// </summary>
    private static ScrollingCaptureOptions? _sharedOptions;

    /// <summary>
    /// Returns the shared options, seeding them from <paramref name="seed"/> on
    /// first use. Subsequent captures reuse the same instance that the result
    /// window's Options dialog mutates, so a later capture honors tuned values.
    /// </summary>
    public static ScrollingCaptureOptions GetSharedOptions(ScrollingCaptureOptions? seed = null)
    {
        return _sharedOptions ??= seed ?? new ScrollingCaptureOptions();
    }

    public ScrollingCaptureManager(ScrollingCaptureOptions options, Rectangle selectedRectangle)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        this.selectedRectangle = selectedRectangle;
    }

    public void Dispose()
    {
        Reset();
    }

    private void Reset()
    {
        lastScreenshot?.Dispose();
        lastScreenshot = null;
        previousScreenshot?.Dispose();
        previousScreenshot = null;
        IsCapturing = false;
    }

    /// <summary>Requests the capture loop stop at the next opportunity.</summary>
    public void StopCapture()
    {
        if (IsCapturing)
        {
            stopRequested = true;
        }
    }

    /// <summary>Runs the scrolling capture loop and returns the working status.</summary>
    public async Task<ScrollingCaptureStatus> StartCaptureAsync()
    {
        if (IsCapturing || selectedRectangle.Width <= 0 || selectedRectangle.Height <= 0)
        {
            return ScrollingCaptureStatus.Failed;
        }

        IsCapturing = true;
        stopRequested = false;
        Status = ScrollingCaptureStatus.Failed;
        bestMatchCount = 0;
        bestMatchIndex = 0;
        bestIgnoreBottomOffset = 0;
        Result?.Dispose();
        Result = null;

        try
        {
            await Task.Delay(Options.StartDelay).ConfigureAwait(false);

            if (Options.AutoScrollTop)
            {
                InputHelpers.SendKeyPress(KeyCode.Home);
                await Task.Delay(Options.ScrollDelay).ConfigureAwait(false);
            }

            // The region selector leaves the pointer where the user released the
            // drag, which is over the captured page, so an X11 XTest wheel event
            // (preferred in SendMouseWheel) is delivered to the page window even
            // though keyboard focus returns to SnapX after selection.

            int frames = 0;
            int stagnantFrames = 0;
            int lastResultHeight = 0;
            while (!stopRequested && frames < MaxFrames)
            {
                lastScreenshot?.Dispose();
                lastScreenshot = CaptureRegion(selectedRectangle);
                if (lastScreenshot == null)
                {
                    break;
                }

                if (CompareLastTwoImages())
                {
                    Status = ScrollingCaptureStatus.Successful;
                    break;
                }

                if (Result == null)
                {
                    Result = lastScreenshot.Clone(ctx => { });
                    Status = ScrollingCaptureStatus.Successful;
                }
                else
                {
                    Image? combined = CombineImages(Result, lastScreenshot);
                    if (combined == null)
                    {
                        break;
                    }
                    Result.Dispose();
                    Result = combined;
                }

                frames++;
                if (stopRequested)
                {
                    break;
                }

                // On animated or sticky-header pages consecutive frames are never
                // byte-identical, so CompareLastTwoImages never fires and the loop
                // previously ran to the frame cap. Track the stitched height: once
                // the page is scrolled to its end the height stops growing even when
                // the content keeps animating, so a few stagnant frames mean the
                // capture is complete and we can stop reliably.
                if (Result.Height <= lastResultHeight)
                {
                    stagnantFrames++;
                    if (stagnantFrames >= StagnancyLimit)
                    {
                        Status = ScrollingCaptureStatus.Successful;
                        break;
                    }
                }
                else
                {
                    stagnantFrames = 0;
                    lastResultHeight = Result.Height;
                }

                previousScreenshot?.Dispose();
                previousScreenshot = lastScreenshot.Clone(ctx => { });
                Scroll();

                var timer = System.Diagnostics.Stopwatch.StartNew();
                int delay = Options.ScrollDelay - (int)timer.ElapsedMilliseconds;
                if (delay > 0)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            Status = ScrollingCaptureStatus.Failed;
        }
        finally
        {
            lastScreenshot?.Dispose();
            lastScreenshot = null;
            previousScreenshot?.Dispose();
            previousScreenshot = null;
            IsCapturing = false;
        }

        return Status;
    }

    private Image? CaptureRegion(Rectangle rect)
    {
        try
        {
            return new Screenshot { CaptureCursor = false }.CaptureRectangle(rect);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Scrolling capture frame failed.");
            return null;
        }
    }

    private bool CompareLastTwoImages()
    {
        // Detects that the page stopped changing (bottom reached) by comparing
        // the most recent frame with the previous one.
        if (lastScreenshot is null || previousScreenshot is null)
        {
            return false;
        }
        return ImagesEqual(lastScreenshot, previousScreenshot);
    }

    private void Scroll()
    {
        switch (Options.ScrollMethod)
        {
            case ScrollMethod.MouseWheel:
                InputHelpers.SendMouseWheel(-120 * Options.ScrollAmount);
                break;
            case ScrollMethod.DownArrow:
                for (int i = 0; i < Options.ScrollAmount; i++)
                {
                    InputHelpers.SendKeyPress(KeyCode.Down);
                }
                break;
            case ScrollMethod.PageDown:
                InputHelpers.SendKeyPress(KeyCode.PageDown);
                break;
            case ScrollMethod.ScrollMessage:
                if (!OperatingSystem.IsWindows())
                {
                    DebugHelper.WriteLine("ScrollMethod.ScrollMessage is Windows-only; using the mouse wheel.");
                    InputHelpers.SendMouseWheel(-120 * Options.ScrollAmount);
                }
                break;
        }
    }

    private Image? CombineImages(Image result, Image current)
    {
        // ImageSharp row comparison needs the concrete Rgba32 backing store.
        // Resolve both frames once so the O(H^2) scan does not clone either
        // full image on every row comparison.
        Image<Rgba32> resultRgba = result as Image<Rgba32> ?? result.CloneAs<Rgba32>();
        Image<Rgba32> currentRgba = current as Image<Rgba32> ?? current.CloneAs<Rgba32>();
        bool disposeResultRgba = !ReferenceEquals(resultRgba, result);
        bool disposeCurrentRgba = !ReferenceEquals(currentRgba, current);
        try
        {
        int matchCount = 0;
        int matchIndex = 0;
        int matchLimit = current.Height / 2;

        int ignoreSideOffset = Math.Max(50, current.Width / 20);
        ignoreSideOffset = Math.Min(ignoreSideOffset, current.Width / 3);

        int ignoreBottomOffsetMax = current.Height / 3;
        int ignoreBottomOffset = Math.Max(50, current.Height / 10);

        if (Options.AutoIgnoreBottomEdge)
        {
            int bottom = FindBottomEdgeOffset(resultRgba, currentRgba, ignoreSideOffset, ignoreBottomOffsetMax);
            ignoreBottomOffset = Math.Max(ignoreBottomOffset, bottom);
            ignoreBottomOffset = Math.Max(ignoreBottomOffset, bestIgnoreBottomOffset);
        }

        ignoreBottomOffset = Math.Min(ignoreBottomOffset, ignoreBottomOffsetMax);
        int rectBottom = result.Height - ignoreBottomOffset - 1;

        for (int currentY = current.Height - 1; currentY >= 0 && matchCount < matchLimit; currentY--)
        {
            int candidate = CountMatchingRows(resultRgba, currentRgba, currentY, rectBottom, ignoreSideOffset, matchLimit);
            if (candidate > matchCount)
            {
                matchCount = candidate;
                matchIndex = currentY;
            }
        }

        bool bestGuess = false;
        if (matchCount == 0 && bestMatchCount > 0)
        {
            matchCount = bestMatchCount;
            matchIndex = bestMatchIndex;
            ignoreBottomOffset = bestIgnoreBottomOffset;
            bestGuess = true;
        }

        if (matchCount <= 0)
        {
            Status = ScrollingCaptureStatus.Failed;
            return null;
        }

        int matchHeight = current.Height - matchIndex - 1;
        if (matchHeight <= 0)
        {
            Status = ScrollingCaptureStatus.Failed;
            return null;
        }

        if (matchCount > bestMatchCount)
        {
            bestMatchCount = matchCount;
            bestMatchIndex = matchIndex;
            bestIgnoreBottomOffset = ignoreBottomOffset;
        }

        // Upstream ShareX treats matchIndex as the last overlapping row in the
        // new frame and starts the appended content one row lower. Passing
        // matchIndex directly would duplicate one extra overlap row and
        // produce an off-by-one height and a visibly repeated seam.
        Image newResult = StitchFrames(result, current, ignoreBottomOffset, matchIndex + 1);

        if (bestGuess)
        {
            Status = ScrollingCaptureStatus.PartiallySuccessful;
        }
        else if (Status != ScrollingCaptureStatus.PartiallySuccessful)
        {
            Status = ScrollingCaptureStatus.Successful;
        }

        return newResult;
        }
        finally
        {
            if (disposeResultRgba) resultRgba.Dispose();
            if (disposeCurrentRgba) currentRgba.Dispose();
        }
    }

    private static int CountMatchingRows(Image<Rgba32> result, Image<Rgba32> current, int currentY, int rectBottom, int ignoreSideOffset, int matchLimit)
    {
        int count = 0;
        for (int y = 0; currentY - y >= 0 && count < matchLimit; y++)
        {
            int currentRow = currentY - y;
            int resultRow = rectBottom - y;
            if (resultRow < 0 || resultRow >= result.Height)
            {
                break;
            }
            if (RowsEqual(result, current, resultRow, currentRow, ignoreSideOffset))
            {
                count++;
            }
            else
            {
                break;
            }
        }
        return count;
    }

    private static bool RowsEqual(Image<Rgba32> left, Image<Rgba32> right, int resultRow, int currentRow, int ignoreSideOffset)
    {
        int width = Math.Min(left.Width, right.Width);
        int start = Math.Min(ignoreSideOffset, width / 4);
        Span<Rgba32> a = left.DangerousGetPixelRowMemory(resultRow).Span;
        Span<Rgba32> b = right.DangerousGetPixelRowMemory(currentRow).Span;
        for (int x = start; x < width - start; x++)
        {
            Rgba32 p = a[x];
            Rgba32 q = b[x];
            if (p.R != q.R || p.G != q.G || p.B != q.B)
            {
                return false;
            }
        }
        return true;
    }

    private static int FindBottomEdgeOffset(Image<Rgba32> result, Image<Rgba32> current, int ignoreSideOffset, int ignoreBottomOffsetMax)
    {
        int found = 0;
        int bottom = result.Height - 1;
        for (int i = 0; i <= ignoreBottomOffsetMax && bottom - i >= 0 && current.Height - 1 - i >= 0; i++)
        {
            int resultRow = bottom - i;
            int currentRow = current.Height - 1 - i;
            if (currentRow < 0)
            {
                break;
            }
            if (!RowsEqual(result, current, resultRow, currentRow, ignoreSideOffset))
            {
                found = i;
                break;
            }
        }
        return found;
    }

    public static bool ImagesEqual(Image a, Image b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        Image<Rgba32> left = a as Image<Rgba32> ?? a.CloneAs<Rgba32>();
        Image<Rgba32> right = b as Image<Rgba32> ?? b.CloneAs<Rgba32>();
        try
        {
            for (int y = 0; y < left.Height; y++)
            {
                Span<Rgba32> rowA = left.DangerousGetPixelRowMemory(y).Span;
                Span<Rgba32> rowB = right.DangerousGetPixelRowMemory(y).Span;
                if (!rowA.SequenceEqual(rowB))
                {
                    return false;
                }
            }
            return true;
        }
        finally
        {
            if (!ReferenceEquals(left, a)) left.Dispose();
            if (!ReferenceEquals(right, b)) right.Dispose();
        }
    }

    /// <summary>
    /// Stitches two frames, trimming <paramref name="overlap"/> rows from the
    /// bottom of the top frame and starting the bottom frame at
    /// <paramref name="bottomStartRow"/>. Pure and deterministic, exposed for
    /// property testing.
    /// </summary>
    public static Image StitchFrames(Image top, Image bottom, int overlap, int bottomStartRow = 0)
    {
        int newHeight = top.Height - overlap + (bottom.Height - bottomStartRow);
        var newResult = new Image<Rgba32>(top.Width, newHeight, Color.Black);
        newResult.Mutate(ctx =>
        {
            ctx.DrawImage(top, new Point(0, 0), new Rectangle(0, 0, top.Width, top.Height - overlap), 1f);
            ctx.DrawImage(bottom, new Point(0, top.Height - overlap), new Rectangle(0, bottomStartRow, bottom.Width, bottom.Height - bottomStartRow), 1f);
        });
        return newResult;
    }
}
