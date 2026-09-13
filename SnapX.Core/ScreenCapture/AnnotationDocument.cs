// SPDX-License-Identifier: GPL-3.0-or-later

using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SnapX.Core.ScreenCapture;

public enum AnnotationTool
{
    Select,
    Freehand,
    Rectangle,
    Ellipse,
    Arrow,
    Text,
    Blur,
    Erase
}

public enum AnnotationResizeHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left
}

public sealed class AnnotationElement
{
    public AnnotationTool Tool { get; set; }
    public RectangleF Bounds { get; set; }
    public Rgba32 Color { get; set; } = new(220, 38, 38);
    public float StrokeWidth { get; set; } = 4;
    public string Text { get; set; } = "Text";
    public float FontSize { get; set; } = 30;
    public float EffectStrength { get; set; } = 12;
    public List<PointF> Points { get; set; } = [];

    public AnnotationElement Clone() => new()
    {
        Tool = Tool,
        Bounds = Bounds,
        Color = Color,
        StrokeWidth = StrokeWidth,
        Text = Text,
        FontSize = FontSize,
        EffectStrength = EffectStrength,
        Points = [.. Points]
    };
}

/// <summary>
/// Pixel-coordinate annotation document shared by every desktop host. UI
/// scaling never changes these values, so export remains at source resolution.
/// </summary>
public sealed class AnnotationDocument
{
    private readonly List<List<AnnotationElement>> undo = [];
    private readonly List<List<AnnotationElement>> redo = [];

    /// <summary>
    /// One concrete installed family shared by the preview and ImageSharp
    /// exporter. Using each toolkit's independent "default" can otherwise
    /// change text metrics after the user accepts the edit.
    /// </summary>
    public static string? TextFontFamilyName { get; } =
        SystemFonts.Collection.Families.Select(family => family.Name).FirstOrDefault();

    public List<AnnotationElement> Elements { get; } = [];
    public int SelectedIndex { get; set; } = -1;
    public AnnotationElement? Selected => SelectedIndex >= 0 && SelectedIndex < Elements.Count
        ? Elements[SelectedIndex]
        : null;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Checkpoint()
    {
        undo.Add(CloneElements());
        if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear();
    }

    public void Add(AnnotationElement element, bool select = true)
    {
        ArgumentNullException.ThrowIfNull(element);
        Checkpoint();
        Elements.Add(element);
        // A canvas can add an element before its pointer gesture is complete so
        // the stroke remains visible while it is being drawn. Do not expose
        // that provisional geometry as a selection until the canvas commits it.
        SelectedIndex = select ? Elements.Count - 1 : -1;
    }

    public bool Select(AnnotationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        SelectedIndex = Elements.IndexOf(element);
        return SelectedIndex >= 0;
    }

    public void DeleteSelected()
    {
        if (Selected is null) return;
        Checkpoint();
        Elements.RemoveAt(SelectedIndex);
        SelectedIndex = Math.Min(SelectedIndex, Elements.Count - 1);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        redo.Add(CloneElements());
        Restore(undo[^1]);
        undo.RemoveAt(undo.Count - 1);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        undo.Add(CloneElements());
        Restore(redo[^1]);
        redo.RemoveAt(redo.Count - 1);
    }

    public int HitTest(PointF point, float tolerance)
    {
        for (int index = Elements.Count - 1; index >= 0; index--)
        {
            AnnotationElement element = Elements[index];
            RectangleF inflated = Inflate(Normalize(element.Bounds), Math.Max(tolerance, element.StrokeWidth));
            if (inflated.Contains(point)) return index;
        }

        return -1;
    }

    public AnnotationResizeHandle HitTestSelectedHandle(PointF point, float radius)
    {
        if (Selected is null) return AnnotationResizeHandle.None;
        RectangleF bounds = Normalize(Selected.Bounds);
        foreach ((AnnotationResizeHandle handle, PointF location) in GetHandleLocations(bounds))
        {
            float dx = point.X - location.X;
            float dy = point.Y - location.Y;
            if (dx * dx + dy * dy <= radius * radius) return handle;
        }

        return AnnotationResizeHandle.None;
    }

