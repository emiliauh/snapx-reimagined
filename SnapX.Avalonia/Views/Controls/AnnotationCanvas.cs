// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SnapX.Core.ScreenCapture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using APoint = Avalonia.Point;
using ARect = Avalonia.Rect;
using AColor = Avalonia.Media.Color;
using Image = SixLabors.ImageSharp.Image;
using ImagePoint = SixLabors.ImageSharp.PointF;
using ImageRectangle = SixLabors.ImageSharp.RectangleF;

namespace SnapX.Avalonia.Views.Controls;

/// <summary>
/// Scaled preview and interaction surface for an original-pixel annotation
/// document. Pointer coordinates are converted back to image pixels before the
/// document is changed.
/// </summary>
public sealed class AnnotationCanvas : Control, IDisposable
{
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor HorizontalResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor VerticalResizeCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor TopLeftResizeCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRightResizeCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor BottomLeftResizeCursor = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor BottomRightResizeCursor = new(StandardCursorType.BottomRightCorner);

    private readonly Image sourceImage;
    private readonly Bitmap? preview;
    private bool dragging;
    private bool moving;
    private bool gestureCheckpointed;
    private AnnotationResizeHandle resizeHandle;
    private ImagePoint lastPoint;
    private AnnotationElement? activeElement;
    private ARect? imageViewport;
    private APoint? hoverPosition;
    private bool drawBackground = true;
    private bool drawSourceImage = true;
    private AnnotationTool tool = AnnotationTool.Select;

