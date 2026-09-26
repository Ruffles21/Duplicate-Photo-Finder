using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ruffles21.DuplicatePhotoFinder;

public sealed class PreviewWindow : Window
{
    private readonly Image image;
    private readonly ScrollViewer scroll;
    private readonly Slider zoom;
    private readonly TextBlock zoomLabel;
    private readonly CancellationTokenSource lifetime = new();
    private bool fitted = true;
    private bool updating;
    private int viewRequest;

    public PreviewWindow(string path, ImageSource initial) : this(path, initial, Thumbnail.DecodeAsync) { }

    internal PreviewWindow(string path, ImageSource initial, Func<string, int, CancellationToken, Task<BitmapSource>> decode)
    {
        Title = Path.GetFileName(path);
        Width = 1040;
        Height = 780;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 33, 39));
        var dock = new DockPanel();
        Content = dock;
        var bar = new WrapPanel { Background = Brushes.White, Margin = new Thickness(10) };
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        image = new Image { Source = initial, Stretch = Stretch.Uniform };
        scroll = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dock.Children.Add(scroll);
        bar.Children.Add(MainWindow.Btn("Fit to window", () => { viewRequest++; fitted = true; Fit(); }));
        var actual = MainWindow.Btn("Actual size", async () =>
        {
            int request = ++viewRequest;
            try
            {
                if (!Scanner.VideoExtensions.Contains(Path.GetExtension(path)))
                {
                    var fullSize = await decode(path, 0, lifetime.Token);
                    if (request != viewRequest || lifetime.IsCancellationRequested)
                        return;
                    image.Source = fullSize;
                }
                fitted = false;
                SetScale(100);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (request == viewRequest && !lifetime.IsCancellationRequested) MessageBox.Show(this, ex.Message, "Full-size preview unavailable"); }
        });
        bar.Children.Add(actual);
        zoom = new Slider { Minimum = 10, Maximum = 300, Value = 100, Width = 170, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        zoomLabel = new TextBlock { Text = "100%", VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(zoom);
        bar.Children.Add(zoomLabel);
        zoom.ValueChanged += (_, _) => { if (!updating) { viewRequest++; fitted = false; SetScale(zoom.Value); } };
        scroll.SizeChanged += (_, _) => { if (fitted) Fit(); };
        Loaded += (_, _) => Fit();
        Closed += (_, _) => lifetime.Cancel();
    }

    private void SetScale(double percent)
    {
        if (image.Source is not BitmapSource bitmap)
            return;
        // One image pixel per device pixel at 100%, including scaled Windows displays.
        var dpi = VisualTreeHelper.GetDpi(this);
        image.Width = bitmap.PixelWidth * percent / 100 / dpi.DpiScaleX;
        image.Height = bitmap.PixelHeight * percent / 100 / dpi.DpiScaleY;
        updating = true;
        zoom.Value = Math.Clamp(percent, 10, 300);
        updating = false;
        zoomLabel.Text = $"{percent:0}%";
    }

    private void Fit()
    {
        if (image.Source is not BitmapSource bitmap)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = Math.Min(Math.Max(1, scroll.ActualWidth - 24) * dpi.DpiScaleX / bitmap.PixelWidth,
            Math.Max(1, scroll.ActualHeight - 24) * dpi.DpiScaleY / bitmap.PixelHeight);
        SetScale(scale * 100);
    }
}
