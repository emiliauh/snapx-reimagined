// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SnapX.Core.ScreenCapture;
using SixLabors.ImageSharp.PixelFormats;

namespace SnapX.Avalonia.Views.Controls;

/// <summary>
/// Compact annotation controls hosted directly by the frozen region overlay.
/// Every button has an accessible name and a tooltip so the icon-only layout
/// remains understandable without consuming the selected image.
/// </summary>
internal sealed class RegionAnnotationToolbar : Border
{
    internal sealed class MovedEventArgs(double deltaX, double deltaY) : EventArgs
    {
        public double DeltaX { get; } = deltaX;
        public double DeltaY { get; } = deltaY;
    }

    private sealed record ToolChoice(string Glyph, string Name, AnnotationTool Tool);

    private static readonly ToolChoice[] ToolChoices =
    [
        new("↖", "Select, move, and resize annotations", AnnotationTool.Select),
        new("✎", "Freehand pen", AnnotationTool.Freehand),
        new("▭", "Rectangle", AnnotationTool.Rectangle),
        new("◯", "Ellipse", AnnotationTool.Ellipse),
        new("➜", "Arrow", AnnotationTool.Arrow),
        new("T", "Text", AnnotationTool.Text)
    ];

    private static readonly Rgba32[] Palette =
    [
        new(225, 38, 38), new(245, 124, 0), new(250, 204, 21), new(22, 163, 74),
        new(37, 99, 235), new(147, 51, 234), new(255, 255, 255), new(0, 0, 0)
    ];

    private readonly IReadOnlyList<AnnotationCanvas> canvases;
    private readonly AnnotationEditorSessionPreferences preferences;
    private readonly Button colorButton;
    private readonly Button undoButton;
    private readonly Button redoButton;
    private readonly Button deleteButton;
    private readonly NumericUpDown strokeWidth;
    private readonly TextBox textBox;
    private readonly Button doneButton;
    private readonly ToggleButton regionButton;
    private bool synchronizing;
    private Color? pendingColor;
    private readonly List<ToggleButton> toolButtons = [];

    public event EventHandler? Accepted;
    public event EventHandler? Cancelled;
    public event EventHandler<MovedEventArgs>? Moved;
    public event EventHandler? RegionSelectionRequested;
    public event EventHandler? AnnotationToolSelected;

    public RegionAnnotationToolbar(
        IReadOnlyList<AnnotationCanvas> annotationCanvases,
        bool enableRegionSelection = false)
    {
        if (annotationCanvases.Count == 0)
            throw new ArgumentException("At least one annotation canvas is required.", nameof(annotationCanvases));

        canvases = annotationCanvases;
        preferences = AnnotationEditorSessionPreferences.Shared;
        AnnotationEditorSessionPreferences.Snapshot defaults = preferences.Read();
        foreach (AnnotationCanvas canvas in canvases)
        {
            canvas.Tool = defaults.Tool;
            canvas.CurrentColor = defaults.Color;
            canvas.CurrentStrokeWidth = defaults.StrokeWidth;
            canvas.DocumentChanged += CanvasOnDocumentChanged;
            canvas.ColorPicked += CanvasOnColorPicked;
        }

        CornerRadius = new CornerRadius(10);
        Background = new SolidColorBrush(Color.FromArgb(242, 27, 30, 36));
        BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
        BorderThickness = new Thickness(1);
        BoxShadow = new BoxShadows(new BoxShadow
        {
            Blur = 18,
            OffsetY = 4,
            Color = Color.FromArgb(150, 0, 0, 0)
        });
        Padding = new Thickness(6);
        ClipToBounds = false;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center
        };

        row.Children.Add(CreateDragHandle());

