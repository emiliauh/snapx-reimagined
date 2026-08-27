using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SnapX.CommonUI;

/// <summary>
/// Toolkit-neutral notification state which a frontend can render as a toast,
/// banner, or native notification.
/// </summary>
public sealed class NotificationWindow : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _message = string.Empty;
    private bool _isVisible;
    private NotificationKind _kind;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Closed;
    public event EventHandler? Activated;

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetField(ref _isVisible, value);
    }

    public NotificationKind Kind
    {
        get => _kind;
        private set => SetField(ref _kind, value);
    }

    public DateTimeOffset ShownAt { get; private set; }

    public void Show(string? title, string? message, NotificationKind kind = NotificationKind.Information)
    {
        Title = title?.Trim() ?? string.Empty;
        Message = message?.Trim() ?? string.Empty;
        Kind = kind;
        ShownAt = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(ShownAt));
        IsVisible = true;
    }

    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Activate()
    {
        if (IsVisible) Activated?.Invoke(this, EventArgs.Empty);
    }

    private void SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum NotificationKind
{
    Information,
    Success,
    Warning,
    Error
}
