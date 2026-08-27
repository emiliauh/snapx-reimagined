using System.Timers;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SnapX.Avalonia.Models;
using SnapX.Avalonia.Views;
using SnapX.CommonUI;
using SnapX.Core;
using SnapX.Core.History;
using SnapX.Core.Job;
using SnapX.Core.Upload;
using SnapX.Core.Utils;

namespace SnapX.Avalonia.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(1);
    public AvaloniaList<ListTaskTemplate> SelectedTasks { get; set; } = [];
    public AvaloniaList<ListTaskTemplate> recentTasks { get; set; } = [];
    public HistoryItemManager HistoryActions { get; }
    public NotificationWindow Notification { get; } = new();
    private System.Timers.Timer _refreshTimer;
    private bool _isRefreshing; // Guard flag to prevent concurrent refreshes
    private int _failedRefreshTasks;
    private bool _initialized;
    private long _historyRevision;

    // Kept for callers that request a refresh after a history action. History is
    // read on every refresh, so there is no stale result cache to invalidate.
    public void InvalidateCache() { }

    public HomePageViewModel()
    {
        _refreshTimer = new System.Timers.Timer(5000); // Refresh every 5 seconds
        HistoryActions = new HistoryItemManager(null, null, null);
        HistoryActions.GetHistoryItems += GetSelectedHistoryItems;
        HistoryActions.OperationFailed += message =>
            ShowNotification("History action failed", message, NotificationKind.Error);
    }

    public async Task Initialize()
    {
        if (!_initialized)
        {
            TaskManager.InitHistoryManager();
            TaskManager.HistoryItemAdded += OnHistoryItemAdded;
            _refreshTimer.Elapsed += OnRefreshTimerElapsed;
            _refreshTimer.AutoReset = true;
            _initialized = true;
        }

        _refreshTimer.Start();
        await RefreshTasks();
    }

    public void StopTimer() => _refreshTimer.Stop();

    public void StartTimer() => _refreshTimer.Start();

    private async void OnRefreshTimerElapsed(object sender, ElapsedEventArgs e)
    {
        if (_isRefreshing)
        {
            DebugHelper.WriteLine(
                "Previous timer run already in progress. Skipping this timer tick."
            );
            // Apply more conservative _refreshTimer interval when we know that there's a bunch of tasks.
            if (recentTasks.Count > 3000)
                _refreshTimer.Interval = 10_000;
            if (_failedRefreshTasks > 15)
                _refreshTimer.Interval = 30_000;
            if (_failedRefreshTasks > 10)
                _refreshTimer.Interval = 20_000;
            if (_failedRefreshTasks > 5)
                _refreshTimer.Interval = 10_000;
            if (_failedRefreshTasks > 19)
                _refreshTimer.Interval = 60_000;
            // Fuck it, give up.
            if (_failedRefreshTasks > 20)
                _refreshTimer.Stop();
            _failedRefreshTasks++;
            return;
        }

        _isRefreshing = true;
        try
        {
            // ConfigureAwait(false) is good practice here as it's background work.
            await RefreshTasks().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
        }
        finally
        {
            _isRefreshing = false; // Reset the flag when the refresh is complete (or fails)
        }
    }

    private void OnHistoryItemAdded(HistoryItem historyItem)
    {
        Interlocked.Increment(ref _historyRevision);
        Dispatcher.UIThread.Post(() =>
        {
            if (historyItem.DateTime >= DateTime.Now.Subtract(HistoryWindow))
            {
                UpsertHistoryItem(historyItem);
            }
        });
    }

    private void UpsertHistoryItem(HistoryItem historyItem)
    {
        var template = new ListTaskTemplate(typeof(HomePageViewModel), historyItem);
        int existingIndex = recentTasks
            .Select((item, index) => (item, index))
            .FirstOrDefault(x => x.item.task.Id == historyItem.Id)
            .index;

        if (existingIndex >= 0 && existingIndex < recentTasks.Count &&
            recentTasks[existingIndex].task.Id == historyItem.Id)
        {
            recentTasks.RemoveAt(existingIndex);
        }

        int insertIndex = 0;
        while (insertIndex < recentTasks.Count)
        {
            HistoryItem current = recentTasks[insertIndex].task;
            if (current.DateTime < historyItem.DateTime ||
                (current.DateTime == historyItem.DateTime && current.Id < historyItem.Id))
            {
                break;
            }
            insertIndex++;
        }

        recentTasks.Insert(insertIndex, template);
    }

    [RelayCommand]
    public void ContextMenuSelection(object Sender)
    {
        if (Sender is ListTaskTemplate item)
        {
            EnsureActionSelection(item);
        }
    }

    [RelayCommand]
    public void ShareHistoryItem(object Sender)
    {
        if (Sender is not ListTaskTemplate ltt || string.IsNullOrWhiteSpace(ltt.task.URL))
            return;
        UploadManager.ShareURL(ltt.task.URL);
    }

    [RelayCommand]
    public void ShortenHistoryItem(object Sender)
    {
        if (Sender is not ListTaskTemplate ltt || string.IsNullOrWhiteSpace(ltt.task.URL))
            return;
        UploadManager.ShortenURL(ltt.task.URL);
    }

    [RelayCommand]
    public void DeleteHistoryItemLocally(object Sender)
    {
        if (Sender is not ListTaskTemplate ltt)
            return;

        var task = ltt.task;
        if (string.IsNullOrWhiteSpace(task.FilePath))
        {
            DebugHelper.WriteLine(
                $"DeleteHistoryItemLocally called with a invalid file path: '{task.FilePath}'. The task file name is '{task.FileName}'"
            );
            return;
        }
        DebugHelper.WriteLine($"Deleting file {task.FilePath}");
        try
        {
            File.Delete(task.FilePath);
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
        }

        task.FilePath = null;
        TaskManager.History.UpdateHistoryItem(task);
        SelectedTasks.Remove(ltt);
        recentTasks.Remove(ltt);
        InvalidateCache();
    }

    [RelayCommand]
    public void RemoveHistoryItem(object Sender)
    {
        if (Sender is not ListTaskTemplate ltt)
            return;
        var task = ltt.task;
        DebugHelper.WriteLine(
            $"Removing {task.FilePath ?? task.FileName} (Id: {task.Id}) from history"
        );
        var success = TaskManager.History.RemoveHistoryItem(task);
        var status = success ? "Success" : "Failure";
        DebugHelper.WriteLine($"{status} removing history item {task.FilePath ?? task.FileName}");
        if (success)
        {
            SelectedTasks.Remove(ltt);
            recentTasks.Remove(ltt);
            InvalidateCache();
        }
    }

    [RelayCommand]
    private void ToggleSelection(object parameter)
    {
        if (parameter is not ListTaskTemplate item)
            return;

        // This command is retained for command callers that do not carry
        // modifier information. A normal activation is always a single
        // selection; the view passes the Ctrl state to SelectHistoryItem.
        SelectHistoryItem(item, controlPressed: false);
    }

    /// <summary>
    /// Applies the history-card selection convention used by file managers:
    /// a normal click selects exactly one card, while Ctrl+click toggles a
    /// card without disturbing the existing selection. Returns the selected
    /// state the card should render after the operation.
    /// </summary>
    public bool SelectHistoryItem(ListTaskTemplate item, bool controlPressed)
    {
        if (!controlPressed)
        {
            SelectedTasks.Clear();
            SelectedTasks.Add(item);
            return true;
        }

        if (SelectedTasks.Contains(item))
        {
            SelectedTasks.Remove(item);
            return false;
        }

        SelectedTasks.Add(item);
        return true;
    }

    public void SetSelection(ListTaskTemplate? item, bool isSelected)
    {
        if (item is null) return;

        if (isSelected)
        {
            if (!SelectedTasks.Contains(item)) SelectedTasks.Add(item);
        }
        else
        {
            SelectedTasks.Remove(item);
        }
    }

    public bool ExecuteHistoryAction(object? sender, string? actionName)
    {
        if (sender is not ListTaskTemplate item ||
            !Enum.TryParse(actionName, ignoreCase: false, out HistoryAction action))
        {
            return false;
        }

        EnsureActionSelection(item);
        HistoryActions.Execute(action);
        return true;
    }

    public void ShowNotification(
        string title,
        string message,
        NotificationKind kind = NotificationKind.Information) =>
        Notification.Show(title, message, kind);

    [RelayCommand]
    private void DismissNotification() => Notification.Close();

    private HistoryItem[] GetSelectedHistoryItems() => SelectedTasks
        .Where(x => x?.task is not null)
        .Select(x => x.task)
        .Distinct()
        .ToArray();

    private void EnsureActionSelection(ListTaskTemplate item)
    {
        if (SelectedTasks.Contains(item)) return;
        SelectedTasks.Clear();
        SelectedTasks.Add(item);
    }

    private void OnPointerPress(object sender, PointerPressedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: HomePageViewModel vm } button)
            return;
        var item = button.Tag as string;

        if (e.GetCurrentPoint(button).Properties.IsRightButtonPressed)
        {
            // Right-click: Show context menu on the toggle button itself
            vm.ContextMenuSelectionCommand.Execute(button);
        }
        else if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            // Left-click: Toggle selection
            vm.ToggleSelectionCommand.Execute(item);
        }
    }

    [RelayCommand]
    public void OpenURL(object parameter)
    {
        if (parameter is not string url || string.IsNullOrWhiteSpace(url))
            return;
        URLHelpers.OpenURL(url);
    }
    [RelayCommand]
    public void OpenPath(object parameter)
    {
        if (parameter is not string Path || string.IsNullOrWhiteSpace(Path) || !File.Exists(Path))
            return;
        FileHelpers.OpenFolderWithFile(Path);
    }
    [RelayCommand]
    public void OpenFile(object parameter)
    {
        if (parameter is not string Path || string.IsNullOrWhiteSpace(Path) || !File.Exists(Path))
            return;
        FileHelpers.OpenFile(Path);
    }
    [RelayCommand]
    public void OCRImage(object Sender)
    {
        DebugHelper.WriteLine("OCRImage");
        if (Sender is not ListTaskTemplate ltt)
            return;
        DebugHelper.WriteLine("OCRImage 2");
        var OcrWindow = new OCR(ltt.task);
        OcrWindow.Show();
    }

    [RelayCommand]
    public void DownloadButton(object Sender)
    {
        if (Sender is not ListTaskTemplate ltt)
            return;
        var taskSettings = TaskSettings.GetDefaultTaskSettings();
        var url = ltt.task.URL ?? ltt.task.ThumbnailURL;

        if (string.IsNullOrWhiteSpace(url))
            return;

        var task = WorkerTask.CreateDownloadTask(url, false, taskSettings);

        if (task != null)
        {
            TaskManager.Start(task);
        }
    }

    [RelayCommand]
    public void UploadButton(object Sender)
    {
        if (Sender is not ListTaskTemplate ltt)
            return;
        if (ltt.task.FilePath is null)
        {
            DebugHelper.WriteLine(
                "UploadButton called with a null path, using BestImageSource instead"
            );
            UploadManager.DownloadAndUploadFile(ltt.task.BestImageSource);
            return;
        }
        UploadManager.UploadFile(ltt.task.FilePath);
    }

    private CancellationTokenSource? _refreshCts;

    public async Task RefreshTasks(CancellationToken cancellationToken = default)
    {
        long requestedRevision = Volatile.Read(ref _historyRevision);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _refreshCts?.Token ?? CancellationToken.None
        );

        var ct = linkedCts.Token;

        var typeofVM = typeof(HomePageViewModel);

        var historyItems = await TaskManager
            .History.GetHistoryItemsAsync(30_000)
            .WaitAsync(ct)
            .ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        DateTime earliestHistoryTime = DateTime.Now.Subtract(HistoryWindow);
        List<ListTaskTemplate> newDesiredTasks = historyItems
            .Where(task => task.DateTime >= earliestHistoryTime)
            .OrderByDescending(task => task.DateTime)
            .ThenByDescending(task => task.Id)
            .Select(task => new ListTaskTemplate(typeofVM, task))
            .ToList();

        ct.ThrowIfCancellationRequested();

        // A task can commit while this database query is running. Its event has
        // the newer state, so this older snapshot must not overwrite it.
        if (requestedRevision != Volatile.Read(ref _historyRevision)) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (requestedRevision != Volatile.Read(ref _historyRevision)) return;
            var selectedTaskIds = SelectedTasks.Select(template => template.task.Id).ToHashSet();

            if (newDesiredTasks.Count > 50_000)
            {
                recentTasks.ResetBehavior = ResetBehavior.Remove;
                recentTasks.Clear();
                recentTasks.AddRange(newDesiredTasks);
                SelectedTasks.Clear();
                SelectedTasks.AddRange(recentTasks.Where(template => selectedTaskIds.Contains(template.task.Id)));
                return;
            }

            var currentTasksById = recentTasks
                .GroupBy(template => template.task.Id)
                .ToDictionary(group => group.Key, group => group.First());
            var newDesiredTaskIds = newDesiredTasks
                .Select(template => template.task.Id)
                .ToHashSet();

            for (var i = recentTasks.Count - 1; i >= 0; i--)
            {
                ct.ThrowIfCancellationRequested();

                if (!newDesiredTaskIds.Contains(recentTasks[i].task.Id))
                {
                    recentTasks.RemoveAt(i);
                }
            }

            for (int desiredIndex = 0; desiredIndex < newDesiredTasks.Count; desiredIndex++)
            {
                ct.ThrowIfCancellationRequested();

                var newItem = newDesiredTasks[desiredIndex];

                if (currentTasksById.TryGetValue(newItem.task.Id, out var existingItem))
                {
                    int currentIndex = recentTasks.IndexOf(existingItem);
                    if (currentIndex == -1) continue;

                    if (currentIndex == desiredIndex)
                    {
                        if (!existingItem.Equals(newItem)) recentTasks[desiredIndex] = newItem;
                        continue;
                    }

                    recentTasks.RemoveAt(currentIndex);
                    recentTasks.Insert(desiredIndex, existingItem.Equals(newItem) ? existingItem : newItem);
                }
                else
                {
                    recentTasks.Insert(desiredIndex, newItem);
                }
            }

            SelectedTasks.Clear();
            SelectedTasks.AddRange(recentTasks.Where(template => selectedTaskIds.Contains(template.task.Id)));
        });
    }

    public async Task HaltActiveTasks()
    {
        if (_refreshCts != null)
        {
            await _refreshCts.CancelAsync();
            _refreshCts.Dispose();
            _refreshCts = null;
        }
        await Task.CompletedTask;
    }
}
