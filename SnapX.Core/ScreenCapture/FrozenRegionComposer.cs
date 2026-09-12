// SPDX-License-Identifier: GPL-3.0-or-later

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SnapX.Core.ScreenCapture;

/// <summary>
/// A display-sized image captured before a region-selection overlay is mapped.
/// Bounds are expressed in desktop points; Image dimensions are backing pixels.
/// </summary>
public readonly record struct FrozenDisplaySource(Rectangle Bounds, Image Image);

/// <summary>
/// Composes a desktop-point selection from independently captured display frames.
/// The highest backing scale on each axis is used so Retina pixels are not lost
/// when a selection crosses displays with different scale factors.
/// </summary>
public static class FrozenRegionComposer
{
    public static Image Compose(IReadOnlyList<FrozenDisplaySource> sources, Rectangle selection)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (selection.Width <= 0 || selection.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(selection), "Selection bounds must have positive dimensions.");

        var visibleSources = sources
            .Where(source => source.Bounds.Width > 0 && source.Bounds.Height > 0 &&
                !Rectangle.Intersect(source.Bounds, selection).IsEmpty)
            .ToList();
        if (visibleSources.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(selection), "Selection does not intersect a frozen display frame.");
        if (visibleSources.Any(source => source.Image is null || source.Image.Width <= 0 || source.Image.Height <= 0))
            throw new ArgumentException("Frozen display frames must contain decoded image pixels.", nameof(sources));

        if (visibleSources.Count == 1 && Rectangle.Intersect(visibleSources[0].Bounds, selection) == selection)
        {
            FrozenDisplaySource source = visibleSources[0];
            Rectangle crop = MapToBackingPixels(selection, source.Bounds, source.Image.Size);
            return CloneCropAsRgba(source.Image, crop);
        }

        double targetScaleX = visibleSources.Max(source => (double)source.Image.Width / source.Bounds.Width);
        double targetScaleY = visibleSources.Max(source => (double)source.Image.Height / source.Bounds.Height);
        int outputWidth = ScaledLength(selection.Width, targetScaleX);
        int outputHeight = ScaledLength(selection.Height, targetScaleY);
        var output = new Image<Rgba32>(outputWidth, outputHeight, Color.Transparent);

        try
        {
            foreach (FrozenDisplaySource source in visibleSources)
            {
                Rectangle intersection = Rectangle.Intersect(source.Bounds, selection);
                Rectangle sourceCrop = MapToBackingPixels(intersection, source.Bounds, source.Image.Size);
                Rectangle destination = MapToOutputPixels(intersection, selection, targetScaleX, targetScaleY);
                if (sourceCrop.IsEmpty || destination.IsEmpty)
                    continue;

                using Image<Rgba32> piece = CloneCropAsRgba(source.Image, sourceCrop);
                if (piece.Width != destination.Width || piece.Height != destination.Height)
                    piece.Mutate(context => context.Resize(destination.Width, destination.Height, KnownResamplers.Bicubic));
                output.Mutate(context => context.DrawImage(piece, destination.Location, 1f));
            }

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static Image<Rgba32> CloneCropAsRgba(Image source, Rectangle crop)
    {
        Image<Rgba32>? converted = source as Image<Rgba32>;
        bool ownsConverted = converted is null;
        converted ??= source.CloneAs<Rgba32>();
        try
        {
            return converted.Clone(context => context.Crop(crop));
        }
        finally
        {
            if (ownsConverted)
                converted.Dispose();
        }
    }

    private static Rectangle MapToBackingPixels(Rectangle intersection, Rectangle display, Size imageSize)
    {
        double scaleX = (double)imageSize.Width / display.Width;
        double scaleY = (double)imageSize.Height / display.Height;
        int left = ClampEdge(Math.Floor((intersection.Left - display.Left) * scaleX), imageSize.Width);
        int top = ClampEdge(Math.Floor((intersection.Top - display.Top) * scaleY), imageSize.Height);
        int right = ClampEdge(Math.Ceiling((intersection.Right - display.Left) * scaleX), imageSize.Width);
        int bottom = ClampEdge(Math.Ceiling((intersection.Bottom - display.Top) * scaleY), imageSize.Height);
        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static Rectangle MapToOutputPixels(
        Rectangle intersection,
        Rectangle selection,
        double scaleX,
        double scaleY)
    {
        int left = ScaledOffset(intersection.Left - selection.Left, scaleX);
        int top = ScaledOffset(intersection.Top - selection.Top, scaleY);
        int right = ScaledOffset(intersection.Right - selection.Left, scaleX);
        int bottom = ScaledOffset(intersection.Bottom - selection.Top, scaleY);
        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static int ClampEdge(double edge, int length) =>
        Math.Clamp(CheckedDimension(edge), 0, length);

    private static int ScaledLength(int length, double scale) =>
        Math.Max(1, CheckedDimension(Math.Ceiling(length * scale)));

    private static int ScaledOffset(int offset, double scale) =>
        CheckedDimension(Math.Round(offset * scale, MidpointRounding.AwayFromZero));

    private static int CheckedDimension(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > int.MaxValue)
            throw new InvalidOperationException("Frozen display composition produced invalid pixel bounds.");
        return (int)value;
    }
}
