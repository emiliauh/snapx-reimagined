// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SnapX.Core;
using SnapX.Core.ImageEffects.Annotations;
using SnapX.Core.Job;
using SnapX.Core.Utils;
using Image = Avalonia.Controls.Image;
using Point = Avalonia.Point;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpColor = SixLabors.ImageSharp.Color;
using AvaloniaColor = Avalonia.Media.Color;

namespace SnapX.Avalonia.Views;

/// <summary>
/// Minimal modal image editor. It renders the captured image with a live
/// annotation overlay, supports rectangle/redaction, freehand, arrow, text and
/// crop, keeps an undo stack, and composites the accepted annotations onto the
/// source ImageSharp image on save. Cancelling returns <c>null</c> so the
/// worker keeps the original capture instead of uploading an unedited image.
/// </summary>
public sealed class CapturedImageEditorWindow : Window
{
    private readonly NeedEditImageEvent _request;
    private readonly WriteableBitmap _bitmap;
    private readonly AnnotationCanvas _canvas;
    private readonly List<ImageAnnotation> _annotations = [];
    private readonly Stack<ImageAnnotation> _undo = new();
    private bool _completed;
    private string _text = "";

    public CapturedImageEditorWindow(NeedEditImageEvent request, WriteableBitmap bitmap)
    {
        _request = request;
        _bitmap = bitmap;

        Title = "SnapX | Image editor";
        Width = Math.Min(980, bitmap.PixelSize.Width + 40);
        Height = Math.Min(720, bitmap.PixelSize.Height + 140);
        Background = new SolidColorBrush(AvaloniaColor.FromRgb(30, 30, 30));
        SystemDecorations = WindowDecorations.None;
        CanResize = true;

        _canvas = new AnnotationCanvas(bitmap, (a, tool) => AddAnnotation(a, tool), () => _text);
        _canvas.Width = bitmap.PixelSize.Width;
        _canvas.Height = bitmap.PixelSize.Height;

        var scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var toolbar = BuildToolbar();
        var buttons = BuildButtons();

        var layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            }
        };
        layout.Children.Add(toolbar);
        Grid.SetRow(toolbar, 0);
        layout.Children.Add(scroll);
        Grid.SetRow(scroll, 1);
        layout.Children.Add(buttons);
        Grid.SetRow(buttons, 2);

        Content = new Border
        {
            Background = new SolidColorBrush(AvaloniaColor.FromRgb(30, 30, 30)),
            Child = layout
        };

        Closed += (_, _) =>
        {
            // If the window is closed by the OS or task manager without an
            // explicit Save/Cancel, complete the request with null so the worker
            // does not hang. Annotations are discarded (treated as cancel).
            Complete(null);
        };
    }

    private Control BuildToolbar()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        foreach ((string label, ImageAnnotation.Tool tool) in
                 new[]
                 {
                     ("Rect", ImageAnnotation.Tool.Rectangle),
                     ("Redact", ImageAnnotation.Tool.Redaction),
                     ("Freehand", ImageAnnotation.Tool.Freehand),
                     ("Arrow", ImageAnnotation.Tool.Arrow),
                     ("Text", ImageAnnotation.Tool.Text),
                     ("Crop", ImageAnnotation.Tool.Crop)
                 })
        {
            var button = new Button { Content = label, Margin = new Thickness(2) };
            button.Click += (_, _) => _canvas.SetTool(tool);
            panel.Children.Add(button);
        }

        var undo = new Button { Content = "Undo", Margin = new Thickness(2) };
        undo.Click += (_, _) => Undo();
        panel.Children.Add(undo);

        var textBox = new TextBox
        {
            Watermark = "Text",
            Width = 160,
            Margin = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center
        };
        textBox.TextChanged += (_, _) => _text = textBox.Text ?? "";
        panel.Children.Add(textBox);

        return panel;
    }

    private Control BuildButtons()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancel = new Button { Content = "Cancel", Margin = new Thickness(2) };
        cancel.Click += (_, _) => Complete(null);
        panel.Children.Add(cancel);

        var save = new Button { Content = "Save", Margin = new Thickness(2) };
        save.Click += (_, _) => Complete(ComposeResult());
        panel.Children.Add(save);

        return panel;
    }

    private void AddAnnotation(ImageAnnotation annotation, ImageAnnotation.Tool tool)
    {
        if (annotation is not { } a) return;
        _annotations.Add(a);
        if (tool != ImageAnnotation.Tool.Crop)
        {
            _undo.Push(a);
        }
        _canvas.InvalidateVisual();
    }

    private void Undo()
    {
        if (_undo.TryPop(out ImageAnnotation? last))
        {
            _annotations.Remove(last);
            _canvas.InvalidateVisual();
        }
    }

    private SharpImage? ComposeResult()
    {
        SharpImage clone = _request.Image.Clone(ctx => { });
        foreach (ImageAnnotation annotation in _annotations)
        {
            SharpImage? applied = annotation.Apply(clone);
            if (applied == null)
            {
                break;
            }
            if (!ReferenceEquals(applied, clone))
            {
                clone.Dispose();
            }
            clone = applied;
        }
        return clone;
    }

    private void Complete(SharpImage? result)
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        _request.Complete(result);
        _bitmap.Dispose();
        Close();
    }

    private sealed class AnnotationCanvas : Control
    {
        private readonly WriteableBitmap _bitmap;
        private readonly Action<ImageAnnotation, ImageAnnotation.Tool> _onComplete;
        private readonly Func<string> _textProvider;
        private Point _start;
        private Point _current;
        private bool _dragging;
        private ImageAnnotation.Tool _tool;
        private readonly List<Point> _freehandPoints = [];

        public AnnotationCanvas(WriteableBitmap bitmap, Action<ImageAnnotation, ImageAnnotation.Tool> onComplete, Func<string> textProvider)
        {
            _bitmap = bitmap;
            _onComplete = onComplete;
            _textProvider = textProvider;
            ClipToBounds = true;
        }

        public ImageAnnotation.Tool Tool
        {
            get => _tool;
            set => _tool = value;
        }

        public void SetTool(ImageAnnotation.Tool tool) => _tool = tool;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            // The canvas is sized to the bitmap's native pixel size, so
            // annotations captured in control coordinates map 1:1 onto the
            // image. Draw at native size rather than stretching.
            context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height));

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
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _start = e.GetPosition(this);
            _current = _start;
            _dragging = true;
            _freehandPoints.Clear();
            _freehandPoints.Add(_start);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging) return;
            _current = e.GetPosition(this);
            if (_tool == ImageAnnotation.Tool.Freehand)
            {
                _freehandPoints.Add(_current);
            }
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging) return;
            _current = e.GetPosition(this);
            _dragging = false;
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