    public void MoveSelected(float deltaX, float deltaY)
    {
        AnnotationElement? selected = Selected;
        if (selected is null) return;
        selected.Bounds = new RectangleF(
            selected.Bounds.X + deltaX,
            selected.Bounds.Y + deltaY,
            selected.Bounds.Width,
            selected.Bounds.Height);
        for (int i = 0; i < selected.Points.Count; i++)
        {
            PointF point = selected.Points[i];
            selected.Points[i] = new PointF(point.X + deltaX, point.Y + deltaY);
        }
    }

    public void ResizeSelected(AnnotationResizeHandle handle, PointF point, float minimumSize = 2)
    {
        AnnotationElement? selected = Selected;
        if (selected is null || handle == AnnotationResizeHandle.None) return;

        RectangleF old = Normalize(selected.Bounds);
        if (selected.Tool == AnnotationTool.Text)
        {
            ResizeText(selected, old, handle, point, minimumSize);
            return;
        }

        float left = old.Left;
        float top = old.Top;
        float right = old.Right;
        float bottom = old.Bottom;

        if (handle is AnnotationResizeHandle.TopLeft or AnnotationResizeHandle.Left or AnnotationResizeHandle.BottomLeft)
            left = Math.Min(point.X, right - minimumSize);
        if (handle is AnnotationResizeHandle.TopLeft or AnnotationResizeHandle.Top or AnnotationResizeHandle.TopRight)
            top = Math.Min(point.Y, bottom - minimumSize);
        if (handle is AnnotationResizeHandle.TopRight or AnnotationResizeHandle.Right or AnnotationResizeHandle.BottomRight)
            right = Math.Max(point.X, left + minimumSize);
        if (handle is AnnotationResizeHandle.BottomLeft or AnnotationResizeHandle.Bottom or AnnotationResizeHandle.BottomRight)
            bottom = Math.Max(point.Y, top + minimumSize);

        RectangleF resized = RectangleF.FromLTRB(left, top, right, bottom);
        if (selected.Points.Count > 0)
        {
            bool changesLeft = handle is AnnotationResizeHandle.TopLeft or
                AnnotationResizeHandle.Left or AnnotationResizeHandle.BottomLeft;
            bool changesRight = handle is AnnotationResizeHandle.TopRight or
                AnnotationResizeHandle.Right or AnnotationResizeHandle.BottomRight;
            bool changesTop = handle is AnnotationResizeHandle.TopLeft or
                AnnotationResizeHandle.Top or AnnotationResizeHandle.TopRight;
            bool changesBottom = handle is AnnotationResizeHandle.BottomLeft or
                AnnotationResizeHandle.Bottom or AnnotationResizeHandle.BottomRight;

            for (int i = 0; i < selected.Points.Count; i++)
            {
                PointF value = selected.Points[i];
                float? horizontalProgress = old.Width > float.Epsilon
                    ? Math.Clamp((value.X - old.Left) / old.Width, 0, 1)
                    : null;
                float? verticalProgress = old.Height > float.Epsilon
                    ? Math.Clamp((value.Y - old.Top) / old.Height, 0, 1)
                    : null;
                selected.Points[i] = new PointF(
                    TransformAxis(value.X, old.Left, old.Right, resized.Left, resized.Right,
                        changesLeft, changesRight, verticalProgress, changesTop, changesBottom),
                    TransformAxis(value.Y, old.Top, old.Bottom, resized.Top, resized.Bottom,
                        changesTop, changesBottom, horizontalProgress, changesLeft, changesRight));
            }

            // Point-backed elements draw from their points, not Bounds. Derive
            // the bounds from the transformed geometry so a horizontal or
            // vertical line cannot leave invisible, stale resize handles.
            selected.Bounds = BoundsFromPoints(selected.Points);
        }
        else
        {
            selected.Bounds = resized;
        }
    }

