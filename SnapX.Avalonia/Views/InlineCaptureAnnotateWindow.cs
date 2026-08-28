// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SnapX.Core.ImageEffects.Annotations;
using SnapX.Core.Utils.Native;
using Image = SixLabors.ImageSharp.Image;
using Point = Avalonia.Point;
using PointF = SixLabors.ImageSharp.PointF;
using SharpColor = SixLabors.ImageSharp.Color;

namespace SnapX.Avalonia.Views;

/// <summary>
/// ShareX-style post-selection annotate surface for native-Wayland captures.
/// Region selection uses the compositor overlay (snapx-picker); this continues
/// the session as a borderless full-monitor overlay with a floating icon toolbar
/// above the cropped capture.
/// </summary>
public sealed class InlineCaptureAnnotateWindow : Window
{
    private readonly Image _sourceImage;
    private readonly WriteableBitmap _bitmap;
    private readonly AnnotationCanvas _canvas;
    private readonly AnnotateOverlayLayout? _overlayLayout;
    private readonly TaskCompletionSource<Image?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ImageAnnotation> _annotations = [];
    private readonly Stack<ImageAnnotation> _undo = new();
    private CaptureAnnotationToolbar? _toolbar;
    private bool _completed;
    private string _text = "";

    public static Task<Image?> ShowAsync(
        Image image,
        AnnotateOverlayLayout? overlayLayout = null,
        CancellationToken cancellationToken = default)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return ShowOnUIThreadAsync(image, overlayLayout, cancellationToken);
        }

        return Dispatcher.UIThread.InvokeAsync(() => ShowOnUIThreadAsync(image, overlayLayout, cancellationToken));
    }

    private static async Task<Image?> ShowOnUIThreadAsync(
        Image image,
        AnnotateOverlayLayout? overlayLayout,
        CancellationToken cancellationToken)
    {
        var window = new InlineCaptureAnnotateWindow(image, overlayLayout);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() => window.Complete(null, disposeSource: true)));
        window.Show();
        window.Activate();
        window.Focus();
        return await window._result.Task.ConfigureAwait(false);
    }

    private InlineCaptureAnnotateWindow(Image sourceImage, AnnotateOverlayLayout? overlayLayout)
    {
        _sourceImage = sourceImage;
        _overlayLayout = overlayLayout;
        _bitmap = App.SnapX.ConvertImageSharpImgToAvalonia(sourceImage);

        Title = "SnapX annotate";
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SystemDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _canvas = new AnnotationCanvas(_bitmap, () => _annotations, AddAnnotation, () => _text);

        var imageHost = new Viewbox
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _canvas
        };
        _canvas.Width = _bitmap.PixelSize.Width;
        _canvas.Height = _bitmap.PixelSize.Height;

        _toolbar = CaptureAnnotationToolbar.CreateAnnotateToolbar(
            tool => _canvas.SetTool(tool),
            Undo,
            () => Complete(ComposeResult(), disposeSource: false),
            () => Complete(_sourceImage, disposeSource: false),
            text => _text = text,
            includeCrop: true);
        _toolbar.ZIndex = 100;

        var root = new Grid
        {
            Background = new SolidColorBrush(global::Avalonia.Media.Color.FromArgb(0x99, 0, 0, 0))
        };
        root.Children.Add(imageHost);
        root.Children.Add(_toolbar);
        Content = root;

        Opened += OnOverlayOpened;

        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Complete(null, disposeSource: true);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Complete(ComposeResult(), disposeSource: false);
                    e.Handled = true;
                    break;
            }
        };

        Closed += (_, _) =>
        {
            if (!_completed)
            {
                Complete(null, disposeSource: true);
            }
        };
    }

    private async void OnOverlayOpened(object? sender, EventArgs e)
    {
        PixelRect bounds = _overlayLayout?.Bounds ?? ResolveOverlayBounds();
        int toolbarTopMargin = _overlayLayout?.ToolbarTopMargin
            ?? await RegionSelectorWindow.ResolveToolbarTopMarginAsync();

        Position = new PixelPoint(bounds.X, bounds.Y);
        Width = bounds.Width;
        Height = bounds.Height;

        if (_toolbar is not null)
        {
            _toolbar.Margin = new Thickness(0, toolbarTopMargin, 0, 0);
        }

        if (OperatingSystem.IsLinux() && LinuxAPI.IsWayland())
        {
            await RegionSelectorWindow.EnsureHyprlandAnnotateOverlayAsync(bounds);
        }
    }

    private PixelRect ResolveOverlayBounds()
    {
        var cursor = Methods.GetCursorPosition();
        var screen = Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y));
        return screen?.Bounds ?? new PixelRect(0, 0, (int)Width, (int)Height);
    }

    private void AddAnnotation(ImageAnnotation annotation, ImageAnnotation.Tool tool)
    {
        if (annotation is not { } a)
        {
            return;
        }

        _annotations.Add(a);
        if (tool != ImageAnnotation.Tool.Crop)
        {
            _undo.Push(a);
        }

        _canvas.InvalidateVisual();
        _toolbar?.SetActiveTool(tool);
    }

    private void Undo()
    {
        if (_undo.TryPop(out ImageAnnotation? last))
        {
            _annotations.Remove(last);
            _canvas.InvalidateVisual();
        }
    }

    private Image ComposeResult()
    {
        Image result = _sourceImage;
        foreach (ImageAnnotation annotation in _annotations.Where(x => x is not CropAnnotation))
        {
            Image? applied = annotation.Apply(result);
            if (applied is null)
            {
                break;
            }

            if (!ReferenceEquals(applied, result))
            {
                result.Dispose();
            }

            result = applied;
        }

        foreach (ImageAnnotation annotation in _annotations.Where(x => x is CropAnnotation))
        {
            Image? applied = annotation.Apply(result);
            if (applied is null)
            {
                break;
            }

            if (!ReferenceEquals(applied, result))
            {
                result.Dispose();
            }

            result = applied;
        }

        return result;
    }

    private void Complete(Image? result, bool disposeSource)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        if (disposeSource || result is null)
        {
            try { _sourceImage.Dispose(); } catch { /* already released */ }
        }
        else if (!ReferenceEquals(result, _sourceImage))
        {
            try { _sourceImage.Dispose(); } catch { /* already released */ }
        }

        _bitmap.Dispose();
        _result.TrySetResult(result);
        Close();
    }

    private sealed class AnnotationCanvas : Control
    {
        private readonly WriteableBitmap _bitmap;
        private readonly Func<IReadOnlyList<ImageAnnotation>> _getAnnotations;
        private readonly Action<ImageAnnotation, ImageAnnotation.Tool> _onComplete;
        private readonly Func<string> _textProvider;
        private Point _start;
        private Point _current;
        private bool _dragging;
        private ImageAnnotation.Tool _tool;
        private readonly List<Point> _freehandPoints = [];
        private long _lastInvalidateTicks;

        public AnnotationCanvas(
            WriteableBitmap bitmap,
            Func<IReadOnlyList<ImageAnnotation>> getAnnotations,
            Action<ImageAnnotation, ImageAnnotation.Tool> onComplete,
            Func<string> textProvider)
        {
            _bitmap = bitmap;
            _getAnnotations = getAnnotations;
            _onComplete = onComplete;
            _textProvider = textProvider;
            ClipToBounds = true;
        }

        public void SetTool(ImageAnnotation.Tool tool) => _tool = tool;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height));

            foreach (ImageAnnotation annotation in _getAnnotations())
            {
                DrawCommittedAnnotation(context, annotation);
            }

            if (_dragging && _tool != ImageAnnotation.Tool.Freehand)
            {
                var rect = MakeRect(_start, _current);
                switch (_tool)
                {
                    case ImageAnnotation.Tool.Rectangle:
                        DrawOutline(context, rect, Brushes.Red);
                        break;
                    case ImageAnnotation.Tool.Redaction:
                        context.FillRectangle(Brushes.Black, rect);
                        break;
                    case ImageAnnotation.Tool.Arrow:
                        context.DrawLine(new Pen(Brushes.Green, 3), _start, _current);
                        break;
                    case ImageAnnotation.Tool.Crop:
                        DrawOutline(context, rect, Brushes.Yellow);
                        break;
                }
            }

            if (_dragging && _tool == ImageAnnotation.Tool.Freehand && _freehandPoints.Count >= 2)
            {
                var pen = new Pen(Brushes.Yellow, 3);
                for (int i = 1; i < _freehandPoints.Count; i++)
                {
                    context.DrawLine(pen, _freehandPoints[i - 1], _freehandPoints[i]);
                }
            }
        }

        private static void DrawCommittedAnnotation(DrawingContext context, ImageAnnotation annotation)
        {
            switch (annotation)
            {
                case RectangleAnnotation rectangle:
                    DrawOutline(context, ToRect(rectangle.Rectangle), Brushes.Red);
                    break;
                case RedactionAnnotation redaction:
                    context.FillRectangle(Brushes.Black, ToRect(redaction.Rectangle));
                    break;
                case FreehandAnnotation freehand when freehand.Points.Count >= 2:
                {
                    var pen = new Pen(Brushes.Yellow, freehand.Thickness);
                    for (int i = 1; i < freehand.Points.Count; i++)
                    {
                        var a = freehand.Points[i - 1];
                        var b = freehand.Points[i];
                        context.DrawLine(pen, new Point(a.X, a.Y), new Point(b.X, b.Y));
                    }

                    break;
                }
                case ArrowAnnotation arrow:
                    context.DrawLine(
                        new Pen(Brushes.Green, arrow.Thickness),
                        new Point(arrow.Start.X, arrow.Start.Y),
                        new Point(arrow.End.X, arrow.End.Y));
                    break;
                case TextAnnotation text when !string.IsNullOrWhiteSpace(text.Text):
                    context.DrawText(
                        new FormattedText(
                            text.Text,
                            System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            Typeface.Default,
                            text.FontSize,
                            Brushes.White),
                        new Point(text.Position.X, text.Position.Y));
                    break;
            }
        }

        private static Rect ToRect(SixLabors.ImageSharp.Rectangle rectangle) =>
            new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

        private static void DrawOutline(DrawingContext context, Rect rect, IBrush brush)
        {
            var pen = new Pen(brush, 2);
            context.DrawLine(pen, new Point(rect.X, rect.Y), new Point(rect.Right, rect.Y));
            context.DrawLine(pen, new Point(rect.Right, rect.Y), new Point(rect.Right, rect.Bottom));
            context.DrawLine(pen, new Point(rect.Right, rect.Bottom), new Point(rect.X, rect.Bottom));
            context.DrawLine(pen, new Point(rect.X, rect.Bottom), new Point(rect.X, rect.Y));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _start = e.GetPosition(this);
            _current = _start;
            _dragging = true;
            _freehandPoints.Clear();
            _freehandPoints.Add(_start);
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging)
            {
                return;
            }

            _current = e.GetPosition(this);
            if (_tool == ImageAnnotation.Tool.Freehand)
            {
                _freehandPoints.Add(_current);
            }

            long now = Environment.TickCount64;
            if (now - _lastInvalidateTicks >= 16)
            {
                _lastInvalidateTicks = now;
                InvalidateVisual();
            }

            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging)
            {
                return;
            }

            _current = e.GetPosition(this);
            _dragging = false;
            e.Pointer.Capture(null);
            Commit();
            e.Handled = true;
        }

        private void Commit()
        {
            var rect = MakeRect(_start, _current);
            switch (_tool)
            {
                case ImageAnnotation.Tool.Rectangle:
                    _onComplete(new RectangleAnnotation
                    {
                        Rectangle = ToSharp(rect),
                        Color = SharpColor.Red,
                        Thickness = 2
                    }, _tool);
                    break;
                case ImageAnnotation.Tool.Redaction:
                    _onComplete(new RedactionAnnotation { Rectangle = ToSharp(rect) }, _tool);
                    break;
                case ImageAnnotation.Tool.Freehand:
                    _onComplete(new FreehandAnnotation
                    {
                        Points = _freehandPoints.Select(p => new PointF((float)p.X, (float)p.Y)).ToList(),
                        Color = SharpColor.Yellow,
                        Thickness = 3
                    }, _tool);
                    break;
                case ImageAnnotation.Tool.Arrow:
                    _onComplete(new ArrowAnnotation
                    {
                        Start = new PointF((float)_start.X, (float)_start.Y),
                        End = new PointF((float)_current.X, (float)_current.Y),
                        Color = SharpColor.Green,
                        Thickness = 3
                    }, _tool);
                    break;
                case ImageAnnotation.Tool.Crop:
                    _onComplete(new CropAnnotation { Rectangle = ToSharp(rect) }, _tool);
                    break;
                case ImageAnnotation.Tool.Text:
                    string value = _textProvider();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _onComplete(new TextAnnotation
                        {
                            Position = new PointF((float)_start.X, (float)_start.Y),
                            Text = value,
                            Color = SharpColor.White
                        }, _tool);
                    }
                    break;
            }

            InvalidateVisual();
        }

        private static Rect MakeRect(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            double w = Math.Abs(a.X - b.X);
            double h = Math.Abs(a.Y - b.Y);
            return new Rect(x, y, w, h);
        }

        private static SixLabors.ImageSharp.Rectangle ToSharp(Rect rect) =>
            new((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);
    }
}
