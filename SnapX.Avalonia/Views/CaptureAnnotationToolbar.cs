// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentIcons.Avalonia.Fluent;
using FluentIcons.Common;
using SnapX.Core.ImageEffects.Annotations;
using AvaloniaColor = Avalonia.Media.Color;

namespace SnapX.Avalonia.Views;

public sealed record AnnotateOverlayLayout(PixelRect Bounds, int ToolbarTopMargin);

/// <summary>
/// ShareX-style compact icon toolbar used by live region capture and post-crop annotate overlays.
/// </summary>
public sealed class CaptureAnnotationToolbar : Border
{
    public const int DefaultTopMargin = 30;

    private readonly Dictionary<ImageAnnotation.Tool, Button> _toolButtons = new();
    private ImageAnnotation.Tool _activeTool = ImageAnnotation.Tool.Rectangle;

    private CaptureAnnotationToolbar()
    {
        Background = new SolidColorBrush(AvaloniaColor.FromArgb(0xCC, 0x25, 0x25, 0x26));
        BorderBrush = new SolidColorBrush(AvaloniaColor.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(3);
        Padding = new Thickness(2, 1);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        IsHitTestVisible = true;
    }

    public ImageAnnotation.Tool ActiveTool => _activeTool;

    public static CaptureAnnotationToolbar CreateAnnotateToolbar(
        Action<ImageAnnotation.Tool> onToolSelected,
        Action onUndo,
        Action onSave,
        Action onCancel,
        Action<string>? onTextChanged = null,
        bool includeCrop = true)
    {
        var toolbar = new CaptureAnnotationToolbar();
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };

        toolbar.AddToolButton(tools, Symbol.Square, "Rectangle", ImageAnnotation.Tool.Rectangle, onToolSelected);
        toolbar.AddToolButton(tools, Symbol.Blur, "Blur", ImageAnnotation.Tool.Redaction, onToolSelected);
        toolbar.AddToolButton(tools, Symbol.Pen, "Freehand", ImageAnnotation.Tool.Freehand, onToolSelected);
        toolbar.AddToolButton(tools, Symbol.ArrowRight, "Arrow", ImageAnnotation.Tool.Arrow, onToolSelected);
        toolbar.AddToolButton(tools, Symbol.TextT, "Text", ImageAnnotation.Tool.Text, onToolSelected);
        if (includeCrop)
        {
            toolbar.AddToolButton(tools, Symbol.Crop, "Crop", ImageAnnotation.Tool.Crop, onToolSelected);
        }

        tools.Children.Add(toolbar.CreateIconButton(Symbol.ArrowUndo, "Undo", onUndo));
        row.Children.Add(tools);

        if (onTextChanged is not null)
        {
            var textBox = new TextBox
            {
                Width = 120,
                MinHeight = 24,
                MaxHeight = 24,
                Padding = new Thickness(4, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Watermark = "Text"
            };
            textBox.TextChanged += (_, _) => onTextChanged(textBox.Text ?? string.Empty);
            row.Children.Add(textBox);
        }

        row.Children.Add(toolbar.CreateIconButton(Symbol.Checkmark, "Save (Enter)", onSave));
        row.Children.Add(toolbar.CreateIconButton(Symbol.Dismiss, "Cancel (Esc)", onCancel));
        toolbar.Child = row;
        toolbar.SetActiveTool(ImageAnnotation.Tool.Rectangle);
        return toolbar;
    }

    public void SetActiveTool(ImageAnnotation.Tool tool)
    {
        _activeTool = tool;
        UpdateHighlight();
    }

    public static bool IsPointerOverToolbar(object? source, Visual toolbarHost)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        for (Visual? current = visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, toolbarHost))
            {
                return true;
            }
        }

        return false;
    }

    private void AddToolButton(
        StackPanel panel,
        Symbol symbol,
        string tip,
        ImageAnnotation.Tool tool,
        Action<ImageAnnotation.Tool> onToolSelected)
    {
        Button button = CreateIconButton(symbol, tip, () =>
        {
            SetActiveTool(tool);
            onToolSelected(tool);
        });
        panel.Children.Add(button);
        _toolButtons[tool] = button;
    }

    private Button CreateIconButton(Symbol symbol, string tip, Action onClick)
    {
        var button = new Button
        {
            Width = 24,
            Height = 24,
            MinWidth = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(2),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            Content = new SymbolIcon
            {
                Symbol = symbol,
                FontSize = 12,
                IconVariant = IconVariant.Regular
            }
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void UpdateHighlight()
    {
        foreach (KeyValuePair<ImageAnnotation.Tool, Button> entry in _toolButtons)
        {
            bool active = _activeTool == entry.Key;
            entry.Value.Background = active
                ? new SolidColorBrush(AvaloniaColor.FromRgb(62, 62, 66))
                : Brushes.Transparent;
            entry.Value.Opacity = active ? 1 : 0.75;
        }
    }
}
