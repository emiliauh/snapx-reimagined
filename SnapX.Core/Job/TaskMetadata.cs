
// SPDX-License-Identifier: GPL-3.0-or-later


using SixLabors.ImageSharp;
using SnapX.Core.Media;
using SnapX.Core.Utils.Extensions;

namespace SnapX.Core.Job;

public class TaskMetadata : IDisposable
{
    private const int WindowInfoMaxLength = 255;

    public Image Image { get; set; }

    /// <summary>
    /// This capture workflow includes editing before any save, clipboard, or
    /// upload action, independently of the user's optional after-capture jobs.
    /// </summary>
    public bool RequiresAnnotation { get; set; }

    /// <summary>
    /// The capture surface already committed annotations before returning the
    /// image. This suppresses both automatic and configured duplicate editors.
    /// </summary>
    public bool AnnotationCompleted { get; set; }

    private string? windowTitle;

    public string? WindowTitle
    {
        get
        {
            return windowTitle;
        }
        set
        {
            windowTitle = value.Truncate(WindowInfoMaxLength);
        }
    }

    private string? processName;

    public string? ProcessName
    {
        get
        {
            return processName;
        }
        set
        {
            processName = value.Truncate(WindowInfoMaxLength);
        }
    }

    public TaskMetadata()
    {
    }

    public TaskMetadata(Image image)
    {
        Image = image;
    }
    public void UpdateInfo(WindowInfo? windowInfo)
    {
        if (windowInfo == null) return;
        WindowTitle = windowInfo.Title;
        ProcessName = windowInfo.ProcessName;
    }
    public void Dispose()
    {
        Image?.Dispose();
    }
}
