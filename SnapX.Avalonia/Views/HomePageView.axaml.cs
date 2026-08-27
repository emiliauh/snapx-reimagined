using System.Diagnostics;
using AsyncImageLoader.Loaders;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using SnapX.Avalonia.Models;
using SnapX.Avalonia.ViewModels;
using SnapX.Avalonia.Views;
using SnapX.CommonUI;
using SnapX.Core;
using SnapX.Core.History;
using SnapX.Core.Upload;
using SnapX.Core.Utils;
using HttpClientFactory = SnapX.Core.Utils.Miscellaneous.HttpClientFactory;

namespace SnapX.Avalonia;

public partial class HomePageView : UserControl
{
    private HomePageViewModel ViewModel;
    private bool _historyEventsAttached;
    private ListTaskTemplate? _contextHistoryItem;
    private PendingHistoryDrag? _pendingHistoryDrag;
    private readonly HashSet<ToggleButton> _selectedHistoryCardsOnPress = [];
    private readonly HashSet<ToggleButton> _draggedHistoryCards = [];
    // FAItemsRepeater is allowed to recycle/recreate a card's ToggleButton
    // between the two presses of a double-click. Keep the gesture state on a
    // stable history identity rather than on that transient visual.
    private string? _lastPressedHistoryKey;
    private DateTime _lastHistoryPressUtc;

    private sealed record PendingHistoryDrag(
        ToggleButton Button,
        ListTaskTemplate Item,
        PointerPressedEventArgs PressedEvent,
        global::Avalonia.Point StartPosition,
        bool CanStartFileDrag);

    public HomePageView(HomePageViewModel vm)
    {
        DataContext = vm;
        ViewModel = vm;
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, DragOver);
        AddHandler(DragDrop.DragEnterEvent, DragEnter);
        AddHandler(DragDrop.DropEvent, Drop);
        // Intercept the press and move in the tunnel phase, before a card can
        // handle the gesture itself. Native Wayland drags need pointer capture;
        // committing selection at this point keeps selection independent from
        // the captured release that follows.
        AddHandler(PointerPressedEvent, HistoryCard_OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, HistoryCard_OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, HistoryCard_OnPointerReleased, RoutingStrategies.Tunnel);
        AsyncImageLoader.ImageLoader.AsyncImageLoader = new DiskCachedWebImageLoader(
            HttpClientFactory.Get(),
            false,
            Path.Combine(Core.SnapXL.CacheFolder, "Images")
        );

