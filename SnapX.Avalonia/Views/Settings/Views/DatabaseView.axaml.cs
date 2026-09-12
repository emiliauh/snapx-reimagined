using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using FluentAvalonia.UI.Controls;
using SnapX.Avalonia.ViewModels;

namespace SnapX.Avalonia.Views.Settings.Views;

public partial class DatabaseView : UserControl
{
    private readonly DatabaseVM _vm;
    public DatabaseView(DatabaseVM vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
    }
    private async Task<DatabaseVM.PathReplaceResult?> CapturePathReplacePairAsync(Visual visual)
    {
        var oldPath = await CaptureFolderAsync(visual, "Select the OLD folder path to replace");
        if (string.IsNullOrWhiteSpace(oldPath)) return null;

        var suggestedPath = Core.SnapXL.ScreenshotsParentFolder;
        var newPath = await CaptureFolderAsync(visual, "Select the NEW folder path", suggestedPath);

        if (string.IsNullOrWhiteSpace(newPath)) return null;

        return new DatabaseVM.PathReplaceResult(oldPath, newPath);
    }

    private async Task<string?> CaptureFolderAsync(Visual visual, string title, string? suggestedPath = null)
    {
        var topLevel = TopLevel.GetTopLevel(visual);
        if (topLevel == null) return null;

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        if (!string.IsNullOrWhiteSpace(suggestedPath))
        {
            try
            {
                options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(suggestedPath);
            }
            catch
            {
                // If the path doesn't exist yet, the picker will just open to the default system folder
            }
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);

        return folders?.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private async Task<bool> ShowConfirmationDialogAsync(DatabaseVM.PathUpdateConfirmationMessage m)
    {
        var dialog = new FAContentDialog
        {
            Title = "Confirm path changes",
            PrimaryButtonText = "Apply changes",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Close
        };

        var stack = new StackPanel { Spacing = 10, Width = 600 };

        stack.Children.Add(new TextBlock
        {
            Text = $"This operation will change {m.TotalCount} records.",
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.Red
        });

        stack.Children.Add(new TextBlock
        {
            Text = "This operation does not create a backup. Back up the database before you continue.",
            TextWrapping = TextWrapping.Wrap
        });

        var grid = new DataGrid
        {
            ItemsSource = m.Previews,
            AutoGenerateColumns = true,
            Height = 300,
            IsReadOnly = true,
            GridLinesVisibility = DataGridGridLinesVisibility.All
        };

        stack.Children.Add(new ScrollViewer { Content = grid });
        dialog.Content = stack;

        var result = await dialog.ShowAsync();
        return result == FAContentDialogResult.Primary;
    }
    private void StyledElement_OnInitialized(object? Sender, EventArgs E)
    {
        WeakReferenceMessenger.Default.Register<DatabaseView, DatabaseVM.PathReplaceRequestMessage>(this, (r, m) =>
        {
            m.Reply(CapturePathReplacePairAsync(r));
        });

        WeakReferenceMessenger.Default.Register<DatabaseView, DatabaseVM.PathUpdateConfirmationMessage>(this, (r, m) =>
        {
            m.Reply(ShowConfirmationDialogAsync(m));
        });
        _vm.RefreshCommand.Execute(E);
        ScheduleColumnUpdate();
        _vm.Items.CollectionChanged += (_, __) => ScheduleColumnUpdate();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.AllColumnNames))
                ScheduleColumnUpdate();
        };

    }
    private bool _columnUpdatePending;

    private void ScheduleColumnUpdate()
    {
        if (_columnUpdatePending) return;
        _columnUpdatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _columnUpdatePending = false;
            MainDataGrid.Columns.Clear();
            foreach (string name in _vm.AllColumnNames)
            {
                // A typed template avoids reflection-based binding to dynamic
                // SQLite columns, which is unreliable in Native AOT builds.
                MainDataGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = name,
                    CellTemplate = new FuncDataTemplate<DatabaseRow>((row, _) => new TextBlock
                    {
                        Text = row?[name]?.ToString() ?? string.Empty,
                        Margin = new Thickness(8, 4)
                    })
                });
            }
        });
    }




}
