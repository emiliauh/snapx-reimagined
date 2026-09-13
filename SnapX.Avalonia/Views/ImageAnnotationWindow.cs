// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SnapX.Avalonia.Views.Controls;
using SnapX.Core.ScreenCapture;
using SnapX.Core.Utils.Native;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

namespace SnapX.Avalonia.Views;

/// <summary>
/// Cross-platform Avalonia screenshot editor. The surface keeps annotations in
/// source-image coordinates and renders only after the user accepts the edit.
/// </summary>
public sealed class ImageAnnotationWindow : Window
{
    private sealed record ColorChoice(string Name, Rgba32 Value)
    {
        public override string ToString() => Name;
    }

    private static readonly ColorChoice[] Colors =
    [
        new("Red", new Rgba32(225, 38, 38)),
        new("Orange", new Rgba32(245, 124, 0)),
        new("Yellow", new Rgba32(250, 204, 21)),
        new("Green", new Rgba32(22, 163, 74)),
        new("Blue", new Rgba32(37, 99, 235)),
        new("Purple", new Rgba32(147, 51, 234)),
        new("White", new Rgba32(255, 255, 255)),
        new("Black", new Rgba32(0, 0, 0))
    ];

    private readonly AnnotationCanvas canvas;
    private readonly TaskCompletionSource<ImageAnnotationResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Button undoButton;
    private readonly Button redoButton;
    private readonly Button deleteButton;
    private bool accepted;

    private ImageAnnotationWindow(Image source)
    {
        Title = "SnapX: Annotate a screenshot";
        Width = Math.Clamp(source.Width + 96, 760, 1440);
        Height = Math.Clamp(source.Height + 180, 600, 1000);
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(22, 25, 30));

        var preferences = AnnotationEditorSessionPreferences.Shared;
        var defaults = preferences.Read();
        canvas = new AnnotationCanvas(source)
        {
            Margin = new Thickness(12),
            Tool = defaults.Tool,
            CurrentColor = defaults.Color,
            CurrentStrokeWidth = defaults.StrokeWidth
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions
            {
                new(GridLength.Auto),
                new(GridLength.Star),
                new(GridLength.Auto)
            }
        };

        var toolbar = new WrapPanel
        {
            Margin = new Thickness(12, 10),
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (OperatingSystem.IsLinux() && LinuxAPI.IsWayland())
        {
            toolbar.Children.Add(new TextBlock
            {
                Text = "The Wayland selection is complete. Add annotations before SnapX saves the screenshot.",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 10, 0),
                Opacity = .78
            });
        }

        AddTool(toolbar, "Select", AnnotationTool.Select);
        AddTool(toolbar, "Pen", AnnotationTool.Freehand);
        AddTool(toolbar, "Rectangle", AnnotationTool.Rectangle);
        AddTool(toolbar, "Ellipse", AnnotationTool.Ellipse);
        AddTool(toolbar, "Arrow", AnnotationTool.Arrow);
        AddTool(toolbar, "Text", AnnotationTool.Text);
        AddTool(toolbar, "Blur", AnnotationTool.Blur);
        AddTool(toolbar, "Erase", AnnotationTool.Erase);

