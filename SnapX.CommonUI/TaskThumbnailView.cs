using System.ComponentModel;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SnapX.Core;
using SnapX.Core.Job;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Native;

namespace SnapX.CommonUI;

/// <summary>A toolkit-neutral thumbnail model for an active task.</summary>
public sealed class TaskThumbnailView : INotifyPropertyChanged, IDisposable
{
    private bool _titleVisible = true;
    private bool _thumbnailExists;
    private Size _thumbnailSize = new(200, 150);
    private Image? _thumbnail;

    public TaskThumbnailView(WorkerTask task)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
    }

    public WorkerTask Task { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool TitleVisible
    {
        get => _titleVisible;
        set => SetField(ref _titleVisible, value);
    }

    public bool ThumbnailExists
    {
        get => _thumbnailExists;
        private set => SetField(ref _thumbnailExists, value);
    }

    public Size ThumbnailSize
    {
        get => _thumbnailSize;
        set
        {
            if (value.Width <= 0 || value.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Thumbnail dimensions must be positive.");

            if (!SetField(ref _thumbnailSize, value)) return;
            UpdateThumbnail(force: true);
        }
    }

    /// <summary>The owned thumbnail. Callers must clone it before retaining it.</summary>
    public Image? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail?.Dispose();
            _thumbnail = value;
            ThumbnailExists = value is not null;
            OnPropertyChanged();
        }
    }

    public bool UpdateThumbnail(string? filePath = null, Image? image = null, bool force = false)
    {
        if (!force && ThumbnailExists) return true;

        try
        {
            Thumbnail = CreateThumbnail(filePath, image);
            return ThumbnailExists;
        }
        catch (Exception ex)
        {
            DebugHelper.WriteLine($"Could not create task thumbnail: {ex.Message}");
            Thumbnail = null;
            return false;
        }
    }

    public void ClearThumbnail() => Thumbnail = null;

    private Image? CreateThumbnail(string? filePath, Image? image)
    {
        if (image is not null)
            return ResizeOwnedImage(image.Clone(_ => { }));

        filePath ??= Task.Info.FileName;
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        if (File.Exists(filePath) && !FileHelpers.IsVideoFile(filePath))
        {
            return ResizeOwnedImage(Image.Load(filePath));
        }

        return ResizeOwnedImage(Methods.GetJumboFileIcon(filePath), fill: true);
    }

    private Image ResizeOwnedImage(Image image, bool fill = false)
    {
        try
        {
            return ImageHelpers.ResizeImage(image, ThumbnailSize, false, fill);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Thumbnail = null;
        GC.SuppressFinalize(this);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
