// SPDX-License-Identifier: GPL-3.0-or-later

namespace SnapX.Core.History;

/// <summary>Actions supported by the cross-platform history surface.</summary>
public enum HistoryAction
{
    CopyURL,
    CopyShortenedURL,
    CopyThumbnailURL,
    CopyDeletionURL,
    CopyFile,
    CopyImage,
    CopyText,
    CopyHTMLLink,
    CopyHTMLImage,
    CopyHTMLLinkedImage,
    CopyForumLink,
    CopyForumImage,
    CopyForumLinkedImage,
    CopyMarkdownLink,
    CopyMarkdownImage,
    CopyMarkdownLinkedImage,
    CopyFilePath,
    CopyFileName,
    CopyFileNameWithExtension,
    CopyFolder,
    ShowImagePreview,
    ShowMoreInfo
}
