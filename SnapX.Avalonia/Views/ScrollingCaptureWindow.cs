// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SnapX.Core;
using SnapX.Core.Job;
using SnapX.Core.ScreenCapture;
using SnapX.Core.Upload;
using SnapX.Core.Utils;
using Image = Avalonia.Controls.Image;
using SharpImage = SixLabors.ImageSharp.Image;
using AvaloniaColor = Avalonia.Media.Color;

namespace SnapX.Avalonia.Views;

/// <summary>
/// ShareX-style scrolling-capture result window. Shows the stitched image with
/// a toolbar offering Capture..., Upload/Save and Options..., mirroring the
/// upstream ShareX scrolling-capture window. The window takes ownership of the
/// ImageSharp clone it is handed and disposes it (and its display bitmap) when
/// it closes.
/// </summary>
public sealed class ScrollingCaptureWindow : Window
{
    private readonly SharpImage _image;
    private readonly WriteableBitmap _bitmap;
    private readonly TaskSettings _taskSettings;
    private readonly ScrollingCaptureOptions _options;
    private bool _disposed;

    public ScrollingCaptureWindow(SharpImage image, TaskSettings taskSettings, ScrollingCaptureOptions options)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _taskSettings = taskSettings ?? TaskSettings.GetDefaultTaskSettings();
        _options = options ?? new ScrollingCaptureOptions();

        Title = "SnapX | Scrolling capture";
        Width = Math.Min(980, _image.Width + 40);
        Height = Math.Min(720, _image.Height + 160);
        Background = new SolidColorBrush(AvaloniaColor.FromRgb(30, 30, 30));
        CanResize = true;

        // Convert on the UI thread. ConvertImageSharpImgToAvalonia copies the
        // decoded pixels into a WriteableBitmap, so the display bitmap is
        // independent of the worker's ImageSharp image.
        _bitmap = App.SnapX.ConvertImageSharpImgToAvalonia(_image);

        var imageView = new Image
        {
            Source = _bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var scroll = new ScrollViewer
        {
            Content = imageView,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var toolbar = BuildToolbar();

        var layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            }
        };
        layout.Children.Add(toolbar);
        Grid.SetRow(toolbar, 0);
        layout.Children.Add(scroll);
        Grid.SetRow(scroll, 1);

        Content = new Border
        {
            Background = new SolidColorBrush(AvaloniaColor.FromRgb(30, 30, 30)),
            Child = layout
        };