        regionButton = new ToggleButton
        {
            Content = new TextBlock
            {
                Text = "⌗",
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            IsChecked = enableRegionSelection,
            IsVisible = enableRegionSelection,
            Width = 38,
            Height = 36,
            Padding = new Thickness(0)
        };
        ToolTip.SetTip(regionButton, "Select capture region");
        AutomationProperties.SetName(regionButton, "Select capture region");
        regionButton.Click += (_, _) => SelectRegionMode();
        row.Children.Add(regionButton);

        foreach (ToolChoice choice in ToolChoices)
        {
            var button = new ToggleButton
            {
                Content = new TextBlock
                {
                    Text = choice.Glyph,
                    FontSize = choice.Tool == AnnotationTool.Text ? 16 : 19,
                    FontWeight = choice.Tool == AnnotationTool.Text ? FontWeight.Bold : FontWeight.Normal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                IsChecked = !enableRegionSelection && defaults.Tool == choice.Tool,
                Width = 38,
                Height = 36,
                Padding = new Thickness(0),
                Tag = choice.Tool
            };
            ToolTip.SetTip(button, choice.Name);
            AutomationProperties.SetName(button, choice.Name);
            button.Click += (_, _) =>
            {
                regionButton.IsChecked = false;
                foreach (ToggleButton peer in toolButtons) peer.IsChecked = ReferenceEquals(peer, button);
                SelectTool(choice.Tool);
                AnnotationToolSelected?.Invoke(this, EventArgs.Empty);
            };
            toolButtons.Add(button);
            row.Children.Add(button);
        }

        row.Children.Add(Separator());
        colorButton = IconButton("●", "Annotation color", (_, _) => OpenColorPicker());
        SetColorButton(defaults.Color);
        row.Children.Add(colorButton);

        var pickButton = IconButton("⌖", "Pick a color from the frozen screenshot", (_, _) =>
        {
            regionButton.IsChecked = false;
            foreach (ToggleButton peer in toolButtons) peer.IsChecked = false;
            AnnotationToolSelected?.Invoke(this, EventArgs.Empty);
            foreach (AnnotationCanvas canvas in canvases) canvas.BeginColorPick();
        });
        row.Children.Add(pickButton);

        strokeWidth = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 40,
            Increment = 1,
            Value = (decimal)defaults.StrokeWidth,
            Width = 58,
            Height = 36,
            FormatString = "0"
        };
        ToolTip.SetTip(strokeWidth, "Line width");
        AutomationProperties.SetName(strokeWidth, "Annotation line width");
        strokeWidth.ValueChanged += (_, _) =>
        {
            if (synchronizing) return;
            float value = (float)(strokeWidth.Value ?? 4);
            preferences.SetStrokeWidth(value);
            foreach (AnnotationCanvas canvas in canvases)
            {
                canvas.CurrentStrokeWidth = value;
                canvas.ApplySelectedStrokeWidth();
            }
        };
        row.Children.Add(strokeWidth);

        textBox = new TextBox
        {
            Watermark = "Text",
            Text = "Text",
            Width = 112,
            Height = 36,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(textBox, "Text to place; choose the Text tool and click the image");
        AutomationProperties.SetName(textBox, "Annotation text");
        textBox.TextChanged += (_, _) =>
        {
            if (synchronizing) return;
            string value = textBox.Text ?? string.Empty;
            foreach (AnnotationCanvas canvas in canvases) canvas.CurrentText = value;
        };
        textBox.LostFocus += (_, _) =>
        {
            foreach (AnnotationCanvas canvas in canvases) canvas.ApplySelectedText();
        };
        row.Children.Add(textBox);

        row.Children.Add(Separator());
        undoButton = IconButton("↶", "Undo (Command-Z)", (_, _) => Primary.Undo());
        redoButton = IconButton("↷", "Redo (Command-Shift-Z)", (_, _) => Primary.Redo());
        deleteButton = IconButton("⌫", "Delete selected annotation", (_, _) => Primary.DeleteSelected());
        row.Children.Add(undoButton);
        row.Children.Add(redoButton);
        row.Children.Add(deleteButton);

        var cancel = IconButton("×", "Cancel screenshot (Escape)", (_, _) => Cancelled?.Invoke(this, EventArgs.Empty));
        doneButton = IconButton("✓", "Capture selected region (Enter)", (_, _) => Accepted?.Invoke(this, EventArgs.Empty));
        doneButton.Classes.Add("accent");
        doneButton.IsEnabled = !enableRegionSelection;

        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new(GridLength.Star),
                new(GridLength.Auto),
                new(GridLength.Auto)
            }
        };
        var scroller = new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        layout.Children.Add(scroller);
        Grid.SetColumn(cancel, 1);
        Grid.SetColumn(doneButton, 2);
        cancel.Margin = new Thickness(4, 0, 2, 0);
        doneButton.Margin = new Thickness(2, 0, 0, 0);
        layout.Children.Add(cancel);
        layout.Children.Add(doneButton);
        Child = layout;
        RefreshCommands();
    }

    private AnnotationCanvas Primary => canvases[0];

    public void HandleKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && canvases.Any(canvas => canvas.IsPickingColor))
        {
            foreach (AnnotationCanvas canvas in canvases) canvas.CancelColorPick();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (e.Source is Control source &&
            (source is TextBox || source.FindAncestorOfType<TextBox>() is not null)) return;

        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (command && e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Primary.Redo();
            else Primary.Undo();
            e.Handled = true;
        }
        else if (command && e.Key == Key.Y)
        {
            Primary.Redo();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back)
        {
            Primary.DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && doneButton.IsEnabled)
        {
            Accepted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    public void SetCanAccept(bool canAccept) => doneButton.IsEnabled = canAccept;

    private void SelectRegionMode()
    {
        regionButton.IsChecked = true;
        foreach (ToggleButton peer in toolButtons) peer.IsChecked = false;
        foreach (AnnotationCanvas canvas in canvases) canvas.CancelColorPick();
        RegionSelectionRequested?.Invoke(this, EventArgs.Empty);
    }

    private Control CreateDragHandle()
    {
        var handle = new Border
        {
            Width = 24,
            Height = 36,
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new TextBlock
            {
                Text = "⠿",
                FontSize = 17,
                Opacity = .72,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        ToolTip.SetTip(handle, "Drag to move the annotation toolbar");
        AutomationProperties.SetName(handle, "Move annotation toolbar");
        bool dragging = false;
        Point previous = default;
        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            previous = e.GetPosition(topLevel);
            dragging = true;
            e.Pointer.Capture(handle);
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            Point current = e.GetPosition(topLevel);
            Moved?.Invoke(this, new MovedEventArgs(current.X - previous.X, current.Y - previous.Y));
            previous = current;
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        return handle;
    }

    private void SelectTool(AnnotationTool tool)
    {
        foreach (AnnotationCanvas canvas in canvases)
        {
            canvas.CancelColorPick();
            canvas.Tool = tool;
        }
        preferences.SetTool(tool);
    }

    private void OpenColorPicker()
    {
        AnnotationEditorSessionPreferences.Snapshot snapshot = preferences.Read();
        var picker = new ColorPicker
        {
            Color = ToColor(Primary.CurrentColor),
            ColorModel = ColorModel.Rgba,
            IsAlphaEnabled = false,
            IsAlphaVisible = false,
            IsColorSpectrumVisible = true,
            IsColorComponentsVisible = true,
            IsComponentTextInputVisible = true,
            IsHexInputVisible = true,
            IsColorPaletteVisible = true,
            PaletteColors = Palette.Select(ToColor).ToArray(),
            PaletteColumnCount = Palette.Length,
            MinWidth = 300,
            Margin = new Thickness(8)
        };
        picker.ColorChanged += (_, e) =>
        {
            Rgba32 value = new(e.NewColor.R, e.NewColor.G, e.NewColor.B, 255);
            ApplyColor(value);
            pendingColor = e.NewColor;
        };

        var recent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 8, 8) };
        foreach (Rgba32 value in snapshot.RecentColors)
        {
            var swatch = new Button
            {
                Content = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(ToColor(value)),
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1)
                },
                Padding = new Thickness(5)
            };
            ToolTip.SetTip(swatch, $"Use #{value.R:X2}{value.G:X2}{value.B:X2}");
            swatch.Click += (_, _) =>
            {
                picker.Color = ToColor(value);
                ApplyColor(value);
            };
            recent.Children.Add(swatch);
        }

        var content = new StackPanel();
        content.Children.Add(picker);
        content.Children.Add(recent);
        var flyout = new Flyout { Content = content };
        flyout.Closed += (_, _) =>
        {
            if (pendingColor is { } color)
            {
                preferences.RememberColor(new Rgba32(color.R, color.G, color.B, 255));
                pendingColor = null;
            }
        };
        flyout.ShowAt(colorButton);
    }

    private void ApplyColor(Rgba32 value)
    {
        preferences.SetColor(value);
        SetColorButton(value);
        foreach (AnnotationCanvas canvas in canvases)
        {
            canvas.CurrentColor = value;
            canvas.ApplySelectedColor();
        }
    }

    private void SetColorButton(Rgba32 value)
    {
        colorButton.Foreground = new SolidColorBrush(ToColor(value));
    }

    private void CanvasOnColorPicked(object? sender, EventArgs e)
    {
        if (sender is not AnnotationCanvas source) return;
        foreach (AnnotationCanvas canvas in canvases)
        {
            canvas.CancelColorPick();
            canvas.CurrentColor = source.CurrentColor;
        }
        ApplyColor(source.CurrentColor);
        preferences.RememberColor(source.CurrentColor);
    }

    private void CanvasOnDocumentChanged(object? sender, EventArgs e)
    {
        foreach (AnnotationCanvas canvas in canvases)
        {
            if (!ReferenceEquals(canvas, sender)) canvas.InvalidateVisual();
        }

        synchronizing = true;
        try
        {
            AnnotationElement? selected = Primary.Document.Selected;
            if (selected is not null)
            {
                ApplyCurrentValuesWithoutDocumentChange(selected.Color, selected.StrokeWidth, selected.Text);
            }
            RefreshCommands();
        }
        finally
        {
            synchronizing = false;
        }
    }

    private void ApplyCurrentValuesWithoutDocumentChange(Rgba32 color, float width, string text)
    {
        foreach (AnnotationCanvas canvas in canvases)
        {
            canvas.CurrentColor = color;
            canvas.CurrentStrokeWidth = width;
            if (!string.IsNullOrWhiteSpace(text)) canvas.CurrentText = text;
        }
        SetColorButton(color);
        strokeWidth.Value = (decimal)width;
        if (!string.IsNullOrWhiteSpace(text)) textBox.Text = text;
    }

    private void RefreshCommands()
    {
        undoButton.IsEnabled = Primary.Document.CanUndo;
        redoButton.IsEnabled = Primary.Document.CanRedo;
        deleteButton.IsEnabled = Primary.Document.Selected is not null;
    }

    private static Separator Separator() => new()
    {
        Width = 1,
        Height = 26,
        Margin = new Thickness(4, 0)
    };

    private static Button IconButton(string glyph, string tooltip, EventHandler<global::Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 19,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 38,
            Height = 36,
            Padding = new Thickness(0)
        };
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += click;
        return button;
    }

    private static Color ToColor(Rgba32 value) => Color.FromRgb(value.R, value.G, value.B);
}
