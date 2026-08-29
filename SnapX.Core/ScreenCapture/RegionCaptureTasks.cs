// SPDX-License-Identifier: GPL-3.0-or-later

using SixLabors.ImageSharp;
using SnapX.Core.Media;
using SnapX.Core.ScreenCapture.Helpers;
using SnapX.Core.Utils;

namespace SnapX.Core.ScreenCapture;

/// <summary>
/// Result returned by the host application's region selector. Coordinates are
/// expressed in virtual-desktop pixels, while <see cref="Image"/> contains only
/// the selected pixels when an image was requested.
/// </summary>
public sealed class RegionCaptureSelection
{
    public Rectangle Rectangle { get; init; }
    public Rectangle CaptureBounds { get; init; }
    public Image? Image { get; init; }
    public WindowInfo? WindowInfo { get; init; }
}

/// <summary>
/// Describes an interactive selection request without introducing a dependency
/// on a particular UI toolkit in SnapX.Core.
/// </summary>
public sealed class RegionCaptureRequest
{
    public RegionCaptureOptions Options { get; init; } = new();
    public RegionCaptureType CaptureType { get; init; }
    public bool CaptureImage { get; init; }
}

public static class RegionCaptureTasks
{
    private static Func<RegionCaptureRequest, CancellationToken, Task<RegionCaptureSelection?>>? regionSelector;
    private static readonly object ActiveSelectionLock = new();
    private static CancellationTokenSource? activeSelectionCancellation;
    private static readonly Lock LastRegionLock = new();
    private static Rectangle lastRegion = Rectangle.Empty;
    private static RegionCaptureType lastRegionCaptureType = RegionCaptureType.Default;

    public static bool IsRegionSelectorAvailable => Volatile.Read(ref regionSelector) is not null;

    /// <summary>
    /// True while an interactive region selector (slurp, snapx-picker, or
    /// Avalonia overlay) is waiting for user input in this process.
    /// </summary>
    public static bool IsSelectionActive
    {
        get
        {
            lock (ActiveSelectionLock)
            {
                return activeSelectionCancellation is not null;
            }
        }
    }

    public static void SetRegionSelector(
        Func<RegionCaptureRequest, CancellationToken, Task<RegionCaptureSelection?>>? selector)
    {
        Volatile.Write(ref regionSelector, selector);
    }

    /// <summary>
    /// Cancels the selector currently open in this process. The native picker
    /// processes are linked to this token and are killed by cancellation, so
    /// a one-shot timeout cannot leave a layer-shell helper running after the
    /// application exits.
    /// </summary>
    public static void CancelActiveSelection()
    {
        CancellationTokenSource? cancellation;
        lock (ActiveSelectionLock)
        {
            cancellation = activeSelectionCancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The selection completed and released its source before timeout.
        }
    }

