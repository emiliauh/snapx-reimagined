// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using SnapX.Core.Utils;

namespace SnapX.Core.History;

/// <summary>
/// Coordinates history actions without taking a dependency on a UI framework.
/// Clipboard and presentation work is delegated to the active frontend.
/// </summary>
public sealed class HistoryItemManager
{
    private const long MaximumTextFileBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".ogv", ".webm"
    };

    public event Func<HistoryItem[]?>? GetHistoryItems;
    public event Action<string>? CopyTextRequested;
    public event Action<IReadOnlyList<string>>? CopyFilesRequested;
    public event Action<string>? CopyImageRequested;
    public event Action<HistoryItem>? ImagePreviewRequested;
    public event Action<HistoryItem>? VideoPreviewRequested;
    public event Action<HistoryItem>? MoreInfoRequested;
    public event Action<string>? OperationFailed;

    public HistoryItem? HistoryItem { get; private set; }
    public int SelectedItemCount { get; private set; }
    public bool HideShowMoreInfoButton { get; }

    public bool IsURLExist { get; private set; }
    public bool IsShortenedURLExist { get; private set; }
    public bool IsThumbnailURLExist { get; private set; }
    public bool IsDeletionURLExist { get; private set; }
    public bool IsImageURL { get; private set; }
    public bool IsTextURL { get; private set; }
    public bool IsFilePathValid { get; private set; }
    public bool IsFileExist { get; private set; }
    public bool IsImageFile { get; private set; }
    public bool IsVideoFile { get; private set; }
    public bool IsTextFile { get; private set; }

    private readonly Action<string>? uploadFile;
    private readonly Action<string>? editImage;
    private readonly Action<string>? pinToScreen;

    public HistoryItemManager(
        Action<string>? uploadFile,
        Action<string>? editImage,
        Action<string>? pinToScreen,
        bool hideShowMoreInfoButton = false)
    {
        this.uploadFile = uploadFile;
        this.editImage = editImage;
        this.pinToScreen = pinToScreen;
        HideShowMoreInfoButton = hideShowMoreInfoButton;
    }

    public HistoryItem? UpdateSelectedHistoryItem()
    {
        HistoryItem[] historyItems = GetSelectedItems();
        SelectedItemCount = historyItems.Length;
        HistoryItem = historyItems.FirstOrDefault();

        ResetSelectionState();

        if (HistoryItem is null)
        {
            return null;
        }

        IsURLExist = HasValue(HistoryItem.URL);
        IsShortenedURLExist = HasValue(HistoryItem.ShortenedURL);
        IsThumbnailURLExist = HasValue(HistoryItem.ThumbnailURL);
        IsDeletionURLExist = HasValue(HistoryItem.DeletionURL);
        IsImageURL = IsURLExist && IsImagePath(HistoryItem.URL);
        IsTextURL = IsURLExist && IsTextPath(HistoryItem.URL);
        IsFilePathValid = IsValidFilePath(HistoryItem.FilePath);
        IsFileExist = IsFilePathValid && SafeFileExists(HistoryItem.FilePath);
        IsImageFile = IsFileExist && IsImagePath(HistoryItem.FilePath);
        IsVideoFile = IsFileExist && IsVideoPath(HistoryItem.FilePath);
        IsTextFile = IsFileExist && IsTextPath(HistoryItem.FilePath);

        return HistoryItem;
    }

    public HistoryItem[] OnGetHistoryItems() => GetSelectedItems();

    public void Execute(HistoryAction action)
    {
        UpdateSelectedHistoryItem();

        switch (action)
        {
            case HistoryAction.CopyURL: CopyURL(); break;
            case HistoryAction.CopyShortenedURL: CopyShortenedURL(); break;
            case HistoryAction.CopyThumbnailURL: CopyThumbnailURL(); break;
            case HistoryAction.CopyDeletionURL: CopyDeletionURL(); break;
            case HistoryAction.CopyFile: CopyFile(); break;
            case HistoryAction.CopyImage: CopyImage(); break;
            case HistoryAction.CopyText: CopyText(); break;
            case HistoryAction.CopyHTMLLink: CopyHTMLLink(); break;
            case HistoryAction.CopyHTMLImage: CopyHTMLImage(); break;
            case HistoryAction.CopyHTMLLinkedImage: CopyHTMLLinkedImage(); break;
            case HistoryAction.CopyForumLink: CopyForumLink(); break;
            case HistoryAction.CopyForumImage: CopyForumImage(); break;
            case HistoryAction.CopyForumLinkedImage: CopyForumLinkedImage(); break;
            case HistoryAction.CopyMarkdownLink: CopyMarkdownLink(); break;
            case HistoryAction.CopyMarkdownImage: CopyMarkdownImage(); break;
            case HistoryAction.CopyMarkdownLinkedImage: CopyMarkdownLinkedImage(); break;
            case HistoryAction.CopyFilePath: CopyFilePath(); break;
            case HistoryAction.CopyFileName: CopyFileName(); break;
            case HistoryAction.CopyFileNameWithExtension: CopyFileNameWithExtension(); break;
            case HistoryAction.CopyFolder: CopyFolder(); break;
            case HistoryAction.ShowImagePreview: ShowImagePreview(); break;
            case HistoryAction.ShowMoreInfo: ShowMoreInfo(); break;
            default: throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    public void OpenURL()
    {
        if (HistoryItem is not null && IsURLExist) URLHelpers.OpenURL(HistoryItem.URL);
    }

    public void OpenShortenedURL()
    {
        if (HistoryItem is not null && IsShortenedURLExist) URLHelpers.OpenURL(HistoryItem.ShortenedURL);
    }

    public void OpenThumbnailURL()
    {
        if (HistoryItem is not null && IsThumbnailURLExist) URLHelpers.OpenURL(HistoryItem.ThumbnailURL);
    }

    public void OpenDeletionURL()
    {
        if (HistoryItem is not null && IsDeletionURLExist) URLHelpers.OpenURL(HistoryItem.DeletionURL);
    }

    public void OpenFile()
    {
        if (HistoryItem is not null && IsFileExist) FileHelpers.OpenFile(HistoryItem.FilePath);
    }

    public void OpenFolder()
    {
        if (HistoryItem is not null && IsFileExist) FileHelpers.OpenFolderWithFile(HistoryItem.FilePath);
    }

    public void TryOpen()
    {
        if (HistoryItem is null) return;

        if (IsShortenedURLExist) URLHelpers.OpenURL(HistoryItem.ShortenedURL);
        else if (IsURLExist) URLHelpers.OpenURL(HistoryItem.URL);
        else if (IsFileExist) FileHelpers.OpenFile(HistoryItem.FilePath);
    }

    public void CopyURL() => CopySelectedText(x => x.URL);
    public void CopyShortenedURL() => CopySelectedText(x => x.ShortenedURL);
    public void CopyThumbnailURL() => CopySelectedText(x => x.ThumbnailURL);
    public void CopyDeletionURL() => CopySelectedText(x => x.DeletionURL);

    public void CopyFile()
    {
        string[] paths = GetSelectedItems()
            .Select(x => x.FilePath)
            .Where(SafeFileExists)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (paths.Length > 0) CopyFilesRequested?.Invoke(paths);
    }

    public void CopyImage()
    {
        if (HistoryItem is not null && IsImageFile && HistoryItem.FilePath is not null)
        {
            CopyImageRequested?.Invoke(HistoryItem.FilePath);
        }
    }

    public void CopyText()
    {
        if (HistoryItem is null || !IsTextFile || HistoryItem.FilePath is null) return;

        try
        {
            var fileInfo = new FileInfo(HistoryItem.FilePath);
            if (fileInfo.Length > MaximumTextFileBytes)
            {
                Fail($"The selected text file is larger than {MaximumTextFileBytes / 1024 / 1024} MiB.");
                return;
            }

            RequestText(File.ReadAllText(HistoryItem.FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Fail($"Could not read the selected text file: {ex.Message}");
        }
    }

    public void CopyHTMLLink() => CopySelectedText(x => HasValue(x.URL)
        ? $"<a href=\"{Html(x.URL)}\">{Html(x.URL)}</a>" : null);

    public void CopyHTMLImage() => CopySelectedText(x => HasValue(x.URL) && IsImagePath(x.URL)
        ? $"<img src=\"{Html(x.URL)}\"/>" : null);

    public void CopyHTMLLinkedImage() => CopySelectedText(x =>
        HasValue(x.URL) && IsImagePath(x.URL) && HasValue(x.ThumbnailURL)
            ? $"<a href=\"{Html(x.URL)}\"><img src=\"{Html(x.ThumbnailURL)}\"/></a>"
            : null);

    public void CopyForumLink() => CopySelectedText(x => HasValue(x.URL) ? $"[url]{x.URL}[/url]" : null);
    public void CopyForumImage() => CopySelectedText(x => HasValue(x.URL) && IsImagePath(x.URL) ? $"[img]{x.URL}[/img]" : null);
    public void CopyForumLinkedImage() => CopySelectedText(x =>
        HasValue(x.URL) && IsImagePath(x.URL) && HasValue(x.ThumbnailURL)
            ? $"[url={x.URL}][img]{x.ThumbnailURL}[/img][/url]"
            : null);

    public void CopyMarkdownLink() => CopySelectedText(x => HasValue(x.URL)
        ? $"[{MarkdownLabel(GetDisplayName(x))}]({MarkdownTarget(x.URL)})" : null);

    public void CopyMarkdownImage() => CopySelectedText(x => HasValue(x.URL) && IsImagePath(x.URL)
        ? $"![{MarkdownLabel(GetDisplayName(x))}]({MarkdownTarget(x.URL)})" : null);

    public void CopyMarkdownLinkedImage() => CopySelectedText(x =>
        HasValue(x.URL) && IsImagePath(x.URL) && HasValue(x.ThumbnailURL)
            ? $"[![{MarkdownLabel(GetDisplayName(x))}]({MarkdownTarget(x.ThumbnailURL)})]({MarkdownTarget(x.URL)})"
            : null);

    public void CopyFilePath() => CopySelectedText(x => HasValue(x.FilePath) ? x.FilePath : null);
    public void CopyFileName() => CopySelectedText(x =>
        SafePathPart(x.FilePath, Path.GetFileNameWithoutExtension) ??
        SafePathPart(x.FileName, Path.GetFileNameWithoutExtension));
    public void CopyFileNameWithExtension() => CopySelectedText(x =>
        SafePathPart(x.FilePath, Path.GetFileName) ??
        SafePathPart(x.FileName, Path.GetFileName));
    public void CopyFolder() => CopySelectedText(x => SafePathPart(x.FilePath, Path.GetDirectoryName));

    public void ShowImagePreview()
    {
        if (HistoryItem is null) return;

        if (IsImageFile)
        {
            ImagePreviewRequested?.Invoke(HistoryItem);
        }
        else if (IsVideoFile)
        {
            VideoPreviewRequested?.Invoke(HistoryItem);
        }
    }

    public void UploadFile()
    {
        if (uploadFile is not null && HistoryItem is not null && IsFileExist && HistoryItem.FilePath is not null)
            uploadFile(HistoryItem.FilePath);
    }

    public void EditImage()
    {
        if (editImage is not null && HistoryItem is not null && IsImageFile && HistoryItem.FilePath is not null)
            editImage(HistoryItem.FilePath);
    }

    public void PinToScreen()
    {
        if (pinToScreen is not null && HistoryItem is not null && IsImageFile && HistoryItem.FilePath is not null)
            pinToScreen(HistoryItem.FilePath);
    }

    public void ShowMoreInfo()
    {
        if (!HideShowMoreInfoButton && HistoryItem is not null) MoreInfoRequested?.Invoke(HistoryItem);
    }

    private HistoryItem[] GetSelectedItems()
    {
        try
        {
            return GetHistoryItems?.Invoke()?
                .Where(x => x is not null)
                .Distinct()
                .ToArray() ?? [];
        }
        catch (Exception ex)
        {
            Fail($"Could not read the history selection: {ex.Message}");
            return [];
        }
    }

    private void CopySelectedText(Func<HistoryItem, string?> selector)
    {
        string[] values = GetSelectedItems()
            .Select(item => SafeSelect(item, selector))
            .Where(HasValue)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        RequestText(string.Join(Environment.NewLine, values));
    }

    private static string? SafeSelect(HistoryItem item, Func<HistoryItem, string?> selector)
    {
        try { return selector(item); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private void RequestText(string? text)
    {
        if (HasValue(text)) CopyTextRequested?.Invoke(text!);
    }

    private void Fail(string message)
    {
        DebugHelper.WriteLine(message);
        OperationFailed?.Invoke(message);
    }

    private void ResetSelectionState()
    {
        IsURLExist = false;
        IsShortenedURLExist = false;
        IsThumbnailURLExist = false;
        IsDeletionURLExist = false;
        IsImageURL = false;
        IsTextURL = false;
        IsFilePathValid = false;
        IsFileExist = false;
        IsImageFile = false;
        IsTextFile = false;
    }

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool SafeFileExists(string? path)
    {
        if (!HasValue(path)) return false;
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsValidFilePath(string? path)
    {
        if (!HasValue(path)) return false;
        try { return Path.HasExtension(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsImagePath(string? path)
    {
        if (!HasValue(path)) return false;
        try { return FileHelpers.IsImageFile(GetExtensionProbe(path!)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsVideoPath(string? path)
    {
        if (!HasValue(path)) return false;
        try { return VideoExtensions.Contains(Path.GetExtension(GetExtensionProbe(path!))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsTextPath(string? path)
    {
        if (!HasValue(path)) return false;
        try { return FileHelpers.IsTextFile(GetExtensionProbe(path!)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string GetExtensionProbe(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && !uri.IsFile
            ? uri.AbsolutePath
            : value;

    private static string? SafePathPart(string? path, Func<string, string?> selector)
    {
        if (!HasValue(path)) return null;
        try { return selector(path!); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string GetDisplayName(HistoryItem item) =>
        HasValue(item.FileName) ? item.FileName! : HasValue(item.URL) ? item.URL! : "link";

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string MarkdownLabel(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal);

    private static string MarkdownTarget(string? value) => (value ?? string.Empty)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);
}
