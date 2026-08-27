
// SPDX-License-Identifier: GPL-3.0-or-later


using System.Runtime.InteropServices;
using System.Web;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SnapX.Core.Job;
using SnapX.Core.Utils;
using SnapX.Core.Utils.Extensions;
using SnapX.Core.Utils.Native;
using Xdg.Directories;

namespace SnapX.Core.Upload;

public static class UploadManager
{
    private static IMultiUploadConfirmation multiUploadConfirmation = HeadlessMultiUploadConfirmation.Instance;

    /// <summary>
    /// Injectable confirmation hook for UI hosts. Headless callers safely reject
    /// warning-triggering batches unless they explicitly provide a policy.
    /// </summary>
    public static IMultiUploadConfirmation MultiUploadConfirmation
    {
        get => Volatile.Read(ref multiUploadConfirmation);
        set => Volatile.Write(ref multiUploadConfirmation, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public static void UploadFile(string? filePath, TaskSettings? taskSettings = null)
    {
        if (taskSettings == null) taskSettings = TaskSettings.GetDefaultTaskSettings();

        if (!string.IsNullOrEmpty(filePath))
        {
            if (System.IO.File.Exists(filePath))
            {
                WorkerTask task = WorkerTask.CreateFileUploaderTask(filePath, taskSettings);
                TaskManager.Start(task);
            }
            else if (Directory.Exists(filePath))
            {
                string?[] files = Directory.GetFiles(filePath, "*.*", SearchOption.AllDirectories);
                UploadFile(files, taskSettings);
            }
        }
    }

    public static void UploadFile(string?[] files, TaskSettings? taskSettings = null)
    {
        taskSettings ??= TaskSettings.GetDefaultTaskSettings();

        if (files == null || files.Length == 0)
            return;

        if (files.Length > 10 && !IsUploadConfirmed(files.Length))
            return;

        foreach (var file in files)
        {
            UploadFile(file, taskSettings);
        }
    }

    private static bool IsUploadConfirmed(int length)
    {
        if (SnapXL.Settings.ShowMultiUploadWarning)
        {
            MultiUploadConfirmationResult response;
            try
            {
                response = MultiUploadConfirmation.Confirm(length);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex);
                return false;
            }

            if (response.SuppressFutureWarning)
            {
                SnapXL.Settings.ShowMultiUploadWarning = false;
            }

            return response.Confirmed;
        }

        return true;
    }

    public static void UploadFile(TaskSettings? taskSettings = null)
    {
        taskSettings ??= TaskSettings.GetDefaultTaskSettings();
        var data = new NeedFileOpenerEvent()
        {
            Title = Lang.UploadManagerUploadFile,
            Multiselect = true,
            Directory = IsValidDirectory(SnapXL.Settings.FileUploadDefaultDirectory) ? SnapXL.Settings.FileUploadDefaultDirectory : UserDirectory.DesktopDir,
            TaskSettings = taskSettings
        };
        DebugHelper.WriteLine("Need file to upload. Asking UI for file.");
        // The UI will now do the rest.
        SnapXL.EventAggregator.Publish(data);
    }

    public static bool IsValidDirectory(string? dir)
    {
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
    }

    public static void UploadFolder(string? folderPath, TaskSettings? taskSettings = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        foreach (var file in Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories))
        {
            UploadFile(file, taskSettings);
        }
    }
    public static void UploadFolder(TaskSettings taskSettings = null)
    {
        // using (FolderSelectDialog folderDialog = new FolderSelectDialog())
        // {
        //     folderDialog.Title = "SnapX - " + Resources.UploadManager_UploadFolder_Folder_upload;
        //
        //     if (!string.IsNullOrEmpty(SnapX.Settings.FileUploadDefaultDirectory) && Directory.Exists(SnapX.Settings.FileUploadDefaultDirectory))
        //     {
        //         folderDialog.InitialDirectory = SnapX.Settings.FileUploadDefaultDirectory;
        //     }
        //     else
        //     {
        //         folderDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        //     }
        //
        //     if (folderDialog.ShowDialog() && !string.IsNullOrEmpty(folderDialog.FileName))
        //     {
        //         SnapX.Settings.FileUploadDefaultDirectory = folderDialog.FileName;
        //         UploadFile(folderDialog.FileName, taskSettings);
        //     }
        // }
    }

    public static void ProcessImageUpload(Image image, TaskSettings taskSettings)
    {
        if (image != null)
        {
            if (!taskSettings.AdvancedSettings.ProcessImagesDuringClipboardUpload)
            {
                taskSettings.AfterCaptureJob = AfterCaptureTasks.UploadImageToHost;
            }

            RunImageTask(image, taskSettings);
        }
    }