    public AnnotationDocument Document { get; }
    /// <summary>
    /// Destination of the complete source image in local device-independent
    /// coordinates. It may extend outside this control for a clipped display
    /// overlay. Null preserves the default centered, aspect-fit preview.
    /// Use an aspect-preserving rectangle so text and stroke widths stay uniform.
    /// </summary>
    public ARect? ImageViewport
    {
        get => imageViewport;
        set
        {
            if (value is { } rectangle &&
                (!double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y) ||
                 !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height) ||
                 rectangle.Width <= 0 || rectangle.Height <= 0))
                throw new ArgumentOutOfRangeException(nameof(value), "Image viewport must be finite with positive dimensions.");
            imageViewport = value;
            InvalidateVisual();
        }
    }

    /// <summary>Whether to paint the editor's opaque letterbox background.</summary>
    public bool DrawBackground
    {
        get => drawBackground;
        set
        {
            drawBackground = value;
            InvalidateVisual();
        }
    }
    /// <summary>
    /// Whether to paint the source screenshot. Multi-display capture overlays
    /// already show their frozen frame underneath this control, so disabling
    /// this avoids allocating and drawing one full-desktop preview per screen.
    /// </summary>
    public bool DrawSourceImage
    {
        get => drawSourceImage;
        set
        {
            drawSourceImage = value;
            InvalidateVisual();
        }
    }
    public AnnotationTool Tool
    {
        get => tool;
        set
        {
            tool = value;
            RefreshCursor();
        }
    }
    public Rgba32 CurrentColor { get; set; } = new(225, 38, 38);
    public float CurrentStrokeWidth { get; set; } = 4;
    public string CurrentText { get; set; } = "Text";
    public bool IsPickingColor { get; private set; }

    public event EventHandler? DocumentChanged;
    public event EventHandler? ColorPicked;

    public AnnotationCanvas(Image source, AnnotationDocument? document = null, bool createPreview = true)
    {
        sourceImage = source ?? throw new ArgumentNullException(nameof(source));
        Document = document ?? new AnnotationDocument();
        preview = createPreview ? App.SnapX.ConvertImageSharpImgToAvalonia(source) : null;
        Focusable = true;
        ClipToBounds = true;
    }

    public Image<Rgba32> RenderToImage() => Document.Render(sourceImage);

    public void BeginColorPick()
    {
        IsPickingColor = true;
        Cursor = CrossCursor;
        Focus();
    }

    public void CancelColorPick()
    {
        IsPickingColor = false;
        RefreshCursor();
    }

    /// <summary>Samples original screenshot pixels, never rendered annotations or the live desktop.</summary>
    public static Rgba32 SampleOriginalColor(Image image, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!float.IsFinite(x) || !float.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "Pixel coordinates must be finite.");
        int pixelX = (int)Math.Clamp(MathF.Floor(x), 0, image.Width - 1);
        int pixelY = (int)Math.Clamp(MathF.Floor(y), 0, image.Height - 1);
        Rgba32 color;
        if (image is Image<Rgba32> rgbaImage)
            color = rgbaImage[pixelX, pixelY];
        else
        {
            using Image<Rgba32> converted = image.CloneAs<Rgba32>();
            color = converted[pixelX, pixelY];
        }
        color.A = 255;
        return color;
    }

    public void Undo()
    {
        Document.Undo();
        Changed();
    }

    public void Redo()
    {
        Document.Redo();
        Changed();
    }

    public void DeleteSelected()
    {
        Document.DeleteSelected();
        Changed();
    }

    public void ApplySelectedColor()
    {
        AnnotationElement? selected = Document.Selected;
        if (selected is null || selected.Color == CurrentColor) return;
        Document.Checkpoint();
        selected.Color = CurrentColor;
        Changed();
    }

    public void ApplySelectedStrokeWidth()
    {
        AnnotationElement? selected = Document.Selected;
        if (selected is null || selected.StrokeWidth == CurrentStrokeWidth) return;
        Document.Checkpoint();
        selected.StrokeWidth = CurrentStrokeWidth;
        Changed();
    }

    public void ApplySelectedText()
    {
        AnnotationElement? selected = Document.Selected;
        if (selected is null || selected.Tool != AnnotationTool.Text ||
            string.IsNullOrWhiteSpace(CurrentText) || selected.Text == CurrentText) return;
        Document.Checkpoint();
        selected.Text = CurrentText;
        AnnotationDocument.FitTextBoundsToContent(selected);
        Changed();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DrawBackground)
            context.FillRectangle(new SolidColorBrush(AColor.FromRgb(27, 30, 36)), new ARect(Bounds.Size));
        else
            // Avalonia hit-tests a custom control's rendered geometry. Frozen
            // capture overlays omit both the background and source image, so
            // an empty document would otherwise have no pointer target at all.
            // Keep the entire surface interactive, including between strokes.
            context.FillRectangle(Brushes.Transparent, new ARect(Bounds.Size));
        ARect viewport = GetViewport();
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        if (DrawSourceImage && preview is not null)
            context.DrawImage(preview, new ARect(preview.Size), viewport);

        foreach (AnnotationElement element in Document.Elements)
            DrawElement(context, element, viewport);

        AnnotationElement? selected = Document.Selected;
        if (selected is not null)
            DrawSelection(context, selected, viewport);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        hoverPosition = point.Position;
        if (!point.Properties.IsLeftButtonPressed || !TryToImage(point.Position, out ImagePoint imagePoint)) return;
        e.Handled = true;

        if (IsPickingColor)
        {
            CurrentColor = SampleOriginalColor(sourceImage, imagePoint.X, imagePoint.Y);
            CancelColorPick();
            ColorPicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        dragging = true;
        lastPoint = imagePoint;
        e.Pointer.Capture(this);

        if (Tool == AnnotationTool.Select)
        {
            resizeHandle = Document.HitTestSelectedHandle(imagePoint, ImageTolerance(8));
            if (resizeHandle != AnnotationResizeHandle.None)
            {
                RefreshCursor();
                return;
            }

            Document.SelectedIndex = Document.HitTest(imagePoint, ImageTolerance(5));
            moving = Document.Selected is not null;
            RefreshCursor();
            Changed();
            return;
        }

        if (Tool == AnnotationTool.Text)
        {
            string text = string.IsNullOrWhiteSpace(CurrentText) ? "Text" : CurrentText.Trim();
            float fontSize = Math.Max(12, CurrentStrokeWidth * 7);
            activeElement = new AnnotationElement
            {
                Tool = Tool,
                Color = CurrentColor,
                StrokeWidth = CurrentStrokeWidth,
                Text = text,
                FontSize = fontSize,
                Bounds = new ImageRectangle(imagePoint.X, imagePoint.Y, 1, 1)
            };
            AnnotationDocument.FitTextBoundsToContent(activeElement);
            Document.Add(activeElement);
            dragging = false;
            e.Pointer.Capture(null);
            Changed();
            return;
        }

        activeElement = new AnnotationElement
        {
            Tool = Tool,
            Color = Tool == AnnotationTool.Erase
                ? SampleOriginalColor(sourceImage, imagePoint.X, imagePoint.Y)
                : CurrentColor,
            StrokeWidth = CurrentStrokeWidth,
            Bounds = new ImageRectangle(imagePoint.X, imagePoint.Y, 1, 1),
            Points = Tool is AnnotationTool.Freehand or AnnotationTool.Arrow ? [imagePoint] : []
        };
        // An arrow needs two endpoints. Keep the provisional first point out
        // of the document so a click cannot create an invisible undo entry.
        if (Tool != AnnotationTool.Arrow)
        {
            Document.Add(activeElement, select: false);
            Changed();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        hoverPosition = e.GetPosition(this);
        if (!dragging)
        {
            RefreshCursor();
            return;
        }
        e.Handled = true;
        if (!TryToImage(hoverPosition.Value, out ImagePoint current)) return;

        if (Tool == AnnotationTool.Select)
        {
            if (current == lastPoint) return;
            if ((resizeHandle != AnnotationResizeHandle.None || moving) && !gestureCheckpointed)
            {
                Document.Checkpoint();
                gestureCheckpointed = true;
            }
            if (resizeHandle != AnnotationResizeHandle.None)
                Document.ResizeSelected(resizeHandle, current, ImageTolerance(2));
            else if (moving)
                Document.MoveSelected(current.X - lastPoint.X, current.Y - lastPoint.Y);
            if (Document.Selected is { Tool: AnnotationTool.Erase } erase)
                erase.Color = AnnotationDocument.SampleRepresentativeColor(sourceImage, erase.Bounds);
            lastPoint = current;
            Changed();
            return;
        }

        if (activeElement is null) return;
        if (Tool == AnnotationTool.Freehand)
        {
            activeElement.Points.Add(current);
            activeElement.Bounds = AnnotationDocument.BoundsFromPoints(activeElement.Points);
        }
        else if (Tool == AnnotationTool.Arrow)
        {
            if (activeElement.Points.Count == 1)
            {
                if (activeElement.Points[0] == current) return;
                activeElement.Points.Add(current);
                Document.Add(activeElement, select: false);
            }
            else activeElement.Points[^1] = current;
            activeElement.Bounds = AnnotationDocument.BoundsFromPoints(activeElement.Points);
        }
        else
        {
            activeElement.Bounds = ImageRectangle.FromLTRB(lastPoint.X, lastPoint.Y, current.X, current.Y);
        }
        if (activeElement.Tool == AnnotationTool.Erase)
            activeElement.Color = AnnotationDocument.SampleRepresentativeColor(sourceImage, activeElement.Bounds);
        Changed();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        hoverPosition = e.GetPosition(this);
        // Text placement and the eyedropper complete on press, but their release
        // must still stay inside this surface rather than completing a region.
        if (e.InitialPressMouseButton == MouseButton.Left &&
            TryToImage(e.GetPosition(this), out _))
            e.Handled = true;
        if (dragging)
        {
            e.Handled = true;
            dragging = false;
            moving = false;
            gestureCheckpointed = false;
            resizeHandle = AnnotationResizeHandle.None;
            CompleteActiveElement();
            e.Pointer.Capture(null);
            Changed();
        }
        RefreshCursor();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        dragging = false;
        moving = false;
        gestureCheckpointed = false;
        resizeHandle = AnnotationResizeHandle.None;
        CompleteActiveElement();
        base.OnPointerCaptureLost(e);
        RefreshCursor();
    }

    private void CompleteActiveElement()
    {
        if (activeElement is null) return;

        // Selection handles describe finished geometry. Deferring selection
        // also keeps every canvas that shares this document (multi-display
        // overlays) from rendering an incrementally growing selection box.
        if (activeElement.Tool == AnnotationTool.Erase)
            activeElement.Color = AnnotationDocument.SampleRepresentativeColor(sourceImage, activeElement.Bounds);
        Document.Select(activeElement);
        activeElement = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (dragging) return;
        hoverPosition = null;
        RefreshCursor();
    }

    private void DrawElement(DrawingContext context, AnnotationElement element, ARect viewport)
    {
        var brush = new SolidColorBrush(ToAvaloniaColor(element.Color));
        var pen = new Pen(brush, Math.Max(1, element.StrokeWidth) * Scale(viewport));
        ARect bounds = ToView(AnnotationDocument.Normalize(element.Bounds), viewport);
        switch (element.Tool)
        {
            case AnnotationTool.Freehand:
                DrawPolyline(context, element.Points, pen, viewport);
                break;
            case AnnotationTool.Rectangle:
                context.DrawRectangle(null, pen, bounds);
                break;
            case AnnotationTool.Ellipse:
                context.DrawEllipse(null, pen, bounds.Center, bounds.Width / 2, bounds.Height / 2);
                break;
            case AnnotationTool.Arrow:
                DrawArrow(context, element, pen, viewport);
                break;
            case AnnotationTool.Text:
                string? familyName = AnnotationDocument.TextFontFamilyName;
                if (string.IsNullOrEmpty(familyName)) break;
                var text = new FormattedText(
                    element.Text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily(familyName)),
                    AnnotationDocument.GetEffectiveTextFontSize(element) * Scale(viewport),
                    brush);
                context.DrawText(text, bounds.TopLeft);
                break;
            case AnnotationTool.Blur:
                DrawBlur(context, bounds, viewport, element.EffectStrength);
                break;
            case AnnotationTool.Erase:
                context.FillRectangle(brush, bounds);
                break;
        }
    }

    private void DrawBlur(DrawingContext context, ARect bounds, ARect viewport, float strength)
    {
        if (preview is not null)
        {
            using (context.PushClip(bounds))
            using (context.PushEffect(new ImmutableBlurEffect(
                Math.Max(1, strength) * Scale(viewport)), viewport))
                context.DrawImage(preview, new ARect(preview.Size), viewport);
            return;
        }

        // Transparent frozen-desktop overlays cannot apply an Avalonia effect
        // to pixels owned by the compositor underneath them. Show an explicit
        // preview marker; the accepted image still receives the real blur.
        using (context.PushClip(bounds))
        {
            context.FillRectangle(new SolidColorBrush(AColor.FromArgb(70, 255, 255, 255)), bounds);
            var marker = new Pen(new SolidColorBrush(AColor.FromArgb(150, 255, 255, 255)), 1);
            for (double x = bounds.Left - bounds.Height; x < bounds.Right; x += 12)
                context.DrawLine(marker,
                    new APoint(x, bounds.Bottom),
                    new APoint(x + bounds.Height, bounds.Top));
        }
    }

    private void DrawSelection(DrawingContext context, AnnotationElement element, ARect viewport)
    {
        ImageRectangle imageBounds = AnnotationDocument.Normalize(element.Bounds);
        ARect bounds = ToView(imageBounds, viewport);
        var outline = new Pen(Brushes.White, 1, dashStyle: DashStyle.Dash);
        context.DrawRectangle(null, outline, bounds.Inflate(3));
        foreach ((_, ImagePoint imagePoint) in AnnotationDocument.GetHandleLocations(imageBounds))
        {
            APoint point = ToView(imagePoint, viewport);
            context.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new ARect(point.X - 4, point.Y - 4, 8, 8));
        }
    }

    private void DrawPolyline(DrawingContext context, IReadOnlyList<ImagePoint> points, Pen pen, ARect viewport)
    {
        for (int index = 1; index < points.Count; index++)
            context.DrawLine(pen, ToView(points[index - 1], viewport), ToView(points[index], viewport));
    }

    private void DrawArrow(DrawingContext context, AnnotationElement element, Pen pen, ARect viewport)
    {
        if (element.Points.Count < 2) return;
        APoint start = ToView(element.Points[0], viewport);
        APoint end = ToView(element.Points[^1], viewport);
        context.DrawLine(pen, start, end);
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        double length = Math.Max(10, Math.Max(1, element.StrokeWidth) * 4) * Scale(viewport);
        APoint a = new(end.X - length * Math.Cos(angle - .55), end.Y - length * Math.Sin(angle - .55));
        APoint b = new(end.X - length * Math.Cos(angle + .55), end.Y - length * Math.Sin(angle + .55));
        context.DrawLine(pen, a, end);
        context.DrawLine(pen, b, end);
    }

    private ARect GetViewport()
    {
        if (ImageViewport is { } viewport) return viewport;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return default;
        double scale = Math.Min(Bounds.Width / sourceImage.Width, Bounds.Height / sourceImage.Height);
        double width = sourceImage.Width * scale;
        double height = sourceImage.Height * scale;
        return new ARect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
    }

    private bool TryToImage(APoint point, out ImagePoint imagePoint)
    {
        ARect viewport = GetViewport();
        if (!viewport.Contains(point) || viewport.Width <= 0 || viewport.Height <= 0)
        {
            imagePoint = default;
            return false;
        }
        imagePoint = new ImagePoint(
            (float)Math.Clamp((point.X - viewport.X) * sourceImage.Width / viewport.Width, 0, sourceImage.Width),
            (float)Math.Clamp((point.Y - viewport.Y) * sourceImage.Height / viewport.Height, 0, sourceImage.Height));
        return true;
    }

    private float ImageTolerance(double viewPixels)
    {
        ARect viewport = GetViewport();
        return viewport.Width <= 0 ? (float)viewPixels : (float)(viewPixels * sourceImage.Width / viewport.Width);
    }

    private double Scale(ARect viewport) => viewport.Width / sourceImage.Width;
    private ARect ToView(ImageRectangle rectangle, ARect viewport)
    {
        return new ARect(
            viewport.X + rectangle.X * viewport.Width / sourceImage.Width,
            viewport.Y + rectangle.Y * viewport.Height / sourceImage.Height,
            rectangle.Width * viewport.Width / sourceImage.Width,
            rectangle.Height * viewport.Height / sourceImage.Height);
    }

    private APoint ToView(ImagePoint point, ARect viewport)
    {
        return new APoint(
            viewport.X + point.X * viewport.Width / sourceImage.Width,
            viewport.Y + point.Y * viewport.Height / sourceImage.Height);
    }

    private static AColor ToAvaloniaColor(Rgba32 color) => AColor.FromArgb(color.A, color.R, color.G, color.B);

    private void RefreshCursor()
    {
        if (IsPickingColor)
        {
            Cursor = CrossCursor;
            return;
        }

        if (dragging && Tool == AnnotationTool.Select)
        {
            Cursor = resizeHandle != AnnotationResizeHandle.None
                ? CursorForHandle(resizeHandle)
                : moving ? MoveCursor : Cursor.Default;
            return;
        }

        if (Tool != AnnotationTool.Select)
        {
            Cursor = Tool == AnnotationTool.Text ? TextCursor : CrossCursor;
            return;
        }

        if (hoverPosition is not { } position || !TryToImage(position, out ImagePoint imagePoint))
        {
            Cursor = Cursor.Default;
            return;
        }

        AnnotationResizeHandle handle = Document.HitTestSelectedHandle(imagePoint, ImageTolerance(8));
        Cursor = handle != AnnotationResizeHandle.None
            ? CursorForHandle(handle)
            : Document.HitTest(imagePoint, ImageTolerance(5)) >= 0 ? MoveCursor : Cursor.Default;
    }

    private static Cursor CursorForHandle(AnnotationResizeHandle handle) => handle switch
    {
        AnnotationResizeHandle.TopLeft => TopLeftResizeCursor,
        AnnotationResizeHandle.Top => VerticalResizeCursor,
        AnnotationResizeHandle.TopRight => TopRightResizeCursor,
        AnnotationResizeHandle.Right => HorizontalResizeCursor,
        AnnotationResizeHandle.BottomRight => BottomRightResizeCursor,
        AnnotationResizeHandle.Bottom => VerticalResizeCursor,
        AnnotationResizeHandle.BottomLeft => BottomLeftResizeCursor,
        AnnotationResizeHandle.Left => HorizontalResizeCursor,
        _ => Cursor.Default
    };

    private void Changed()
    {
        RefreshCursor();
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => preview?.Dispose();
}
