// SPDX-License-Identifier: GPL-3.0-or-later

using SixLabors.ImageSharp;
using SnapX.Core.Job;

namespace SnapX.Core.ScreenCapture;

/// <summary>
/// Toolkit-neutral request for editing a captured image. The host may read the
/// source image for the lifetime of the call, but ownership remains with Core.
/// </summary>
public sealed class ImageAnnotationRequest
{
    public required Image SourceImage { get; init; }
    public required TaskSettings TaskSettings { get; init; }
}

/// <summary>
/// Result from the interactive annotation host. A cancelled result deliberately
/// carries no image so the capture pipeline can stop before clipboard, disk, or
/// network side effects occur.
/// </summary>
public sealed class ImageAnnotationResult
{
    public bool Accepted { get; init; }
    public Image? Image { get; init; }

    public static ImageAnnotationResult Cancelled { get; } = new();

    public static ImageAnnotationResult Accept(Image image) => new()
    {
        Accepted = true,
        Image = image ?? throw new ArgumentNullException(nameof(image))
    };
}

/// <summary>
/// Shared annotation gateway. The Avalonia desktop host registers one editor,
/// giving every supported desktop platform the same workflow without making
/// SnapX.Core depend on a UI toolkit.
/// </summary>
public static class AnnotationTasks
{
    private static Func<ImageAnnotationRequest, CancellationToken, Task<ImageAnnotationResult>>? editor;

    public static bool IsEditorAvailable => Volatile.Read(ref editor) is not null;

    public static void SetEditor(
        Func<ImageAnnotationRequest, CancellationToken, Task<ImageAnnotationResult>>? value)
    {
        Volatile.Write(ref editor, value);
    }

    public static async Task<ImageAnnotationResult> EditAsync(
        Image sourceImage,
        TaskSettings taskSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceImage);
        ArgumentNullException.ThrowIfNull(taskSettings);

        var currentEditor = Volatile.Read(ref editor);
        if (currentEditor is null)
        {
            throw new InvalidOperationException(
                "Image annotation was requested, but the application host did not register an annotation editor.");
        }

        ImageAnnotationResult result = await currentEditor(new ImageAnnotationRequest
        {
            SourceImage = sourceImage,
            TaskSettings = taskSettings
        }, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The annotation editor returned no result.");

        if (result.Accepted && result.Image is null)
        {
            throw new InvalidOperationException("The annotation editor accepted the edit without returning an image.");
        }

        if (!result.Accepted && result.Image is not null)
        {
            result.Image.Dispose();
            throw new InvalidOperationException("A cancelled annotation result unexpectedly contained an image.");
        }

        return result;
    }
}
