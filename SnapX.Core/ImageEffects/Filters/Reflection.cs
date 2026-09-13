
// SPDX-License-Identifier: GPL-3.0-or-later


using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using SixLabors.ImageSharp;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Extensions;

namespace SnapX.Core.ImageEffects.Filters;

internal class Reflection : ImageEffect
{
    private int percentage;

    [DefaultValue(20), Description("Set the reflection height as a percentage of the screenshot height. Enter a value from 1 through 100.")]
    public int Percentage
    {
        get
        {
            return percentage;
        }
        set
        {
            percentage = value.Clamp(1, 100);
        }
    }

    private int maxAlpha;

    [DefaultValue(255), Description("Set the first reflection opacity. Enter a value from 0 through 255.")]
    public int MaxAlpha
    {
        get
        {
            return maxAlpha;
        }
        set
        {
            maxAlpha = value.Clamp(0, 255);
        }
    }

    private int minAlpha;

    [DefaultValue(0), Description("Set the last reflection opacity. Enter a value from 0 through 255.")]
    public int MinAlpha
    {
        get
        {
            return minAlpha;
        }
        set
        {
            minAlpha = value.Clamp(0, 255);
        }
    }

    [DefaultValue(0), Description("Set the reflection offset from the bottom of the screenshot.")]
    public int Offset { get; set; }

    [DefaultValue(false), Description("Add a horizontal skew to the reflection.")]
    public bool Skew { get; set; }

    [DefaultValue(25), Description("Set the horizontal skew in pixels.")]
    public int SkewSize { get; set; }

    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    public Reflection()
    {
        this.ApplyDefaultPropertyValues();
    }

    public override Image Apply(Image img)
    {
        return ImageHelpers.DrawReflection(img, Percentage, MaxAlpha, MinAlpha, Offset, Skew, SkewSize);
    }

    protected override string? GetSummary()
    {
        return Percentage.ToString();
    }
}
