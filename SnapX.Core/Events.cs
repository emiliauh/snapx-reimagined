using SixLabors.ImageSharp;
using SnapX.Core.Job;
using Xdg.Directories;

namespace SnapX.Core;

public class NeedFileOpenerEvent
{
    public string Directory { get; set; } = UserDirectory.PicturesDir;
    public string? FileName { get; set; }
    public List<string>? AcceptedExtensions { get; set; }
    public string? Title { get; set; } = SnapXL.AppName;
    public bool Multiselect { get; set; } = false;
    public bool FolderPicker { get; set; }
    public bool IndexFolder { get; set; }
    public bool HashCheck { get; set; }
    public bool VideoThumbnailer { get; set; }
    public bool VideoConverter { get; set; }
    public TaskSettings TaskSettings { get; set; }
}

public record ErrorMessageEvent(Exception Exception, string Context, bool FullError);

/// <summary>Raised after a task finishes, so the frontend can show a
/// dismissible thumbnail preview (matching ShareX's classic toast).</summary>
public record NeedToastNotificationEvent(
    Image? Image,
    string Title,
    string Message,
    string? Url,
    string? FilePath,
    ToastClickAction ClickAction);

public class NeedMainWindowHandle
{
    // The subscriber will fill this property
    public IntPtr ResultHandle { get; set; } = IntPtr.Zero;
}

public class NeedRegionCaptureEvent { }

public class NeedClipboardCopyEvent
{
    private readonly TaskCompletionSource<bool> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public string? Text { get; set; }

    public NeedClipboardCopyEvent(string text)
    {
        Text = text;
    }
    public NeedClipboardCopyEvent(Image img)
    {
        Image = img;
        FileName = "image.png";
    }
    public NeedClipboardCopyEvent(Image img, string? filename = null)
    {
        Image = img;
        FileName = filename;
    }

    /// <summary>
    /// Places one or more local files on the clipboard as a real file object.
    /// </summary>
    public NeedClipboardCopyEvent(IReadOnlyList<string> filePaths)
    {
        FilePaths = filePaths;
    }


    public Image? Image { get; set; }
    public string FileName { get; set; }
    public object? CustomData { get; set; }
    public Dictionary<string, object> AdditionalFormats { get; } = new();

    public bool HasText => !string.IsNullOrEmpty(Text);
    public bool HasImage => Image != null;

    /// <summary>
    /// One or more local file paths to place on the clipboard as a real file
    /// object (e.g. so a file manager or chat app's "paste" adds the actual
    /// file, not just its path as text). Used by
    /// <see cref="AfterCaptureTasks.CopyFileToClipboard"/>.
    /// </summary>
    public IReadOnlyList<string>? FilePaths { get; set; }

    public bool HasFiles => FilePaths is { Count: > 0 };

    public bool Handled { get; set; }

    /// <summary>
    /// Completes only after the active frontend has finished handing this data
    /// to its native clipboard backend. Capture workers use this to retain the
    /// source image until the frontend has made its independent bitmap copy.
    /// </summary>
    public Task<bool> Completion => _completion.Task;

    public void MarkAsHandled()
    {
        Handled = true;
        _completion.TrySetResult(true);
    }

    public void MarkAsFailed()
    {
        _completion.TrySetResult(false);
    }
}
public class NeedOCRWindowEvent(Image Image, TaskSettings Settings)
{
    public Image Image { get; set; } = Image;
    public TaskSettings TaskSettings { get; set; } = Settings;
}

public class NeedScanQRCodeEvent
{
    public NeedScanQRCodeEvent(Image image, TaskSettings settings)
    {
        Image = image;
        TaskSettings = settings;
    }

    public NeedScanQRCodeEvent(string text, TaskSettings settings)
    {
        Text = text;
        TaskSettings = settings;
    }

    public Image? Image { get; set; }
    public string? Text { get; set; }
    public TaskSettings TaskSettings { get; set; }

    public bool HasText => !string.IsNullOrEmpty(Text);
    public bool HasImage => Image != null;

    // public bool Handled { get; set; }
    //
    // public void MarkAsHandled()
    // {
    //     Handled = true;
    // }
}

public class EventAggregator
{
    private readonly List<Tuple<Type, Action<object>>> _subscriptions = [];

    public void Subscribe<TEvent>(Action<TEvent> action)
    {
        _subscriptions.Add(
            Tuple.Create<Type, Action<object>>(typeof(TEvent), o => action((TEvent)o))
        );
    }

    public void Publish<TEvent>(TEvent @event)
    {
        foreach (var subscription in _subscriptions.Where(s => s.Item1 == typeof(TEvent)))
        {
            subscription.Item2(@event);
        }
    }
}
