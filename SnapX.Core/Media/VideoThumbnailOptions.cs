
// SPDX-License-Identifier: GPL-3.0-or-later


using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using SnapX.Core.Utils.Extensions;

namespace SnapX.Core.Media;

public class VideoThumbnailOptions
{
    [Category("Thumbnails"), DefaultValue(ThumbnailLocationType.DefaultFolder), Description("Select the folder for thumbnails.")]
    public ThumbnailLocationType OutputLocation { get; set; }

    [Category("Thumbnails"), DefaultValue(""), Description("Set the folder where SnapX saves thumbnails.")]
    public string? CustomOutputDirectory { get; set; }

    [Category("Thumbnails"), DefaultValue(EImageFormat.PNG), Description("Select the thumbnail image format.")]
    public EImageFormat ImageFormat { get; set; }

    [Category("Thumbnails"), DefaultValue(9), Description("Set the number of thumbnails.")]
    public int ThumbnailCount { get; set; }

    [Category("Thumbnails"), DefaultValue("_Thumbnail"), Description("Set the suffix for the thumbnail file name.")]
    public string FilenameSuffix { get; set; }

    [Category("Thumbnails"), DefaultValue(false), Description("Select a random frame for each media file.")]
    public bool RandomFrame { get; set; }

    [Category("Thumbnails"), DefaultValue(true), Description("Upload thumbnails.")]
    public bool UploadThumbnails { get; set; }

    [Category("Thumbnails"), DefaultValue(false), Description("Keep the separate image files after SnapX combines the thumbnails.")]
    public bool KeepScreenshots { get; set; }

    [Category("Thumbnails"), DefaultValue(false), Description("Open the output folder after SnapX makes the thumbnails.")]
    public bool OpenDirectory { get; set; }

    [Category("Thumbnails"), DefaultValue(512), Description("Set the maximum thumbnail width. Enter 0 to keep the original width.")]
    public int MaxThumbnailWidth { get; set; }

    [Category("Combined thumbnails"), DefaultValue(true), Description("Combine all thumbnails into one image.")]
    public bool CombineScreenshots { get; set; }

    [Category("Combined thumbnails"), DefaultValue(10), Description("Set the padding in pixels.")]
    public int Padding { get; set; }

    [Category("Combined thumbnails"), DefaultValue(10), Description("Set the space between thumbnails in pixels.")]
    public int Spacing { get; set; }

    [Category("Combined thumbnails"), DefaultValue(3), Description("Set the number of thumbnails in each row.")]
    public int ColumnCount { get; set; }

    [Category("Combined thumbnails"), DefaultValue(true), Description("Add video information to the combined image.")]
    public bool AddVideoInfo { get; set; }

    [Category("Combined thumbnails"), DefaultValue(true), Description("Add the frame time to each thumbnail.")]
    public bool AddTimestamp { get; set; }

    [Category("Combined thumbnails"), DefaultValue(true), Description("Add a shadow behind each thumbnail.")]
    public bool DrawShadow { get; set; }

    [Category("Combined thumbnails"), DefaultValue(true), Description("Add a border around each thumbnail.")]
    public bool DrawBorder { get; set; }

    public string? DefaultOutputDirectory;
    public string LastVideoPath;

    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    public VideoThumbnailOptions()
    {
        this.ApplyDefaultPropertyValues();
    }
}