    /// <summary>Returns the exact font size shared by the live preview and exported image.</summary>
    public static float GetEffectiveTextFontSize(AnnotationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Math.Max(8, element.FontSize);
    }

    /// <summary>
    /// Fits a text annotation's selectable bounds to the content while
    /// retaining its top-left position and font size. The measurement uses
    /// the same font family and size as the exported image.
    /// </summary>
    public static void FitTextBoundsToContent(AnnotationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.Tool != AnnotationTool.Text || string.IsNullOrWhiteSpace(element.Text) ||
            TextFontFamilyName is not { Length: > 0 } familyName)
            return;

        RectangleF old = Normalize(element.Bounds);
        Font font = SystemFonts.CreateFont(familyName, GetEffectiveTextFontSize(element), FontStyle.Regular);
        FontRectangle measured = TextMeasurer.MeasureSize(element.Text, new TextOptions(font));
        element.Bounds = new RectangleF(
            old.X,
            old.Y,
            Math.Max(1, (float)Math.Ceiling(measured.Width)),
            Math.Max(1, (float)Math.Ceiling(measured.Height)));
    }

    public Image<Rgba32> Render(Image source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Image<Rgba32> output = source.CloneAs<Rgba32>();
        foreach (AnnotationElement element in Elements)
        {
            if (element.Tool == AnnotationTool.Blur)
                ApplyBlur(output, element);
            else
                output.Mutate(context => DrawElement(context, element));
        }
        return output;
    }

    /// <summary>
    /// Estimates the background at the perimeter of an erase area. Foreground
    /// text and icons must not brighten/darken the fill by contributing to an
    /// average of the entire selection. Sampling remains bounded for large captures.
    /// </summary>
    public static Rgba32 SampleRepresentativeColor(Image image, RectangleF bounds)
    {
        ArgumentNullException.ThrowIfNull(image);
        Rectangle rectangle = ToPixelRectangle(bounds, image.Width, image.Height);
        if (rectangle.IsEmpty) return new Rgba32(0, 0, 0, 255);

        using Image<Rgba32>? converted = image is Image<Rgba32> ? null : image.CloneAs<Rgba32>();
        Image<Rgba32> pixels = image as Image<Rgba32> ?? converted!;
        // Group nearby colors so antialiasing and gentle background variation
        // cannot let a smaller, perfectly uniform foreground win the vote.
        // Return a real sampled color from the winning group, never a gray
        // synthesized by averaging a dark background with light lettering.
        var groups = new Dictionary<int, int>();
        var colors = new Dictionary<Rgba32, int>();
        void Sample(int x, int y)
        {
            Rgba32 pixel = pixels[x, y];
            pixel.A = 255;
            int group = ColorGroup(pixel);
            groups[group] = groups.GetValueOrDefault(group) + 1;
            colors[pixel] = colors.GetValueOrDefault(pixel) + 1;
        }

        // Sample a narrow inner perimeter band so central text or images
        // cannot overwhelm the background estimate.
        int stepX = Math.Max(1, (rectangle.Width + 63) / 64);
        int stepY = Math.Max(1, (rectangle.Height + 63) / 64);
        int band = Math.Min(2, Math.Min(rectangle.Width, rectangle.Height));
        for (int inset = 0; inset < band; inset++)
        {
            for (int x = rectangle.Left; x < rectangle.Right; x += stepX)
            {
                Sample(x, rectangle.Top + inset);
                Sample(x, rectangle.Bottom - 1 - inset);
            }
            for (int y = rectangle.Top; y < rectangle.Bottom; y += stepY)
            {
                Sample(rectangle.Left + inset, y);
                Sample(rectangle.Right - 1 - inset, y);
            }
        }

        int backgroundGroup = groups.MaxBy(pair => pair.Value).Key;
        return colors.Where(pair => ColorGroup(pair.Key) == backgroundGroup)
            .MaxBy(pair => pair.Value).Key;

        static int ColorGroup(Rgba32 pixel) => (pixel.R >> 3) << 10 | (pixel.G >> 3) << 5 | pixel.B >> 3;
    }

    /// <summary>
    /// Creates an export-only document whose coordinates are mapped from a
    /// source rectangle in this document to the target image. The live
    /// document and its undo history remain untouched; elements outside the
    /// rectangle are retained so ImageSharp can clip them naturally.
    /// </summary>
    public AnnotationDocument Transform(RectangleF sourceRectangle, int targetWidth, int targetHeight)
    {
        sourceRectangle = Normalize(sourceRectangle);
        if (sourceRectangle.Width <= 0 || sourceRectangle.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceRectangle), "Annotation export area must be non-empty.");
        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "Annotation export dimensions must be positive.");

        float scaleX = targetWidth / sourceRectangle.Width;
        float scaleY = targetHeight / sourceRectangle.Height;
        float uniformScale = MathF.Sqrt(scaleX * scaleY);
        var transformed = new AnnotationDocument();
        foreach (AnnotationElement element in Elements)
        {
            AnnotationElement copy = element.Clone();
            copy.Bounds = new RectangleF(
                (copy.Bounds.X - sourceRectangle.X) * scaleX,
                (copy.Bounds.Y - sourceRectangle.Y) * scaleY,
                copy.Bounds.Width * scaleX,
                copy.Bounds.Height * scaleY);
            for (int index = 0; index < copy.Points.Count; index++)
            {
                PointF point = copy.Points[index];
                copy.Points[index] = new PointF(
                    (point.X - sourceRectangle.X) * scaleX,
                    (point.Y - sourceRectangle.Y) * scaleY);
            }
            copy.StrokeWidth *= uniformScale;
            copy.FontSize *= uniformScale;
            copy.EffectStrength *= uniformScale;
            transformed.Elements.Add(copy);
        }
        transformed.SelectedIndex = -1;
        return transformed;
    }

    public static RectangleF BoundsFromPoints(IReadOnlyList<PointF> points)
    {
        if (points.Count == 0) return RectangleF.Empty;
        float left = points.Min(point => point.X);
        float top = points.Min(point => point.Y);
        float right = points.Max(point => point.X);
        float bottom = points.Max(point => point.Y);
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    public static RectangleF Normalize(RectangleF bounds) => RectangleF.FromLTRB(
        Math.Min(bounds.Left, bounds.Right),
        Math.Min(bounds.Top, bounds.Bottom),
        Math.Max(bounds.Left, bounds.Right),
        Math.Max(bounds.Top, bounds.Bottom));

    public static IEnumerable<(AnnotationResizeHandle Handle, PointF Point)> GetHandleLocations(RectangleF bounds)
    {
        float centerX = bounds.Left + bounds.Width / 2;
        float centerY = bounds.Top + bounds.Height / 2;
        yield return (AnnotationResizeHandle.TopLeft, new PointF(bounds.Left, bounds.Top));
        yield return (AnnotationResizeHandle.Top, new PointF(centerX, bounds.Top));
        yield return (AnnotationResizeHandle.TopRight, new PointF(bounds.Right, bounds.Top));
        yield return (AnnotationResizeHandle.Right, new PointF(bounds.Right, centerY));
        yield return (AnnotationResizeHandle.BottomRight, new PointF(bounds.Right, bounds.Bottom));
        yield return (AnnotationResizeHandle.Bottom, new PointF(centerX, bounds.Bottom));
        yield return (AnnotationResizeHandle.BottomLeft, new PointF(bounds.Left, bounds.Bottom));
        yield return (AnnotationResizeHandle.Left, new PointF(bounds.Left, centerY));
    }

    private static void DrawElement(IImageProcessingContext context, AnnotationElement element)
    {
        Color color = Color.FromPixel(element.Color);
        float stroke = Math.Max(1, element.StrokeWidth);
        RectangleF bounds = Normalize(element.Bounds);
        switch (element.Tool)
        {
            case AnnotationTool.Freehand:
                if (element.Points.Count > 1) context.DrawLine(color, stroke, [.. element.Points]);
                break;
            case AnnotationTool.Rectangle:
                context.Draw(color, stroke, bounds);
                break;
            case AnnotationTool.Ellipse:
                context.Draw(color, stroke, new EllipsePolygon(
                    bounds.Left + bounds.Width / 2,
                    bounds.Top + bounds.Height / 2,
                    bounds.Width,
                    bounds.Height));
                break;
            case AnnotationTool.Arrow:
                DrawArrow(context, element, color, stroke);
                break;
            case AnnotationTool.Text:
                DrawText(context, element, color);
                break;
            case AnnotationTool.Erase:
                context.Fill(color, bounds);
                break;
        }
    }

    private static void ApplyBlur(Image<Rgba32> output, AnnotationElement element)
    {
        Rectangle target = ToPixelRectangle(element.Bounds, output.Width, output.Height);
        if (target.IsEmpty) return;

        float strength = Math.Max(1, element.EffectStrength);
        int padding = (int)Math.Ceiling(strength * 3);
        Rectangle sample = Rectangle.Intersect(
            new Rectangle(target.X - padding, target.Y - padding,
                target.Width + padding * 2, target.Height + padding * 2),
            new Rectangle(0, 0, output.Width, output.Height));
        using Image<Rgba32> patch = output.Clone(context =>
            context.Crop(sample).GaussianBlur(strength));
        patch.Mutate(context => context.Crop(new Rectangle(
            target.X - sample.X,
            target.Y - sample.Y,
            target.Width,
            target.Height)));
        output.Mutate(context => context.DrawImage(patch, target.Location, 1));
    }

    private static Rectangle ToPixelRectangle(RectangleF bounds, int imageWidth, int imageHeight)
    {
        bounds = Normalize(bounds);
        int left = Math.Clamp((int)MathF.Floor(bounds.Left), 0, imageWidth);
        int top = Math.Clamp((int)MathF.Floor(bounds.Top), 0, imageHeight);
        int right = Math.Clamp((int)MathF.Ceiling(bounds.Right), 0, imageWidth);
        int bottom = Math.Clamp((int)MathF.Ceiling(bounds.Bottom), 0, imageHeight);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static void DrawArrow(IImageProcessingContext context, AnnotationElement element, Color color, float stroke)
    {
        if (element.Points.Count < 2) return;
        PointF start = element.Points[0];
        PointF end = element.Points[^1];
        context.DrawLine(color, stroke, start, end);
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        float head = Math.Max(10, stroke * 4);
        PointF a = new(end.X - head * (float)Math.Cos(angle - .55), end.Y - head * (float)Math.Sin(angle - .55));
        PointF b = new(end.X - head * (float)Math.Cos(angle + .55), end.Y - head * (float)Math.Sin(angle + .55));
        context.DrawLine(color, stroke, a, end, b);
    }

    private static void DrawText(IImageProcessingContext context, AnnotationElement element, Color color)
    {
        if (string.IsNullOrWhiteSpace(element.Text)) return;
        if (TextFontFamilyName is not { Length: > 0 } familyName) return;
        Font font = SystemFonts.CreateFont(familyName, GetEffectiveTextFontSize(element), FontStyle.Regular);
        context.DrawText(element.Text, font, color, Normalize(element.Bounds).Location);
    }

    private static void ResizeText(
        AnnotationElement element,
        RectangleF old,
        AnnotationResizeHandle handle,
        PointF point,
        float minimumSize)
    {
        bool changesLeft = handle is AnnotationResizeHandle.TopLeft or
            AnnotationResizeHandle.Left or AnnotationResizeHandle.BottomLeft;
        bool changesRight = handle is AnnotationResizeHandle.TopRight or
            AnnotationResizeHandle.Right or AnnotationResizeHandle.BottomRight;
        bool changesTop = handle is AnnotationResizeHandle.TopLeft or
            AnnotationResizeHandle.Top or AnnotationResizeHandle.TopRight;
        bool changesBottom = handle is AnnotationResizeHandle.BottomLeft or
            AnnotationResizeHandle.Bottom or AnnotationResizeHandle.BottomRight;
        bool changesHorizontal = changesLeft || changesRight;
        bool changesVertical = changesTop || changesBottom;

        float oldWidth = Math.Max(old.Width, minimumSize);
        float oldHeight = Math.Max(old.Height, minimumSize);
        float horizontalScale = changesLeft
            ? (old.Right - point.X) / oldWidth
            : changesRight ? (point.X - old.Left) / oldWidth : 1;
        float verticalScale = changesTop
            ? (old.Bottom - point.Y) / oldHeight
            : changesBottom ? (point.Y - old.Top) / oldHeight : 1;

        float scale;
        if (changesHorizontal && changesVertical)
        {
            // Project a corner drag onto the original aspect-ratio diagonal.
            // This is the closest uniform scale to the pointer on both axes.
            float widthSquared = oldWidth * oldWidth;
            float heightSquared = oldHeight * oldHeight;
            scale = (horizontalScale * widthSquared + verticalScale * heightSquared) /
                (widthSquared + heightSquared);
        }
        else
        {
            scale = changesHorizontal ? horizontalScale : verticalScale;
        }

        float minimumScale = Math.Max(
            Math.Max(minimumSize / oldWidth, minimumSize / oldHeight),
            8 / Math.Max(element.FontSize, float.Epsilon));
        scale = Math.Max(scale, minimumScale);

        float width = oldWidth * scale;
        float height = oldHeight * scale;
        float left = changesLeft
            ? old.Right - width
            : changesRight ? old.Left : old.Left + (oldWidth - width) / 2;
        float top = changesTop
            ? old.Bottom - height
            : changesBottom ? old.Top : old.Top + (oldHeight - height) / 2;

        element.Bounds = new RectangleF(left, top, width, height);
        element.FontSize = GetEffectiveTextFontSize(element) * scale;
    }

    private List<AnnotationElement> CloneElements() => Elements.Select(element => element.Clone()).ToList();

    private void Restore(List<AnnotationElement> snapshot)
    {
        Elements.Clear();
        Elements.AddRange(snapshot.Select(element => element.Clone()));
        SelectedIndex = Math.Min(SelectedIndex, Elements.Count - 1);
    }

    private static RectangleF Inflate(RectangleF rectangle, float amount) => RectangleF.FromLTRB(
        rectangle.Left - amount,
        rectangle.Top - amount,
        rectangle.Right + amount,
        rectangle.Bottom + amount);

    private static float TransformAxis(
        float value,
        float oldMinimum,
        float oldMaximum,
        float newMinimum,
        float newMaximum,
        bool changesMinimum,
        bool changesMaximum,
        float? crossAxisProgress,
        bool changesCrossMinimum,
        bool changesCrossMaximum)
    {
        float oldExtent = oldMaximum - oldMinimum;
        if (Math.Abs(oldExtent) > float.Epsilon)
        {
            float ratio = (value - oldMinimum) / oldExtent;
            return newMinimum + ratio * (newMaximum - newMinimum);
        }

        // A perfectly horizontal/vertical stroke has no scale on one axis.
        // A corner drag can still turn it into a diagonal by using progress on
        // the other axis; the opposite endpoint stays anchored. A center-edge
        // drag has no such pivot, so it translates the degenerate axis.
        float target = changesMinimum ? newMinimum : newMaximum;
        if ((changesMinimum || changesMaximum) && crossAxisProgress is float progress)
        {
            if (changesCrossMinimum)
                return target + (oldMinimum - target) * progress;
            if (changesCrossMaximum)
                return oldMinimum + (target - oldMinimum) * progress;
        }

        if (changesMinimum || changesMaximum) return target;
        return value;
    }
}