        Closed += (_, _) => DisposeResources();
    }

    private Control BuildToolbar()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var captureButton = new Button { Content = "Capture...", Margin = new Thickness(2), MinWidth = 96 };
        captureButton.Click += (_, _) => Capture();
        panel.Children.Add(captureButton);

        var uploadSaveButton = new Button { Content = "Upload / Save", Margin = new Thickness(2), MinWidth = 128 };
        uploadSaveButton.Click += (_, _) => UploadOrSave();
        panel.Children.Add(uploadSaveButton);

        var optionsButton = new Button { Content = "Options...", Margin = new Thickness(2), MinWidth = 96 };
        optionsButton.Click += (_, _) => ShowOptions();
        panel.Children.Add(optionsButton);

        var dimensions = new TextBlock
        {
            Text = $"{_image.Width} x {_image.Height}",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(AvaloniaColor.FromRgb(200, 200, 200)),
            Margin = new Thickness(16, 0, 0, 0)
        };
        panel.Children.Add(dimensions);

        return panel;
    }

    private void Capture()
    {
        // Run the next scroll capture off the UI thread. A fresh capture uses
        // the shared options (already tuned by the Options dialog via _options).
        Task.Run(() => TaskHelpers.OpenScrollingCapture(_taskSettings));
    }

    private void UploadOrSave()
    {
        // Work on a clone so the window's close (which disposes our _image)
        // can never race an in-flight encode/upload/clipboard copy.
        SharpImage workClone = _image.Clone(ctx => { });
        Task.Run(() =>
        {
            try
            {
                if (_options.AutoUpload)
                {
                    // WorkerTask takes ownership of the clone and disposes it
                    // when the upload completes.
                    UploadManager.RunImageTask(workClone, _taskSettings);
                }
                else
                {
                    try
                    {
                        TaskHelpers.SaveImageAsFile(workClone, _taskSettings);

                        // Copy to the clipboard as a convenience, mirroring the
                        // worker's after-capture behavior. Wait for the frontend
                        // to finish its native bitmap copy before releasing the
                        // clone.
                        var clipboardEvent = new NeedClipboardCopyEvent(workClone);
                        SnapXL.EventAggregator.Publish(clipboardEvent);
                        if (clipboardEvent.Completion.Wait(TimeSpan.FromSeconds(5))
                            && clipboardEvent.Completion.GetAwaiter().GetResult())
                        {
                            DebugHelper.WriteLine("Scrolling capture image copied to clipboard.");
                        }
                        else
                        {
                            DebugHelper.WriteLine("Scrolling capture clipboard copy timed out.");
                        }
                    }
                    finally
                    {
                        workClone.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Scrolling capture save/upload failed");
                try { workClone.Dispose(); } catch { /* already released */ }
            }
        });
    }

    private void ShowOptions()
    {
        var dialog = new ScrollingCaptureOptionsDialog(_options);
        dialog.ShowDialog(this);
    }

    private void DisposeResources()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _bitmap?.Dispose();
        _image?.Dispose();
    }

    /// <summary>
    /// Modal Options dialog that edits the shared <see cref="ScrollingCaptureOptions"/>
    /// instance in place, so the next capture reuses the tuned values.
    /// </summary>
    private sealed class ScrollingCaptureOptionsDialog : Window
    {
        private readonly ScrollingCaptureOptions _options;
        private readonly NumericUpDown _startDelay;
        private readonly NumericUpDown _scrollDelay;
        private readonly NumericUpDown _scrollAmount;
        private readonly ComboBox _scrollMethod;
        private readonly CheckBox _autoScrollTop;
        private readonly CheckBox _autoUpload;
        private readonly CheckBox _autoIgnoreBottomEdge;
        private readonly CheckBox _showRegion;

        public ScrollingCaptureOptionsDialog(ScrollingCaptureOptions options)
        {
            _options = options;

            Title = "SnapX | Scrolling capture options";
            Width = 380;
            Height = 520;
            Background = new SolidColorBrush(AvaloniaColor.FromRgb(30, 30, 30));
            CanResize = false;

            _startDelay = new NumericUpDown { Minimum = 0, Maximum = 10000, Value = options.StartDelay, Margin = new Thickness(4) };
            _scrollDelay = new NumericUpDown { Minimum = 0, Maximum = 5000, Value = options.ScrollDelay, Margin = new Thickness(4) };
            _scrollAmount = new NumericUpDown { Minimum = 1, Maximum = 20, Value = options.ScrollAmount, Margin = new Thickness(4) };

            _scrollMethod = new ComboBox { Margin = new Thickness(4), MinWidth = 160 };
            foreach (var method in (ScrollMethod[])Enum.GetValues(typeof(ScrollMethod)))
            {
                _scrollMethod.Items.Add(method);
            }
            _scrollMethod.SelectedItem = options.ScrollMethod;

            _autoScrollTop = new CheckBox { Content = "Scroll to top before capturing", IsChecked = options.AutoScrollTop, Margin = new Thickness(4) };
            _autoUpload = new CheckBox { Content = "Upload after capture", IsChecked = options.AutoUpload, Margin = new Thickness(4) };
            _autoIgnoreBottomEdge = new CheckBox { Content = "Ignore bottom edge", IsChecked = options.AutoIgnoreBottomEdge, Margin = new Thickness(4) };
            _showRegion = new CheckBox { Content = "Show region selection", IsChecked = options.ShowRegion, Margin = new Thickness(4) };

            var form = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 8,
                Margin = new Thickness(12)
            };
            form.Children.Add(Labeled("Start delay (ms)", _startDelay));
            form.Children.Add(Labeled("Scroll delay (ms)", _scrollDelay));
            form.Children.Add(Labeled("Scroll amount", _scrollAmount));
            form.Children.Add(Labeled("Scroll method", _scrollMethod));
            form.Children.Add(_autoScrollTop);
            form.Children.Add(_autoUpload);
            form.Children.Add(_autoIgnoreBottomEdge);
            form.Children.Add(_showRegion);

            var ok = new Button { Content = "OK", MinWidth = 80, Margin = new Thickness(4) };
            ok.Click += (_, _) =>
            {
                _options.StartDelay = (int)(_startDelay.Value ?? _options.StartDelay);
                _options.ScrollDelay = (int)(_scrollDelay.Value ?? _options.ScrollDelay);
                _options.ScrollAmount = (int)(_scrollAmount.Value ?? _options.ScrollAmount);
                if (_scrollMethod.SelectedItem is ScrollMethod method)
                {
                    _options.ScrollMethod = method;
                }
                _options.AutoScrollTop = _autoScrollTop.IsChecked == true;
                _options.AutoUpload = _autoUpload.IsChecked == true;
                _options.AutoIgnoreBottomEdge = _autoIgnoreBottomEdge.IsChecked == true;
                _options.ShowRegion = _showRegion.IsChecked == true;
                Close();
            };

            var cancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(4) };
            cancel.Click += (_, _) => Close();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 12, 12)
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var root = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto)
                }
            };
            root.Children.Add(form);
            Grid.SetRow(form, 0);
            root.Children.Add(buttons);
            Grid.SetRow(buttons, 1);

            Content = new Border
            {
                Background = new SolidColorBrush(AvaloniaColor.FromRgb(30, 30, 30)),
                Child = root
            };
        }

        private static Control Labeled(string label, Control control)
        {
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(200))
                }
            };
            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(AvaloniaColor.FromRgb(200, 200, 200)),
                Margin = new Thickness(4)
            };
            grid.Children.Add(text);
            Grid.SetColumn(text, 0);
            grid.Children.Add(control);
            Grid.SetColumn(control, 1);
            return grid;
        }
    }
}
