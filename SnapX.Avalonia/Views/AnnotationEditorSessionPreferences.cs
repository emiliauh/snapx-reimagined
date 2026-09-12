// SPDX-License-Identifier: GPL-3.0-or-later

using SnapX.Core.ScreenCapture;
using SixLabors.ImageSharp.PixelFormats;

namespace SnapX.Avalonia.Views;

/// <summary>
/// Remembers drawing defaults for new editor windows during this app session.
/// No images, annotation text, or document state are retained. Explicit setters
/// prevent an older window from overwriting unrelated preferences when closed.
/// </summary>
internal sealed class AnnotationEditorSessionPreferences
{
    internal static AnnotationEditorSessionPreferences Shared { get; } = new();
    private const int RecentColorLimit = 4;
    private readonly object sync = new();
    private readonly List<Rgba32> recentColors = [];
    private AnnotationTool tool = AnnotationTool.Select;
    private Rgba32 color = new(225, 38, 38);
    private float strokeWidth = 4;

    internal readonly record struct Snapshot(
        AnnotationTool Tool, Rgba32 Color, float StrokeWidth, Rgba32[] RecentColors);

    internal Snapshot Read()
    {
        lock (sync)
            return new(tool, color, strokeWidth, recentColors.ToArray());
    }

    internal void SetTool(AnnotationTool value)
    {
        if (value is < AnnotationTool.Select or > AnnotationTool.Text)
            throw new ArgumentOutOfRangeException(nameof(value));
        lock (sync) tool = value;
    }

    internal void SetColor(Rgba32 value)
    {
        lock (sync) color = Opaque(value);
    }

    internal void SetStrokeWidth(float value)
    {
        if (!float.IsFinite(value) || value < 1 || value > 40)
            throw new ArgumentOutOfRangeException(nameof(value));
        lock (sync) strokeWidth = value;
    }

    /// <summary>
    /// Call after finishing a color choice, rather than on every spectrum drag.
    /// Returns an independent, newest-first snapshot for the swatch controls.
    /// Does not change the drawing default: committing an older window's color
    /// on close must not overwrite a more recent choice made in another window.
    /// </summary>
    internal Rgba32[] RememberColor(Rgba32 value)
    {
        value = Opaque(value);
        lock (sync)
        {
            recentColors.Remove(value);
            recentColors.Insert(0, value);
            if (recentColors.Count > RecentColorLimit)
                recentColors.RemoveAt(RecentColorLimit);
            return recentColors.ToArray();
        }
    }

    private static Rgba32 Opaque(Rgba32 value) => new(value.R, value.G, value.B, 255);
}
