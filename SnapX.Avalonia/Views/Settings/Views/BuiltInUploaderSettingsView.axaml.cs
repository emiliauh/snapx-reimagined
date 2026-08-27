
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapX.Avalonia.ViewModels;
using SnapX.Avalonia.Views.Settings.Views.FileUploaders;
using SnapX.Avalonia.Views.Settings.Views.ImageUploaders;
using SnapX.Avalonia.Views.Settings.Views.TextUploaders;
using SnapX.Core.Upload.File;
using SnapX.Core.Upload.Img;
using SnapX.Core.Upload.Text;

namespace SnapX.Avalonia.Views.Settings.Views;

public partial class BuiltInUploaderSettingsView : UserControl
{
    private readonly CoreUploaderVM _vm;
    public BuiltInUploaderSettingsView(CoreUploaderVM vm)
    {
        _vm = vm;
        DataContext = _vm;
        InitializeComponent();
        _vm.PropertyChanged += OnVmPropertyChanged;

    }

    public BuiltInUploaderSettingsView() : this(new CoreUploaderVM())
    {

    }
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CoreUploaderVM.SelectedUploaderInstance))
        {
            UpdateSettingsView(_vm.SelectedUploaderInstance);
        }
    }

    private void UpdateSettingsView(object? instance)
    {
        if (instance == null)
        {
            SettingsContainer.Child = new StackPanel
            {
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock {
                        Text = "Select",
                        FontSize = 44,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 10)
                    },
                    new TextBlock {
                        Text = "Select an uploader.",
                        FontSize = 18,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock {
                        Text = "Select an uploader to view its settings.",
                        Foreground = Brushes.Gray,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            };
            return;
        }

        Control? newView = instance switch
        {
            Imgur => new ImgurUploaderSettingsView { DataContext = instance },
            ImageShackUploader => new ImageShackUploaderSettingsView { DataContext = instance },
            FlickrUploader => new FlickrUploaderSettingsView { DataContext = instance },
            GooglePhotos => new PicasaUploaderSettingsView { DataContext = instance },
            Pastebin => new PastebinUploaderSettingsView() { DataContext = instance },
            Paste_ee => new PasteeUploaderSettingsView() { DataContext = instance },
            GitHubGist => new GithubGistUploaderSettingsView() { DataContext = instance },
            Hastebin => new HastebinUploaderSettingsView() { DataContext = instance },
            OneTimeSecret => new OneTimeSecretUploadSettingsView() { DataContext = instance },
            FTP or SFTP => new FTPSettingsView() { DataContext = instance },
            // Add other mappings here:
            // AmazonS3 => new S3SettingsView { DataContext = instance },
            _ => new StackPanel
            {
                Spacing = 15,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock {
                        Text = "Settings",
                        FontSize = 48,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new SelectableTextBlock {
                        Text = "Uploader settings",
                        FontSize = 20,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock {
                        Text = "SnapX does not provide settings for this uploader.",
                        Foreground = Brushes.Gray,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new Border {
                        Background = Brushes.Transparent,
                        BorderBrush = Brushes.DodgerBlue,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10, 5),
                        Margin = new Thickness(0, 10, 0, 0),
                        Child = new TextBlock {
                            Text = "No settings are available.",
                            Foreground = Brushes.DodgerBlue,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            FontWeight = FontWeight.SemiBold
                        }
                    },
                    new TextBlock {
                        Text = "Select another uploader to view its settings.",
                        FontSize = 12,
                        FontStyle = FontStyle.Italic,
                        Foreground = Brushes.DimGray,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 300,
                        TextAlignment = TextAlignment.Center
                    }
                }
            }
        };

        SettingsContainer.Child = newView;
    }
}