    public static void ProcessTextUpload(string? text, TaskSettings taskSettings)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var url = text.Trim();

        if (URLHelpers.IsValidURL(url))
        {
            if (taskSettings.UploadSettings.ClipboardUploadURLContents)
            {
                DownloadAndUploadFile(url, taskSettings);
                return;
            }

            if (taskSettings.UploadSettings.ClipboardUploadShortenURL)
            {
                ShortenURL(url, taskSettings);
                return;
            }

            if (taskSettings.UploadSettings.ClipboardUploadShareURL)
            {
                ShareURL(url, taskSettings);
                return;
            }
        }

        if (taskSettings.UploadSettings.ClipboardUploadAutoIndexFolder && text.Length <= 260 && Directory.Exists(text))
        {
            IndexFolder(text, taskSettings);
        }
        else
        {
            UploadText(text, taskSettings, true);
        }
    }

    public static void ProcessFilesUpload(string?[] files, TaskSettings taskSettings)
    {
        if (files?.Length > 0)
        {
            UploadFile(files, taskSettings);
        }
    }


    public static void ClipboardUpload(TaskSettings taskSettings = null)
    {
        if (taskSettings == null) taskSettings = TaskSettings.GetDefaultTaskSettings();

        try
        {
            if (Clipboard.ContainsImage())
            {
                Image<Rgba64> image;


                image = Clipboard.GetImage();


                ProcessImageUpload(image, taskSettings);
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();

                ProcessTextUpload(text, taskSettings);
            }
            else if (Clipboard.ContainsFileDropList())
            {
                string?[] files = Clipboard.GetFileDropList().Cast<string>().ToArray();

                ProcessFilesUpload(files, taskSettings);
            }
        }
        catch (ExternalException e)
        {
            DebugHelper.WriteException(e);
            // Basic retries. Should use Polly Nuget package
            ClipboardUpload(taskSettings);

        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);
        }
    }

    public static void UploadURL(TaskSettings taskSettings = null, string? url = null)
    {
        if (taskSettings == null) taskSettings = TaskSettings.GetDefaultTaskSettings();

        var candidate = !string.IsNullOrWhiteSpace(url) ? url.Trim() : Clipboard.GetText()?.Trim();

        if (URLHelpers.IsValidURL(candidate))
        {
            DownloadAndUploadFile(candidate!, taskSettings);
        }
    }
    public static void RunImageTask(Image image, TaskSettings taskSettings)
    {
        var metadata = new TaskMetadata(image);
        RunImageTask(metadata, taskSettings);
    }
    public static void RunImageTask(Image image, TaskSettings taskSettings, bool skipQuickTaskMenu = false, bool skipAfterCaptureWindow = false)
    {
        var metadata = new TaskMetadata(image);
        RunImageTask(metadata, taskSettings, skipQuickTaskMenu, skipAfterCaptureWindow);
    }

    public static void RunImageTask(TaskMetadata metadata, TaskSettings taskSettings, bool skipQuickTaskMenu = false, bool skipAfterCaptureWindow = false)
    {
        if (taskSettings == null) taskSettings = TaskSettings.GetDefaultTaskSettings();

        if (metadata != null && metadata.Image != null && taskSettings != null)
        {
            if (!skipQuickTaskMenu && taskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.ShowQuickTaskMenu))
            {
                RunImageTask(metadata, taskSettings, true);
                return;
            }

            string customFileName = null;


            var task = WorkerTask.CreateImageUploaderTask(metadata, taskSettings, customFileName);
            TaskManager.Start(task);
        }
    }

    public static void UploadImage(Image image, TaskSettings taskSettings = null)
    {
        if (image != null)
        {
            if (taskSettings == null)
            {
                taskSettings = TaskSettings.GetDefaultTaskSettings();
            }

            if (taskSettings.IsSafeTaskSettings)
            {
                taskSettings.UseDefaultAfterCaptureJob = false;
                taskSettings.AfterCaptureJob = AfterCaptureTasks.UploadImageToHost;
            }

            RunImageTask(image, taskSettings);
        }
    }

    public static void UploadImage(Image image, ImageDestination imageDestination, FileDestination imageFileDestination, TaskSettings taskSettings = null)
    {
        if (image != null)
        {
            if (taskSettings == null)
            {
                taskSettings = TaskSettings.GetDefaultTaskSettings();
            }

            if (taskSettings.IsSafeTaskSettings)
            {
                taskSettings.UseDefaultAfterCaptureJob = false;
                taskSettings.AfterCaptureJob = AfterCaptureTasks.UploadImageToHost;
                taskSettings.UseDefaultDestinations = false;
                taskSettings.ImageDestination = imageDestination;
                taskSettings.ImageFileDestination = imageFileDestination;
            }

            RunImageTask(image, taskSettings);
        }
    }
    public static void UploadText(string? text, TaskSettings? taskSettings = null, bool allowCustomText = false)
    {
        taskSettings ??= TaskSettings.GetDefaultTaskSettings();

        if (string.IsNullOrEmpty(text)) return;

        if (allowCustomText)
        {
            string input = taskSettings.AdvancedSettings.TextCustom;

            if (!string.IsNullOrEmpty(input))
            {
                if (taskSettings.AdvancedSettings.TextCustomEncodeInput)
                {
                    text = HttpUtility.HtmlEncode(text);
                }

                text = input.Replace("%input", text);
            }
        }

        var task = WorkerTask.CreateTextUploaderTask(text, taskSettings);
        TaskManager.Start(task);
    }

    public static void UploadImageStream(Stream stream, string? fileName, TaskSettings taskSettings = null)
    {
        taskSettings ??= TaskSettings.GetDefaultTaskSettings();

        if (stream == null || stream.Length == 0 || string.IsNullOrEmpty(fileName))
            return;

        var task = WorkerTask.CreateDataUploaderTask(EDataType.Image, stream, fileName, taskSettings);
        TaskManager.Start(task);
    }


    public static void ShortenURL(string? url, TaskSettings taskSettings = null)
    {
        if (string.IsNullOrEmpty(url))
            return;

        taskSettings ??= TaskSettings.GetDefaultTaskSettings();
        var task = WorkerTask.CreateURLShortenerTask(url, taskSettings);
        TaskManager.Start(task);
    }


    public static void ShortenURL(string? url, UrlShortenerType urlShortener)
    {
        if (string.IsNullOrEmpty(url))
            return;

        var taskSettings = TaskSettings.GetDefaultTaskSettings();
        taskSettings.URLShortenerDestination = urlShortener;

        var task = WorkerTask.CreateURLShortenerTask(url, taskSettings);
        TaskManager.Start(task);
    }


    public static void ShareURL(string? url, TaskSettings taskSettings = null)
    {
        if (string.IsNullOrEmpty(url))
            return;

        taskSettings ??= TaskSettings.GetDefaultTaskSettings();

        var task = WorkerTask.CreateShareURLTask(url, taskSettings);
        TaskManager.Start(task);
    }


    public static void ShareURL(string? url, URLSharingServices urlSharingService)
    {
        if (string.IsNullOrEmpty(url))
            return;

        var taskSettings = TaskSettings.GetDefaultTaskSettings();
        taskSettings.URLSharingServiceDestination = urlSharingService;

        var task = WorkerTask.CreateShareURLTask(url, taskSettings);
        TaskManager.Start(task);
    }

    public static void DownloadFile(string? url, TaskSettings taskSettings = null)
        => DownloadFile(url, false, taskSettings);

    public static void DownloadAndUploadFile(string? url, TaskSettings taskSettings = null)
        => DownloadFile(url, true, taskSettings);

    private static void DownloadFile(string? url, bool upload, TaskSettings? taskSettings = null)
    {
        DebugHelper.WriteLine($"Downloading file {url}");
        DebugHelper.WriteLine($"Upload: {upload}");
        if (string.IsNullOrEmpty(url)) return;

        taskSettings ??= TaskSettings.GetDefaultTaskSettings();

        var task = WorkerTask.CreateDownloadTask(url, upload, taskSettings);

        if (task != null)
        {
            TaskManager.Start(task);
        }
    }

    public static void IndexFolder(string? folderPath, TaskSettings? taskSettings = null)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

        taskSettings ??= TaskSettings.GetDefaultTaskSettings();
        taskSettings.ToolsSettings.IndexerSettings.BinaryUnits = SnapXL.Settings.BinaryUnits;

        string? source = null;

        Task.Run(() =>
        {
            source = Indexer.Indexer.Index(folderPath, taskSettings.ToolsSettings.IndexerSettings);
        }).ContinueInCurrentContext(() =>
        {
            if (string.IsNullOrEmpty(source)) return;
            var task = WorkerTask.CreateTextUploaderTask(source, taskSettings);
            task.Info.FileName = Path.ChangeExtension(task.Info.FileName, taskSettings.ToolsSettings.IndexerSettings.Output.ToString().ToLower());
            TaskManager.Start(task);
        });
    }
}
