using System.ComponentModel;
using System.Runtime.CompilerServices;
using SnapX.Core.History;
using SnapX.Core.Media.Services;
using SnapX.Core.Utils;

namespace SnapX.Avalonia.Models;

public class ListTaskTemplate(Type modelType, HistoryItem task) : INotifyPropertyChanged
{
    public Type ModelType { get; init; } = modelType;
    public HistoryItem task { get; init; } = task;

    private string? _uiDisplaySource;
    private readonly Lock _sourceLock = new();
    private Task? _loadSourceTask;

    public string? UIDisplaySource
    {
        get
        {
            EnsureSourceLoading();
            return _uiDisplaySource;
        }
    }

    /// <summary>
    /// Re-resolves the history item's preview. This is useful after a cache
    /// repair and, unlike the old one-shot lazy getter, retries a failed video
    /// thumbnail instead of permanently binding the Image control to an MP4.
    /// </summary>
    public Task RefreshSourceAsync()
    {
        lock (_sourceLock)
        {
            _uiDisplaySource = null;
            _loadSourceTask = null;
        }
        OnPropertyChanged(nameof(UIDisplaySource));
        return EnsureSourceLoading();
    }

    private Task EnsureSourceLoading()
    {
        lock (_sourceLock)
        {
            if (_loadSourceTask is { IsCompleted: false })
            {
                return _loadSourceTask;
            }

            _loadSourceTask = LoadSourceAsync();
            return _loadSourceTask;
        }
    }

    private async Task LoadSourceAsync()
    {
        string? originalSource = task.BestImageSource;

        try
        {
            var result = await ThumbnailService.GetCompatibleSourceAsync(originalSource);

            // An Image control cannot decode an MP4. Keep its source empty on
            // a failed video-thumbnail attempt so the next refresh retries
            // generation rather than preserving a known-invalid source.
            if (FileHelpers.IsVideoFile(originalSource)
                && string.Equals(result, originalSource, StringComparison.Ordinal))
            {
                result = null;
            }

            SetDisplaySource(result);
        }
        catch
        {
            SetDisplaySource(FileHelpers.IsVideoFile(originalSource) ? null : originalSource);
        }
    }

    private void SetDisplaySource(string? source)
    {
        bool changed;
        lock (_sourceLock)
        {
            changed = !string.Equals(_uiDisplaySource, source, StringComparison.Ordinal);
            _uiDisplaySource = source;
        }
        if (changed)
        {
            OnPropertyChanged(nameof(UIDisplaySource));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override bool Equals(object? obj)
    {
        if (obj is ListTaskTemplate other)
        {
            return EqualityComparer<Type>.Default.Equals(ModelType, other.ModelType)
                && EqualityComparer<HistoryItem>.Default.Equals(task, other.task);
        }
        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ModelType, task);
    }
}