        AttachHistoryEvents();
    }

    // Right-click selection and drag-out share this card-level entry point.
    // A drag is deliberately deferred until the pointer moves: starting a
    // native drag on pointer-down consumes a plain click's release and breaks
    // normal selection, double-click preview, and context-menu gestures.
    private void HistoryCard_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // In tunnel mode the sender is this UserControl; find the card button
        // the pointer is over. This runs before the ToggleButton (ClickMode=Press)
        // marks the press handled, so left-button presses are still visible here.
        var button = FindHistoryCardButton(e.Source as Visual);
        if (button is not { DataContext: ListTaskTemplate item })
        {
            DebugHelper.WriteLine("DDIAG: no card button for press; returning");
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(button).Properties;
        DebugHelper.WriteLine($"DDIAG: PointerPressed on card, right={properties.IsRightButtonPressed} left={properties.IsLeftButtonPressed}");

        if (properties.IsRightButtonPressed)
        {
            // Select the right-clicked card immediately, before the
            // ContextFlyout opens (PopupFlyoutBase_OnOpening below still runs
            // as the flyout's own Opening handler; setting it here too means
            // the selection is correct even in the moment between the press
            // and the flyout's Opening callback). Never start a drag for a
            // right-click: the platform drag source is a left-button-only
            // concept, and starting one here is what broke the flyout.
            _contextHistoryItem = item;
            ViewModel.ContextMenuSelectionCommand.Execute(item);
            SynchronizeHistoryCardChecks();

            // The card captures left-button gestures for native file drags.
            // On Wayland, relying on the implicit ContextFlyout trigger after
            // that tunnel handler is unreliable: the native popup is never
            // shown even though the flyout remains attached in XAML. Open the
            // existing flyout explicitly at the pointer instead. This keeps
            // the full Copy/Open/Delete submenu available without introducing
            // a separate top-level window.
            if (button.ContextFlyout is PopupFlyoutBase contextFlyout)
            {
                contextFlyout.ShowAt(button, true);
            }
            else
            {
                button.ContextFlyout?.ShowAt(button);
            }
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        // A captured PointerReleased is not consistently routed to the card
        // by Avalonia's Wayland backend. Select on the reliable press instead
        // so plain click is always single-select and Ctrl+click adds/removes a
        // card. The ToggleButton Click which arrives later is suppressed below.
        bool isSelected = ViewModel.SelectHistoryItem(
            item,
            e.KeyModifiers.HasFlag(KeyModifiers.Control));
        button.IsChecked = isSelected;
        _selectedHistoryCardsOnPress.Add(button);
        SynchronizeHistoryCardChecks();

        // Do not depend on ToggleButton.DoubleTapped after we capture the
        // pointer for a possible native file drag. The press is reliably
        // delivered for both clicks, so it is the appropriate double-click
        // boundary. Use a slightly forgiving interval for compositors which
        // add a frame of latency between click sequences.
        DateTime now = DateTime.UtcNow;
        string historyKey = item.task.FilePath ?? item.task.FileName ?? string.Empty;
        if (!string.IsNullOrEmpty(historyKey) &&
            string.Equals(_lastPressedHistoryKey, historyKey, StringComparison.Ordinal) &&
            now - _lastHistoryPressUtc <= TimeSpan.FromMilliseconds(750))
        {
            _lastPressedHistoryKey = null;
            DebugHelper.WriteLine($"DDIAG: history double-click preview requested: {item.task.FilePath}");
            OpenHistoryPreview(item);
        }
        else
        {
            _lastPressedHistoryKey = historyKey;
            _lastHistoryPressUtc = now;
        }

        // Selection and previews are useful even for a remote/missing history
        // item. Only a real native file drag needs the path to exist.
        bool canStartFileDrag = !string.IsNullOrEmpty(item.task.FilePath) && File.Exists(item.task.FilePath);
        _pendingHistoryDrag = new PendingHistoryDrag(
            button,
            item,
            e,
            e.GetPosition(button),
            canStartFileDrag);
        e.Pointer.Capture(button);
    }

    private async void HistoryCard_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pendingHistoryDrag is not { } pending)
        {
            return;
        }

        ToggleButton button = pending.Button;

        // Keep the pointer capture until release so all history cards get the
        // same selection/double-click behavior, but never turn a card without
        // a local file into a failed drag operation.
        if (!pending.CanStartFileDrag)
        {
            return;
        }

        if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            ClearPendingHistoryDrag(e);
            return;
        }

        global::Avalonia.Point position = e.GetPosition(button);
        double horizontalMovement = position.X - pending.StartPosition.X;
        double verticalMovement = position.Y - pending.StartPosition.Y;
        const double dragThreshold = 4;
        if (horizontalMovement * horizontalMovement + verticalMovement * verticalMovement < dragThreshold * dragThreshold)
        {
            return;
        }

        _pendingHistoryDrag = null;
        // A drag does not produce the normal click we suppress after an
        // ordinary press/release. Remove that marker now; _draggedHistoryCards
        // separately guards the unlikely backend which raises Click after the
        // drag has ended.
        _selectedHistoryCardsOnPress.Remove(pending.Button);
        _draggedHistoryCards.Add(pending.Button);
        e.Pointer.Capture(null);
        await BeginHistoryDragAsync(pending.Item, pending.PressedEvent);
    }

    private void HistoryCard_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pendingHistoryDrag is not null)
        {
            ClearPendingHistoryDrag(e);
        }
    }

    /// <summary>
    /// Walks up from an event source to the history card's ToggleButton. The
    /// drag handlers run in the tunnel phase with this UserControl as sender,
    /// and the pointer may land on an inner element (the image or the filename
    /// TextBlock), so the nearest ancestor ToggleButton carrying a
    /// <see cref="ListTaskTemplate"/> data context is what we act on.
    /// </summary>
    private static ToggleButton? FindHistoryCardButton(Visual? source)
    {
        if (source is null)
        {
            return null;
        }

        if (source is ToggleButton { DataContext: ListTaskTemplate } direct)
        {
            return direct;
        }

        for (var current = source as StyledElement; current is not null; current = current.Parent)
        {
            if (current is ToggleButton { DataContext: ListTaskTemplate } button)
            {
                return button;
            }
        }

        return null;
    }

    private void ClearPendingHistoryDrag(PointerEventArgs e)
    {
        _pendingHistoryDrag = null;
        e.Pointer.Capture(null);
    }

    private async Task BeginHistoryDragAsync(ListTaskTemplate item, PointerPressedEventArgs e)
    {
        DebugHelper.WriteLine("DDIAG: BeginHistoryDragAsync entered");
        string? path = item.task.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            var dataTransfer = new DataTransfer();
            var transferItem = new DataTransferItem();

            // The local file is the primary, always-correct format: it is
            // what makes the drag work as a real drop target in file
            // managers, editors, browsers, and chat apps.
            if (TopLevel.GetTopLevel(this)?.StorageProvider is { } provider)
            {
                var file = await provider.TryGetFileFromPathAsync(path);
                DebugHelper.WriteLine($"DDIAG: storage file resolved null={file is null} path=[{path}]");
                if (file is not null)
                {
                    // Avalonia maps DataFormat.File to the native Wayland
                    // text/uri-list MIME type. Do not add text/plain here:
                    // some targets prefer it and then treat the URI as text,
                    // producing a .txt upload rather than accepting the
                    // history image/video as a file.
                    transferItem.SetFile(file);
                }
            }

            DebugHelper.WriteLine($"DDIAG: transferItem.Formats.Count={transferItem.Formats.Count}");
            if (transferItem.Formats.Count > 0)
            {
                dataTransfer.Add(transferItem);
                DebugHelper.WriteLine("DDIAG: calling DragDrop.DoDragDropAsync");
                DragDropEffects result = await DragDrop.DoDragDropAsync(e, dataTransfer, DragDropEffects.Copy);
                DebugHelper.WriteLine($"DDIAG: DoDragDropAsync returned result={result}");
            }
            else
            {
                DebugHelper.WriteLine("DDIAG: no transfer formats; drag aborted");
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine("DDIAG: exception in drag: " + ex);
            DebugHelper.WriteException(ex);
        }
    }

    private void DragEnter(object? Sender, DragEventArgs e)
    {
        // DebugHelper.WriteLine("DragEnter Event");
        // DebugHelper.WriteLine($"Sender: {Sender} | EventArgs: {e.GetPosition(this)}");
    }

    private void DragOver(object? Sender, DragEventArgs e)
    {
        // DebugHelper.WriteLine("DragOver Event");
        // DebugHelper.WriteLine($"Sender: {Sender} | EventArgs: {e.GetPosition(this)}");
    }

    private void Drop(object? Sender, DragEventArgs e)
    {
        DebugHelper.WriteLine("Drop Event");
        DebugHelper.WriteLine($"Sender: {Sender} | EventArgs: {e.GetPosition(this)}");
        // A history drop imports a copy of the file; it must never negotiate a
        // move, including when both ends happen to be SnapX history cards.
        e.DragEffects &= DragDropEffects.Copy;
        // Prefer the real file whenever both formats are present. This also
        // prevents a URI/text fallback from becoming a new .txt history item.
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var files = DataTransferExtensions.TryGetFiles(e.DataTransfer) ?? Array.Empty<IStorageItem>();

            foreach (var item in files)
            {
                switch (item)
                {
                    case IStorageFile file:
                        UploadManager.UploadFile(file.Path.AbsolutePath);
                        break;
                    case IStorageFolder folder:
                        UploadManager.UploadFolder(folder.Path.AbsolutePath);
                        break;
                }
            }
        }
        else if (e.DataTransfer.Contains(DataFormat.Text))
        {
            UploadManager.UploadText(DataTransferExtensions.TryGetText(e.DataTransfer));
        }

        DebugHelper.WriteLine($"{string.Join(", ", e.DataTransfer.Formats.Select(f => f.Identifier))}");
    }

    public HomePageView()
        : this(new HomePageViewModel()) { }

    private void PopupFlyoutBase_OnOpening(object? Sender, EventArgs E)
    {
        if (Sender is FlyoutBase { Target.DataContext: ListTaskTemplate item })
        {
            _contextHistoryItem = item;
            ViewModel.ContextMenuSelectionCommand.Execute(item);
        }
    }

    private void Control_OnLoaded(object? Sender, RoutedEventArgs E)
    {
        AttachHistoryEvents();
        ViewModel.StartTimer();
    }
    private async void Control_OnInitialized(object? Sender, EventArgs E)
    {
        await ViewModel.Initialize();
    }

    private void DeleteLocallyButton_OnClick(object? Sender, RoutedEventArgs E)
    {
        if (Sender is not FAMenuFlyoutItem menuFlyoutItem)
            return;
        ViewModel.DeleteHistoryItemLocallyCommand.Execute(menuFlyoutItem.DataContext);
        ViewModel.InvalidateCache();
        ViewModel.StopTimer();
        _ = ViewModel.RefreshTasks();
        ViewModel.StartTimer();
    }

    private void RemoveHistoryItem_OnClick(object? Sender, RoutedEventArgs E)
    {
        if (Sender is not FAMenuFlyoutItem menuFlyoutItem)
            return;
        ViewModel.RemoveHistoryItemCommand.Execute(menuFlyoutItem.DataContext);
        ViewModel.InvalidateCache();
        ViewModel.StopTimer();
        _ = ViewModel.RefreshTasks();
        ViewModel.StartTimer();
    }

    private void Control_OnUnloaded(object? Sender, RoutedEventArgs E)
    {
        ViewModel.StopTimer();
        _ = ViewModel.HaltActiveTasks();
        DetachHistoryEvents();
    }

    private void OCRImageClick(object? Sender, RoutedEventArgs E)
    {
        if (Sender is not FAMenuFlyoutItem menuFlyoutItem)
            return;

        ViewModel.OCRImageCommand.Execute(menuFlyoutItem.DataContext);
    }

    private void DownloadButton_OnClick(object? Sender, RoutedEventArgs E)
    {
        if (Sender is not FAMenuFlyoutItem menuFlyoutItem)
            return;
        ViewModel.DownloadButtonCommand.Execute(menuFlyoutItem.DataContext);
        ViewModel.StopTimer();
        ViewModel.RefreshTasks();
        ViewModel.StartTimer();
    }

    private void UploadButton_OnClick(object? Sender, RoutedEventArgs E)
    {
        if (Sender is not FAMenuFlyoutItem menuFlyoutItem)
            return;
        ViewModel.UploadButtonCommand.Execute(menuFlyoutItem.DataContext);
    }

    private void DynamicOpenURL(object? sender, RoutedEventArgs e)
    {
        if (sender is not FAMenuFlyoutItem menuFlyoutItem)
            return;

        if (menuFlyoutItem.DataContext is not ListTaskTemplate listTaskTemplate)
            return;
        // menuFlyoutItem.Command?.Execute(menuFlyoutItem.CommandParameter);
        var path = menuFlyoutItem.Tag as string;
        ViewModel.OpenURLCommand.Execute(path);
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not FAMenuFlyoutItem menuItem)
            return;

        var filePath = menuItem.Tag as string;
        if (string.IsNullOrEmpty(filePath))
            return;

        var folderPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folderPath))
        {
            FileHelpers.OpenFolder(folderPath);
        }
    }

    private void HistoryItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: ListTaskTemplate item } button)
        {
            // The tunnel press handler has already committed selection. Do
            // not let ToggleButton's later Click apply it again (especially
            // important for Ctrl+click multi-selection).
            if (_selectedHistoryCardsOnPress.Remove(button))
            {
                SynchronizeHistoryCardChecks();
                return;
            }

            if (_draggedHistoryCards.Remove(button))
            {
                SynchronizeHistoryCardChecks();
                return;
            }

            bool isSelected = ViewModel.SelectHistoryItem(item, controlPressed: false);
            button.IsChecked = isSelected;
            SynchronizeHistoryCardChecks();
        }
    }

    private void SynchronizeHistoryCardChecks()
    {
        // ToggleButton owns visual state, while selected history items are
        // stored in the view model for bulk actions. Keep the two models in
        // lockstep so a normal click visibly clears all other cards.
        foreach (ToggleButton historyCard in this.GetVisualDescendants()
            .OfType<ToggleButton>()
            .Where(card => card.DataContext is ListTaskTemplate))
        {
            if (historyCard.DataContext is ListTaskTemplate historyItem)
            {
                historyCard.IsChecked = ViewModel.SelectedTasks.Contains(historyItem);
            }
        }
    }

    private void OpenHistoryPreview(ListTaskTemplate template)
    {
        HistoryItem item = template.task;
        if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
        {
            ShowTransientNotification("Preview unavailable", "The history file is no longer available locally.", NotificationKind.Error);
            return;
        }

        string extension = Path.GetExtension(item.FilePath);
        if (ImageExtensions.Contains(extension))
        {
            ShowImagePreview(item);
        }
        else if (VideoExtensions.Contains(extension))
        {
            ShowVideoPreview(item);
        }
        else if (TextExtensions.Contains(extension))
        {
            ShowTextPreview(item);
        }
        else
        {
            // For a type SnapX cannot render (PDFs, archives, and so on),
            // delegate to its desktop viewer rather than manufacture another
            // SnapX top-level window.
            FileHelpers.OpenFile(item.FilePath);
        }
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".gif", ".ico", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".ogv", ".webm"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".json", ".log", ".md", ".rtf", ".text", ".txt", ".xml", ".yaml", ".yml"
    };

    private void HistoryAction_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not FAMenuFlyoutItem { Tag: string action } menuItem)
            return;

        var item = menuItem.DataContext as ListTaskTemplate ?? _contextHistoryItem;
        ViewModel.ExecuteHistoryAction(item, action);
    }

    private async void CopyTextToClipboard(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException("The clipboard is unavailable.");

            await App.SetClipboardTextAsync(clipboard, text);
            ShowTransientNotification("Copied", "Text copied to the clipboard.", NotificationKind.Success);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            ShowTransientNotification("Copy failed", ex.Message, NotificationKind.Error);
        }
    }

    private async void CopyFilesToClipboard(IReadOnlyList<string> paths)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var clipboard = topLevel?.Clipboard;
            var storageProvider = topLevel?.StorageProvider;
            if (clipboard is null || storageProvider is null)
                throw new InvalidOperationException("The clipboard or storage provider is unavailable.");

            var storageItems = new List<IStorageItem>(paths.Count);
            foreach (string path in paths.Where(File.Exists))
            {
                var item = await storageProvider.TryGetFileFromPathAsync(path);
                if (item is not null) storageItems.Add(item);
            }

            if (storageItems.Count == 0)
                throw new FileNotFoundException("None of the selected history files still exist.");

            var dataTransfer = new DataTransfer();
            var transferItem = new DataTransferItem();
            foreach (var storageItem in storageItems)
            {
                transferItem.SetFile(storageItem);
            }
            transferItem.SetText(string.Join(Environment.NewLine, paths));
            dataTransfer.Add(transferItem);
            await App.SetClipboardDataObjectAsync(clipboard, dataTransfer);
            ShowTransientNotification("Copied", $"Copied {storageItems.Count} file(s) to the clipboard.", NotificationKind.Success);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            ShowTransientNotification("Copy failed", ex.Message, NotificationKind.Error);
        }
    }

    private async void CopyImageToClipboard(string path)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException("The clipboard is unavailable.");

            // SetBitmapAsync is the reliable cross-platform (X11/Wayland) path for
            // placing an image on the clipboard. The DataObject + SetDataObjectAsync
            // path with DataFormat.Bitmap does not translate to the native image
            // formats on every backend, so a paste into another app produced nothing.
            await using var stream = File.OpenRead(path);
            var bitmap = new Bitmap(stream);
            await App.SetClipboardBitmapAsync(clipboard, bitmap);
            ShowTransientNotification("Copied", "Image copied to the clipboard.", NotificationKind.Success);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            ShowTransientNotification("Copy failed", ex.Message, NotificationKind.Error);
        }
    }

    private void ShowImagePreview(HistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath)) return;

        try
        {
            // Preview stays entirely inside SnapX: a persistent overlay in the
            // main window's OverlayLayer. It is not a new top-level Window
            // (which becomes a tiled Wayland toplevel) and not a transient
            // Popup/Flyout (whose EGL surface fails under native Wayland).
            ShowHistoryPreview(item);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            ShowTransientNotification("Preview failed", ex.Message, NotificationKind.Error);
        }
    }

    private void ShowHistoryPreview(HistoryItem item)
    {
        Window? owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not { IsVisible: true })
        {
            owner = App.MyMainWindow is { IsVisible: true } mainWindow ? mainWindow : null;
        }

        HistoryPreviewOverlay.Show(
            item,
            owner,
            copy: () => CopyHistoryItem(item),
            delete: () => DeleteHistoryItem(item),
            openFolder: () => OpenHistoryFolder(item));
    }

    private void CopyHistoryItem(HistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath)) return;

        if (ImageExtensions.Contains(Path.GetExtension(item.FilePath)))
        {
            CopyImageToClipboard(item.FilePath);
        }
        else if (TextExtensions.Contains(Path.GetExtension(item.FilePath)))
        {
            CopyTextToClipboard(File.ReadAllText(item.FilePath));
        }
        else if (File.Exists(item.FilePath))
        {
            CopyFilesToClipboard([item.FilePath]);
        }
    }

    private void DeleteHistoryItem(HistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath)) return;

        try
        {
            File.Delete(item.FilePath);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to delete the history file.");
            return;
        }

        var template = ViewModel.recentTasks.FirstOrDefault(t => t.task.Id == item.Id);
        if (template is not null)
        {
            ViewModel.RemoveHistoryItemCommand.Execute(template);
            _ = ViewModel.RefreshTasks();
            ViewModel.InvalidateCache();
        }
        else
        {
            item.FilePath = null;
            ViewModel.InvalidateCache();
            _ = ViewModel.RefreshTasks();
        }
    }

    private void OpenHistoryFolder(HistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath)) return;
        string? folder = Path.GetDirectoryName(item.FilePath);
        if (!string.IsNullOrEmpty(folder)) FileHelpers.OpenFolder(folder);
    }

    private void ShowTextPreview(HistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath)) return;

        try
        {
            // Text previews stay inside SnapX as the same persistent overlay
            // used by images and videos, not an auto-dismissing toast.
            ShowHistoryPreview(item);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            ShowTransientNotification("Preview failed", ex.Message, NotificationKind.Error);
        }
    }

    private void ShowVideoPreview(HistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath)) return;

        try
        {
            // Video previews stay inside SnapX: ffmpeg decodes frames and the
            // overlay renders them into a WriteableBitmap. No external player.
            ShowHistoryPreview(item);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Failed to show the SnapX video preview");
            ShowTransientNotification("Preview failed", ex.Message, NotificationKind.Error);
        }
    }

    private async void ShowMoreInfo(HistoryItem item)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
                throw new InvalidOperationException("The owner window is unavailable.");

            string details = BuildHistoryDetails(item);
            var dialog = new FAContentDialog
            {
                Title = item.FileName ?? "History item details",
                Content = new ScrollViewer
                {
                    MaxHeight = 520,
                    Content = new SelectableTextBlock
                    {
                        Text = details,
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                        MaxWidth = 720
                    }
                },
                PrimaryButtonText = "Copy details",
                CloseButtonText = "Close",
                DefaultButton = FAContentDialogButton.Close
            };

            if (await dialog.ShowAsync(owner) == FAContentDialogResult.Primary)
            {
                CopyTextToClipboard(details);
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            ShowTransientNotification("Details unavailable", ex.Message, NotificationKind.Error);
        }
    }

    private static string BuildHistoryDetails(HistoryItem item)
    {
        var lines = new List<string>
        {
            $"File name: {item.FileName ?? "—"}",
            $"File path: {item.FilePath ?? "—"}",
            $"Date: {item.DateTime:O}",
            $"Type: {item.Type ?? "—"}",
            $"Host: {item.Host ?? "—"}",
            $"URL: {item.URL ?? "—"}",
            $"Shortened URL: {item.ShortenedURL ?? "—"}",
            $"Thumbnail URL: {item.ThumbnailURL ?? "—"}",
            $"Deletion URL: {item.DeletionURL ?? "—"}"
        };

        if (item.Tags is { Count: > 0 })
        {
            lines.Add("Tags:");
            lines.AddRange(item.Tags.Where(tag => tag is not null).Select(tag =>
                $"  {tag.Text ?? "—"} | {tag.WindowTitle ?? "—"} | {tag.ProcessName ?? "—"}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void ShowTransientNotification(string title, string message, NotificationKind kind)
    {
        ViewModel.ShowNotification(title, message, kind);
        DateTimeOffset shownAt = ViewModel.Notification.ShownAt;
        DispatcherTimer.RunOnce(
            () =>
            {
                if (ViewModel.Notification.ShownAt == shownAt) ViewModel.Notification.Close();
            },
            TimeSpan.FromSeconds(3));
    }

    private void DismissNotification_OnClick(object? sender, RoutedEventArgs e) =>
        ViewModel.DismissNotificationCommand.Execute(null);

    private void AttachHistoryEvents()
    {
        if (_historyEventsAttached) return;
        ViewModel.HistoryActions.CopyTextRequested += CopyTextToClipboard;
        ViewModel.HistoryActions.CopyFilesRequested += CopyFilesToClipboard;
        ViewModel.HistoryActions.CopyImageRequested += CopyImageToClipboard;
        ViewModel.HistoryActions.ImagePreviewRequested += ShowImagePreview;
        ViewModel.HistoryActions.VideoPreviewRequested += ShowVideoPreview;
        ViewModel.HistoryActions.MoreInfoRequested += ShowMoreInfo;
        _historyEventsAttached = true;
    }

    private void DetachHistoryEvents()
    {
        if (!_historyEventsAttached) return;
        ViewModel.HistoryActions.CopyTextRequested -= CopyTextToClipboard;
        ViewModel.HistoryActions.CopyFilesRequested -= CopyFilesToClipboard;
        ViewModel.HistoryActions.CopyImageRequested -= CopyImageToClipboard;
        ViewModel.HistoryActions.ImagePreviewRequested -= ShowImagePreview;
        ViewModel.HistoryActions.VideoPreviewRequested -= ShowVideoPreview;
        ViewModel.HistoryActions.MoreInfoRequested -= ShowMoreInfo;
        _historyEventsAttached = false;
    }
}