    public static async Task<RegionCaptureSelection?> SelectRegionAsync(
        RegionCaptureOptions? options = null,
        RegionCaptureType captureType = RegionCaptureType.Default,
        bool captureImage = true,
        CancellationToken cancellationToken = default)
    {
        var selector = Volatile.Read(ref regionSelector);
        if (selector is null)
        {
            throw new InvalidOperationException(
                "Interactive region capture is unavailable because the desktop host did not register a region selector.");
        }

        bool annotateCaptureDisabled = options?.AnnotateCapture == false;
        var request = new RegionCaptureRequest
        {
            Options = GetRegionCaptureOptions(options),
            CaptureType = captureType,
            CaptureImage = captureImage
        };
        if (annotateCaptureDisabled)
        {
            request.Options.AnnotateCapture = false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (ActiveSelectionLock)
        {
            activeSelectionCancellation = linkedCancellation;
        }

        RegionCaptureSelection? selection;
        try
        {
            selection = await selector(request, linkedCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (ActiveSelectionLock)
            {
                if (ReferenceEquals(activeSelectionCancellation, linkedCancellation))
                {
                    activeSelectionCancellation = null;
                }
            }
        }

        if (selection is null)
        {
            return null;
        }

        try
        {
            Rectangle captureBounds = selection.CaptureBounds;
            if (captureBounds.Width <= 0 || captureBounds.Height <= 0)
            {
                try
                {
                    captureBounds = CaptureHelpers.GetScreenBounds();
                }
                catch (Exception ex) when (OperatingSystem.IsLinux())
                {
                    throw new PlatformNotSupportedException(
                        "The desktop backend did not provide usable capture bounds and native screen bounds are unavailable.",
                        ex);
                }
            }

            Rectangle normalized = NormalizeRectangle(
                selection.Rectangle,
                captureBounds,
                request.Options.MinimumSize);
            if (normalized.IsEmpty)
            {
                throw new InvalidOperationException("The selected capture region is empty or outside the virtual desktop.");
            }

            if (captureImage && selection.Image is null)
            {
                throw new InvalidOperationException("The region selector completed without returning the requested image.");
            }

            if (request.Options.UpdateRegionHistory)
            {
                lock (LastRegionLock)
                {
                    lastRegion = normalized;
                    lastRegionCaptureType = captureType;
                }
            }

            return new RegionCaptureSelection
            {
                Rectangle = normalized,
                CaptureBounds = captureBounds,
                Image = selection.Image,
                WindowInfo = selection.WindowInfo
            };
        }
        catch
        {
            // The selector transfers image ownership only on a successful
            // result. Validation failures (bad bounds, cancellation races,
            // or a missing requested image) must not retain its full-frame
            // bitmap while the capture gate unwinds.
            selection.Image?.Dispose();
            throw;
        }
    }

    public static bool TryGetLastRegion(out Rectangle rectangle, out RegionCaptureType captureType)
    {
        lock (LastRegionLock)
        {
            rectangle = lastRegion;
            captureType = lastRegionCaptureType;
            return !rectangle.IsEmpty;
        }
    }

    public static void SetLastRegion(Rectangle rectangle, RegionCaptureType captureType)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rectangle), "The last region must have positive dimensions.");
        }

        lock (LastRegionLock)
        {
            lastRegion = rectangle;
            lastRegionCaptureType = captureType;
        }
    }

    public static Image? GetRegionImage(RegionCaptureOptions? options = null)
    {
        return WaitForSelection(SelectRegionAsync(options))?.Image;
    }

    public static Image? GetRegionImage(out Rectangle rect, RegionCaptureOptions? options = null)
    {
        RegionCaptureSelection? selection = WaitForSelection(SelectRegionAsync(options));
        rect = selection?.Rectangle ?? Rectangle.Empty;
        return selection?.Image;
    }

    public static bool GetRectangleRegion(out Rectangle rect, RegionCaptureOptions? options = null)
    {
        RegionCaptureSelection? selection = WaitForSelection(
            SelectRegionAsync(options, captureImage: false));
        rect = selection?.Rectangle ?? Rectangle.Empty;
        return selection is not null;
    }

    public static bool GetRectangleRegion(
        out Rectangle rect,
        out WindowInfo windowInfo,
        RegionCaptureOptions? options = null)
    {
        RegionCaptureSelection? selection = WaitForSelection(
            SelectRegionAsync(options, captureImage: false));
        rect = selection?.Rectangle ?? Rectangle.Empty;
        windowInfo = selection?.WindowInfo ?? new WindowInfo();
        return selection is not null;
    }

    public static bool GetRectangleRegionTransparent(out Rectangle rect)
    {
        RegionCaptureSelection? selection = WaitForSelection(
            SelectRegionAsync(captureType: RegionCaptureType.Transparent, captureImage: false));
        rect = selection?.Rectangle ?? Rectangle.Empty;
        return selection is not null;
    }

    public static SimpleWindowInfo? GetWindowInfo(RegionCaptureOptions options)
    {
        RegionCaptureOptions newOptions = GetRegionCaptureOptions(options);
        newOptions.BackgroundDimStrength = 0;
        newOptions.ShowMagnifier = false;
        newOptions.DetectWindows = true;

        RegionCaptureSelection? selection = WaitForSelection(
            SelectRegionAsync(newOptions, captureImage: false));
        if (selection?.WindowInfo is not { Handle: var handle } || handle == IntPtr.Zero)
        {
            return null;
        }

        return new SimpleWindowInfo(handle, selection.WindowInfo.Rectangle)
        {
            IsWindow = true
        };
    }

    public static void ShowScreenRuler(RegionCaptureOptions options)
    {
        throw new NotSupportedException(
            "The current rectangle selector does not provide an interactive screen ruler.");
    }

    /// <summary>
    /// Normalizes an arbitrary drag rectangle and clamps it to the virtual desktop.
    /// </summary>
    public static Rectangle NormalizeRectangle(Rectangle rectangle, Rectangle bounds, int minimumSize = 1)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return Rectangle.Empty;
        }

        long rectangleX2 = (long)rectangle.X + rectangle.Width;
        long rectangleY2 = (long)rectangle.Y + rectangle.Height;
        long left = Math.Min(rectangle.X, rectangleX2);
        long top = Math.Min(rectangle.Y, rectangleY2);
        long right = Math.Max(rectangle.X, rectangleX2);
        long bottom = Math.Max(rectangle.Y, rectangleY2);

        long boundsX2 = (long)bounds.X + bounds.Width;
        long boundsY2 = (long)bounds.Y + bounds.Height;
        long boundsLeft = Math.Min(bounds.X, boundsX2);
        long boundsTop = Math.Min(bounds.Y, boundsY2);
        long boundsRight = Math.Max(bounds.X, boundsX2);
        long boundsBottom = Math.Max(bounds.Y, boundsY2);

        left = Math.Clamp(left, boundsLeft, boundsRight);
        top = Math.Clamp(top, boundsTop, boundsBottom);
        right = Math.Clamp(right, boundsLeft, boundsRight);
        bottom = Math.Clamp(bottom, boundsTop, boundsBottom);

        long width = right - left;
        long height = bottom - top;
        int requiredSize = Math.Max(1, minimumSize);
        if (left < int.MinValue || left > int.MaxValue
            || top < int.MinValue || top > int.MaxValue
            || width < requiredSize || width > int.MaxValue
            || height < requiredSize || height > int.MaxValue
            || left + width > int.MaxValue
            || top + height > int.MaxValue)
        {
            return Rectangle.Empty;
        }

        return new Rectangle((int)left, (int)top, (int)width, (int)height);
    }

    private static RegionCaptureSelection? WaitForSelection(Task<RegionCaptureSelection?> selectionTask)
    {
        if (!selectionTask.IsCompleted && SynchronizationContext.Current is not null)
        {
            throw new InvalidOperationException(
                "Synchronous region selection cannot block a UI synchronization context. Use SelectRegionAsync instead.");
        }

        return selectionTask.ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Returns a sanitized copy of the region-capture options for a selector session.
    /// </summary>
    public static RegionCaptureOptions GetRegionCaptureOptions(RegionCaptureOptions? options)
    {
        if (options is null)
        {
            return new RegionCaptureOptions();
        }

        var sanitized = new RegionCaptureOptions
        {
            QuickCrop = options.QuickCrop,
            MinimumSize = Math.Max(1, options.MinimumSize),
            DetectWindows = options.DetectWindows,
            DetectControls = options.DetectControls,
            UseDimming = options.UseDimming,
            BackgroundDimStrength = Math.Clamp(options.BackgroundDimStrength, 0, 100),
            SnapSizes = options.SnapSizes?.ToList() ?? [],
            ShowInfo = options.ShowInfo,
            ShowMagnifier = options.ShowMagnifier,
            UseSquareMagnifier = options.UseSquareMagnifier,
            MagnifierPixelCount = Math.Clamp(options.MagnifierPixelCount,
                RegionCaptureOptions.MagnifierPixelCountMinimum,
                RegionCaptureOptions.MagnifierPixelCountMaximum),
            MagnifierPixelSize = Math.Clamp(options.MagnifierPixelSize,
                RegionCaptureOptions.MagnifierPixelSizeMinimum,
                RegionCaptureOptions.MagnifierPixelSizeMaximum),
            ShowCrosshair = options.ShowCrosshair,
            UseLightResizeNodes = options.UseLightResizeNodes,
            EnableAnimations = options.EnableAnimations,
            IsFixedSize = options.IsFixedSize,
            FixedSize = options.FixedSize,
            ActiveMonitorMode = options.ActiveMonitorMode,
            ScreenColorPickerInfoText = options.ScreenColorPickerInfoText,
            WindowPickerMode = options.WindowPickerMode,
            WindowOrRegionPickerMode = options.WindowOrRegionPickerMode,
            MonitorPickerMode = options.MonitorPickerMode,
            UpdateRegionHistory = options.UpdateRegionHistory,
            // AnnotateCapture is runtime-only; persisted SurfaceOptions omit it
            // from YAML, so bool fields would otherwise deserialize as false.
            AnnotateCapture = true
        };

        if (sanitized.AnnotateCapture &&
            !sanitized.WindowPickerMode &&
            !sanitized.MonitorPickerMode)
        {
            // ShareX-style live annotate must not inherit a stale window-or-region
            // picker flag from an earlier selector session on the same options bag.
            sanitized.WindowOrRegionPickerMode = false;
        }

        return sanitized;
    }
}