        toolbar.Children.Add(new Separator { Margin = new Thickness(8, 0), Height = 30 });
        toolbar.Children.Add(new TextBlock
        {
            Text = "Color",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0)
        });
        var colorPicker = new ColorPicker
        {
            Color = Color.FromRgb(canvas.CurrentColor.R, canvas.CurrentColor.G, canvas.CurrentColor.B),
            ColorModel = ColorModel.Rgba,
            IsAlphaEnabled = false,
            IsAlphaVisible = false,
            IsColorSpectrumVisible = true,
            IsColorComponentsVisible = true,
            IsComponentTextInputVisible = true,
            IsHexInputVisible = true,
            IsColorPaletteVisible = true,
            PaletteColors = Colors.Select(choice => Color.FromRgb(choice.Value.R, choice.Value.G, choice.Value.B)).ToArray(),
            PaletteColumnCount = 8,
            MinWidth = 76,
            Margin = new Thickness(2)
        };
        global::Avalonia.Automation.AutomationProperties.SetName(colorPicker, "Annotation color: spectrum, RGB and hex");
        ToolTip.SetTip(colorPicker, "Choose any annotation color using the spectrum, RGB values, hex code, or palette");
        var recentSwatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
        Color? pendingColor = null;
        bool synchronizingToolbar = false;

        void RefreshRecentColors(IEnumerable<Rgba32> colors)
        {
            recentSwatches.Children.Clear();
            foreach (Rgba32 value in colors)
            {
                Color recent = Color.FromRgb(value.R, value.G, value.B);
                string hex = $"#{recent.R:X2}{recent.G:X2}{recent.B:X2}";
                var swatch = new Button
                {
                    Content = new Border { Background = new SolidColorBrush(recent), Width = 16, Height = 16, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                    Padding = new Thickness(5),
                    MinWidth = 28,
                    MinHeight = 28
                };
                global::Avalonia.Automation.AutomationProperties.SetName(swatch, $"Recent annotation color {hex}");
                ToolTip.SetTip(swatch, $"Recent color {hex}");
                swatch.Click += (_, _) =>
                {
                    colorPicker.Color = recent;
                    preferences.SetColor(value);
                    pendingColor = recent;
                    CommitColor();
                };
                recentSwatches.Children.Add(swatch);
            }
        }

        void CommitColor()
        {
            if (pendingColor is not { } color) return;
            pendingColor = null;
            RefreshRecentColors(preferences.RememberColor(new Rgba32(color.R, color.G, color.B)));
        }

        RefreshRecentColors(defaults.RecentColors);
        colorPicker.ColorChanged += (_, e) =>
        {
            if (synchronizingToolbar) return;
            canvas.CurrentColor = new Rgba32(e.NewColor.R, e.NewColor.G, e.NewColor.B, 255);
            preferences.SetColor(canvas.CurrentColor);
            pendingColor = e.NewColor;
            canvas.ApplySelectedColor();
        };
        colorPicker.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!colorPicker.IsKeyboardFocusWithin) CommitColor();
        });
        toolbar.Children.Add(colorPicker);
        toolbar.Children.Add(recentSwatches);
        var eyedropper = AddButton(toolbar, "Pick color", (_, _) => canvas.BeginColorPick());
        ToolTip.SetTip(eyedropper, "Select a pixel in the original screenshot to use its color. Press Escape to cancel.");
        global::Avalonia.Automation.AutomationProperties.SetName(eyedropper, "Pick color from original screenshot");
        canvas.ColorPicked += (_, _) =>
        {
            colorPicker.Color = Color.FromRgb(canvas.CurrentColor.R, canvas.CurrentColor.G, canvas.CurrentColor.B);
            preferences.SetColor(canvas.CurrentColor);
            pendingColor = colorPicker.Color;
            CommitColor();
        };

        toolbar.Children.Add(new TextBlock
        {
            Text = "Width",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0)
        });
        var stroke = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 40,
            Value = (decimal)canvas.CurrentStrokeWidth,
            Increment = 1,
            Width = 72,
            Margin = new Thickness(2)
        };
        stroke.ValueChanged += (_, _) =>
        {
            if (synchronizingToolbar) return;
            canvas.CurrentStrokeWidth = (float)(stroke.Value ?? 4);
            preferences.SetStrokeWidth(canvas.CurrentStrokeWidth);
            canvas.ApplySelectedStrokeWidth();
        };
        toolbar.Children.Add(stroke);

        var textBox = new TextBox
        {
            Watermark = "Annotation text",
            Text = "Text",
            Width = 170,
            Margin = new Thickness(8, 2, 2, 2)
        };
        bool textChanged = false;
        textBox.TextChanged += (_, _) =>
        {
            if (synchronizingToolbar) return;
            canvas.CurrentText = textBox.Text ?? string.Empty;
            textChanged = true;
        };
        textBox.LostFocus += (_, _) =>
        {
            if (textChanged) canvas.ApplySelectedText();
            textChanged = false;
        };
        toolbar.Children.Add(textBox);

        undoButton = AddButton(toolbar, "Undo", (_, _) => canvas.Undo());
        redoButton = AddButton(toolbar, "Redo", (_, _) => canvas.Redo());
        deleteButton = AddButton(toolbar, "Delete", (_, _) => canvas.DeleteSelected());

        void SynchronizeToolbarFromSelection()
        {
            RefreshCommands();
            AnnotationElement? selected = canvas.Document.Selected;
            if (selected is null) return;

            synchronizingToolbar = true;
            try
            {
                Color selectedColor = Color.FromRgb(selected.Color.R, selected.Color.G, selected.Color.B);
                colorPicker.Color = selectedColor;
                stroke.Value = (decimal)selected.StrokeWidth;
                canvas.CurrentColor = selected.Color;
                canvas.CurrentStrokeWidth = selected.StrokeWidth;
                if (selected.Tool == AnnotationTool.Text)
                {
                    textBox.Text = selected.Text;
                    canvas.CurrentText = selected.Text;
                    textChanged = false;
                }
            }
            finally
            {
                synchronizingToolbar = false;
            }
        }

        canvas.DocumentChanged += (_, _) => SynchronizeToolbarFromSelection();

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new(GridLength.Star),
                new(GridLength.Auto),
                new(GridLength.Auto)
            },
            Margin = new Thickness(12, 0, 12, 12)
        };
        footer.Children.Add(new TextBlock
        {
            Text = $"{source.Width:N0} by {source.Height:N0} pixels. Drag the selected annotations to move them. Drag a white handle to change their size.",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = .72
        });
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(6, 0) };
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(cancel);
        var done = new Button
        {
            Content = "Done",
            MinWidth = 100,
            Margin = new Thickness(6, 0),
            Classes = { "accent" }
        };
        done.Click += (_, _) => Accept();
        Grid.SetColumn(done, 2);
        footer.Children.Add(done);

        Grid.SetRow(toolbar, 0);
        Grid.SetRow(canvas, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(toolbar);
        root.Children.Add(canvas);
        root.Children.Add(footer);
        Content = root;

        KeyDown += OnKeyDown;
        Closing += (_, _) =>
        {
            CommitColor();
            if (!accepted) completion.TrySetResult(ImageAnnotationResult.Cancelled);
        };
        Closed += (_, _) => canvas.Dispose();
        RefreshCommands();
    }

    public static async Task<ImageAnnotationResult> EditAsync(
        Image source,
        CancellationToken cancellationToken = default)
    {
        var window = new ImageAnnotationWindow(source);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(window.Close));

        if (App.MyMainWindow is { IsVisible: true } owner)
            window.Show(owner);
        else
            window.Show();

        return await window.completion.Task;
    }

    private void AddTool(Panel toolbar, string label, AnnotationTool tool)
    {
        var option = new RadioButton
        {
            Content = label,
            GroupName = "AnnotationTools",
            IsChecked = canvas.Tool == tool,
            Margin = new Thickness(2),
            Padding = new Thickness(10, 6)
        };
        option.Click += (_, _) =>
        {
            canvas.CancelColorPick();
            canvas.Tool = tool;
            AnnotationEditorSessionPreferences.Shared.SetTool(tool);
        };
        toolbar.Children.Add(option);
    }

    private static Button AddButton(Panel panel, string label, EventHandler<RoutedEventArgs> click)
    {
        var button = new Button { Content = label, Margin = new Thickness(4, 2), Padding = new Thickness(10, 6) };
        button.Click += click;
        panel.Children.Add(button);
        return button;
    }

    private void Accept()
    {
        if (accepted) return;
        try
        {
            Image result = canvas.RenderToImage();
            accepted = true;
            completion.TrySetResult(ImageAnnotationResult.Accept(result));
            Close();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            Close();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && canvas.IsPickingColor)
        {
            canvas.CancelColorPick();
            e.Handled = true;
            return;
        }
        // Editing annotation text or RGB/hex input must retain normal text
        // selection, deletion and undo rather than manipulating the document.
        if (e.Source is TextBox || e.Source is Control control && control.FindAncestorOfType<TextBox>() is not null)
            return;
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (command && e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) canvas.Redo();
            else canvas.Undo();
            e.Handled = true;
        }
        else if (command && e.Key == Key.Y)
        {
            canvas.Redo();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back)
        {
            canvas.DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void RefreshCommands()
    {
        undoButton.IsEnabled = canvas.Document.CanUndo;
        redoButton.IsEnabled = canvas.Document.CanRedo;
        deleteButton.IsEnabled = canvas.Document.Selected is not null;
    }
}
