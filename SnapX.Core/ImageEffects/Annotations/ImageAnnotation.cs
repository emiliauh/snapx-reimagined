// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SnapX.Core.ImageEffects.Annotations;

/// <summary>
/// A single non-destructive annotation drawn over a captured image. Concrete
/// subclasses store their geometry in capture coordinates and render onto an
/// ImageSharp <see cref="Image"/> via <see cref="Apply"/>. The model is kept in
/// SnapX.Core so the editor UI only has to translate user gestures into these
/// primitives; the compositing logic is testable headlessly.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RectangleAnnotation), "Rectangle")]
[JsonDerivedType(typeof(RedactionAnnotation), "Redaction")]
[JsonDerivedType(typeof(BlurAnnotation), "Blur")]
[JsonDerivedType(typeof(FreehandAnnotation), "Freehand")]
[JsonDerivedType(typeof(ArrowAnnotation), "Arrow")]
[JsonDerivedType(typeof(TextAnnotation), "Text")]
[JsonDerivedType(typeof(CropAnnotation), "Crop")]
public abstract class ImageAnnotation
{
    public enum Tool
    {
        Rectangle,
        Redaction,
        Blur,
        Freehand,
        Arrow,
        Text,
        Crop
    }

    public bool Enabled { get; set; } = true;

    public abstract Image Apply(Image img);
}

public sealed class RectangleAnnotation : ImageAnnotation
{
    public Rectangle Rectangle { get; set; }
    public Color Color { get; set; } = Color.Red;
    public int Thickness { get; set; } = 2;
    public bool Filled { get; set; }

    public override Image Apply(Image img)
    {
        if (!Enabled || Rectangle.IsEmpty) return img;

        img.Mutate(ctx =>
        {
            if (Filled)
            {
                ctx.Fill(new SolidBrush(AnnotationCompositor.ToRgba32(Color)), Rectangle);
            }
            else
            {
                ctx.Draw(new SolidPen(AnnotationCompositor.ToRgba32(Color), Thickness), Rectangle);
            }
        });
        return img;
    }
}

public sealed class RedactionAnnotation : ImageAnnotation
{
    public Rectangle Rectangle { get; set; }

    public override Image Apply(Image img)
    {
        if (!Enabled || Rectangle.IsEmpty) return img;

        img.Mutate(ctx => ctx.Fill(Color.Black, Rectangle));
        return img;
    }
}

public sealed class BlurAnnotation : ImageAnnotation
{
    public Rectangle Rectangle { get; set; }
    public float Radius { get; set; } = 12;

    public override Image Apply(Image img)
    {
        if (!Enabled || Rectangle.IsEmpty) return img;

        Rectangle clipped = SixLabors.ImageSharp.Rectangle.Intersect(Rectangle, img.Bounds);
        if (clipped.IsEmpty) return img;

        float maxRadius = Math.Max(0.5f, (Math.Min(clipped.Width, clipped.Height) - 1) / 6f);
        float radius = Math.Clamp(Radius, 0.5f, maxRadius);
        using Image blurred = img.Clone(ctx =>
            ctx.Crop(clipped).GaussianBlur(radius));
        img.Mutate(ctx => ctx.DrawImage(
            blurred,
            new Point(clipped.X, clipped.Y),
            1f));
        return img;
    }
}

public sealed class FreehandAnnotation : ImageAnnotation
{
    public List<PointF> Points { get; set; } = [];
    public Color Color { get; set; } = Color.Yellow;
    public int Thickness { get; set; } = 3;

    public override Image Apply(Image img)
    {
        if (!Enabled || Points == null || Points.Count < 2) return img;

        img.Mutate(ctx =>
        {
            var pen = new SolidPen(AnnotationCompositor.ToRgba32(Color), Thickness);
            for (int i = 1; i < Points.Count; i++)
            {
                ctx.DrawLine(pen, Points[i - 1], Points[i]);
            }
        });
        return img;
    }
}

public sealed class ArrowAnnotation : ImageAnnotation
{
    public PointF Start { get; set; }
    public PointF End { get; set; }
    public Color Color { get; set; } = Color.Green;
    public int Thickness { get; set; } = 3;

    public override Image Apply(Image img)
    {
        if (!Enabled) return img;

        img.Mutate(ctx =>
        {
            var pen = new SolidPen(AnnotationCompositor.ToRgba32(Color), Thickness);
            ctx.DrawLine(pen, Start, End);

            const double angle = Math.PI / 6;
            const int headLength = 16;
            double dx = End.X - Start.X;
            double dy = End.Y - Start.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            double ux = dx / len;
            double uy = dy / len;
            double bx = End.X - headLength * ux;
            double by = End.Y - headLength * uy;
            double px = bx + headLength / 2 * (-uy);
            double py = by + headLength / 2 * ux;
            PointF left = new((float)(bx + headLength * Math.Cos(Math.PI - angle) * ux - headLength * Math.Sin(Math.PI - angle) * uy), (float)(by + headLength * Math.Cos(Math.PI - angle) * uy + headLength * Math.Sin(Math.PI - angle) * ux));
            PointF right = new((float)(bx + headLength * Math.Cos(Math.PI + angle) * ux - headLength * Math.Sin(Math.PI + angle) * uy), (float)(by + headLength * Math.Cos(Math.PI + angle) * uy + headLength * Math.Sin(Math.PI + angle) * ux));
            ctx.DrawPolygon(pen, [left, new PointF(End.X, End.Y), right]);
        });
        return img;
    }
}

public sealed class TextAnnotation : ImageAnnotation
{
    public PointF Position { get; set; }
    public string Text { get; set; } = "";
    public int FontSize { get; set; } = 18;
    public Color Color { get; set; } = Color.White;

    public override Image Apply(Image img)
    {
        if (!Enabled || string.IsNullOrEmpty(Text)) return img;

        // Fonts are not guaranteed to be available in a headless/trimmed
        // environment, so text is composited with a best-effort font lookup.
        // If no system font resolves this is a deliberate no-op rather than a
        // crash in the capture pipeline.
        try
        {
            SixLabors.Fonts.FontFamily family = SixLabors.Fonts.SystemFonts.Families.FirstOrDefault();
            if (family.Name is { Length: > 0 } name)
            {
                SixLabors.Fonts.Font font = SixLabors.Fonts.SystemFonts.CreateFont(name, FontSize);
                img.Mutate(ctx => ctx.DrawText(Text, font, AnnotationCompositor.ToRgba32(Color), Position));
            }
        }
        catch
        {
            // Font resolution unavailable; keep the original pixels.
        }

        return img;
    }
}

public sealed class CropAnnotation : ImageAnnotation
{
    public Rectangle Rectangle { get; set; }

    public override Image Apply(Image img)
    {
        if (!Enabled || Rectangle.IsEmpty) return img;

        int x = Math.Clamp(Rectangle.X, 0, img.Width);
        int y = Math.Clamp(Rectangle.Y, 0, img.Height);
        int right = Math.Clamp(Rectangle.X + Rectangle.Width, 0, img.Width);
        int bottom = Math.Clamp(Rectangle.Y + Rectangle.Height, 0, img.Height);
        int width = right - x;
        int height = bottom - y;
        if (width <= 0 || height <= 0) return img;

        var crop = new Rectangle(x, y, width, height);
        return img.Clone(ctx => ctx.Crop(crop));
    }
}

internal static class AnnotationCompositor
{
    internal static Rgba32 ToRgba32(Color color) => color.ToPixel<Rgba32>();
}
