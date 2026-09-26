using Ruffles21.DuplicatePhotoFinder;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;

static class Tests
{
    static int passed;
    static void Check(bool condition, string label)
    {
        if (!condition)
            throw new Exception("FAILED: " + label);
        Console.WriteLine("PASS: " + label);
        passed++;
    }
    static void Reject(Action action, string label)
    {
        try
        {
            action();
        }
        catch (IOException) { Check(true, label); return; }
        catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception("FAILED: " + label);
    }
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--scan-folder")
        {
            var realScanner = new Scanner();
            int realGroups = 0;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            long lastReport = -10;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                realScanner.Scan(new[] { args[1] }, _ => realGroups++, _ => { long seconds = elapsed.ElapsedMilliseconds / 1000; if (seconds - lastReport >= 5) { lastReport = seconds; Console.WriteLine($"{seconds}s: {realScanner.PhotosFound} photos, {realScanner.VideosFound} videos, {realGroups} verified groups, {realScanner.Warnings.Count} warnings"); } }, timeout.Token);
            }
            catch (OperationCanceledException) { Console.WriteLine("Diagnostic scan reached its 3-minute limit."); }
            Console.WriteLine($"FINAL: {realScanner.PhotosFound} photos, {realScanner.VideosFound} videos, {realScanner.FilesExamined} files examined, {realScanner.UnsupportedFiles} other files, {realGroups} groups, {realScanner.Warnings.Count} warnings, {elapsed.Elapsed.TotalSeconds:0.0}s.");
            foreach (var warning in realScanner.Warnings.Take(8))
                Console.WriteLine(warning);
            return;
        }
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string Add(string name, string data)
        {
            string p = Path.Combine(root, name);
            File.WriteAllText(p, data);
            return p;
        }
        string a = Add("original.jpg", "identical bytes");
        string b = Add("copy.jpg", "identical bytes");
        Add("different.jpg", "different bytes");
        Add("ignore.txt", "identical bytes");
        var groups = new List<Group>();
        var scanner = new Scanner();
        scanner.Scan(new[] { root, root }, groups.Add, _ => { }, CancellationToken.None);
        Check(groups.Count == 1 && groups[0].Photos.Count == 2, "Exact matching, extension filter and overlapping folder deduplication");
        var group = groups[0];
        Check(group.Photos.Count(p => p.IsKeep) == 1 && group.Photos.All(p => !p.Marked), "Scan protects one survivor without marking anything");
        group.AutoMark();
        Check(group.Photos.Count(p => p.Marked) == 1, "Auto Mark leaves one survivor");
        group.Keep.Marked = true;
        Check(!group.Keep.Marked, "Protected copy cannot be marked");
        var previous = group.Keep;
        var next = group.Photos.Single(p => !p.IsKeep);
        group.Protect(next);
        previous.Marked = true;
        Check(!next.Marked && previous.Marked && group.Photos.Count(p => p.IsKeep) == 1, "Changing KEEP clears its selection and releases prior KEEP");
        group.Clear();
        Check(group.Photos.All(p => !p.Marked) && group.Keep == next, "Unmark preserves protection");
        Reject(() => SafeRemoval.Remove(next, _ => throw new Exception("should not call")), "Removal rejects protected copy");
        group.AutoMark();
        bool called = false;
        SafeRemoval.Remove(previous, _ => called = true);
        Check(called, "Valid redundant copy passes full removal revalidation");
        File.WriteAllText(previous.Path, "altered content");
        File.SetLastWriteTimeUtc(previous.Path, previous.Modified);
        Reject(() => SafeRemoval.Remove(previous, _ => throw new Exception("should not call")), "Same-length content change with restored timestamp blocks removal");
        File.WriteAllText(previous.Path, "identical bytes");
        File.SetLastWriteTimeUtc(previous.Path, previous.Modified);
        File.Move(next.Path, next.Path + ".missing");
        Reject(() => SafeRemoval.Remove(previous, _ => throw new Exception("should not call")), "Missing protected original blocks removal");
        File.Move(next.Path + ".missing", next.Path);
        Check(Group.IsWithin(@"D:\Master Photos\a.jpg", @"D:\Master Photos") && !Group.IsWithin(@"D:\Master Photos Backup\a.jpg", @"D:\Master Photos"), "Preferred folder respects directory boundaries");
        group.ApplyRule("Keep shortest filename", "", "", overrideManual: true);
        Check(group.Keep.Name == "copy.jpg", "Shortest filename preference");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool stopped = false;
        try
        {
            scanner.Scan(new[] { root }, _ => { }, _ => { }, canceled.Token);
        }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "Cancellation stops scanning");
        var nested = Path.Combine(root, "nested media");
        Directory.CreateDirectory(nested);
        foreach (string extension in new[] { ".JPG", ".MOV", ".3gp", ".mts", ".CR3", ".avif" })
        {
            File.WriteAllText(Path.Combine(nested, "one" + extension), "fixture " + extension);
            File.WriteAllText(Path.Combine(nested, "two" + extension), "fixture " + extension);
        }
        var mediaGroups = new List<Group>();
        var updates = new List<string>();
        var mediaScanner = new Scanner();
        mediaScanner.Scan(new[] { nested }, mediaGroups.Add, updates.Add, CancellationToken.None);
        Check(mediaGroups.Count == 6 && mediaScanner.PhotosFound == 6 && mediaScanner.VideosFound == 6, "Uppercase, phone video, camera RAW and modern photo extensions are discovered");
        Check(updates.First().StartsWith("Starting scan") && updates.Last().Contains("6 photos"), "Small scans report start and completion without being lost to throttling");
        int hashUpdates = 0;
        using var large = new MemoryStream(new byte[1024 * 1024]);
        Scanner.Hash(large, CancellationToken.None, _ => hashUpdates++);
        Check(hashUpdates >= 8, "Large files report incremental hashing progress");
        using var hashCancel = new CancellationTokenSource();
        bool hashStopped = false;
        try
        {
            Scanner.Hash(large, hashCancel.Token, _ => hashCancel.Cancel());
        }
        catch (OperationCanceledException) { hashStopped = true; }
        Check(hashStopped, "Cancellation interrupts hashing within a file");
        var links = Path.Combine(root, "hardlinks");
        Directory.CreateDirectory(links);
        var linkOriginal = Path.Combine(links, "original.jpg");
        File.WriteAllText(linkOriginal, "same physical file");
        if (!CreateHardLink(Path.Combine(links, "alias.jpg"), linkOriginal, IntPtr.Zero))
            throw new Exception("Hard-link test setup failed");
        var linkGroups = new List<Group>();
        new Scanner().Scan(new[] { links }, linkGroups.Add, _ => { }, CancellationToken.None);
        Check(linkGroups.Count == 0, "Hard-link aliases are never offered as redundant files");
        File.Copy(linkOriginal, Path.Combine(links, "real-copy.jpg"));
        new Scanner().Scan(new[] { links }, linkGroups.Add, _ => { }, CancellationToken.None);
        Check(linkGroups.Count == 1 && linkGroups[0].Photos.Count == 2, "Hard-link deduplication still includes genuine independent copies");
        if (args.Contains("--recycle-test"))
        {
            var recycleDir = Path.Combine(root, "recycle");
            Directory.CreateDirectory(recycleDir);
            File.WriteAllText(Path.Combine(recycleDir, "keep.png"), "disposable test file");
            File.WriteAllText(Path.Combine(recycleDir, "remove.png"), "disposable test file");
            var recycleGroups = new List<Group>();
            new Scanner().Scan(new[] { recycleDir }, recycleGroups.Add, _ => { }, CancellationToken.None);
            var rg = recycleGroups.Single();
            rg.AutoMark();
            var target = rg.Photos.Single(p => p.Marked);
            SafeRemoval.Remove(target, Recycle.Move);
            Check(!File.Exists(target.Path) && File.Exists(rg.Keep.Path), "Windows Recycle Bin integration on disposable test files");
        }
        passed += CoreRegressionTests.Run(root);
        passed += SafetyRegressionTests.Run(root);
        var recycleGuard = new Recycle.RecycleProgressSink("unused", CancellationToken.None);
        Check(recycleGuard.PreDeleteItem(0, null!) < 0 && recycleGuard.Error!.Contains("Recycle Bin"), "Shell removal callback rejects an operation without recycling enabled");
        Render(root, args.Contains("--video-test"));
        Console.WriteLine($"{passed} checks passed. Fixtures: {root}");
    }
    static void Render(string root, bool testVideo)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var mediaChecks = MediaRegressionTests.RunAsync(root);
        Pump(mediaChecks);
        passed += mediaChecks.Result;
        if (testVideo)
        {
            var videoChecks = MediaRegressionTests.RunVideoAsync(root);
            Pump(videoChecks);
            passed += videoChecks.Result;
        }
        // Original procedural sample media, kept entirely inside test artifacts.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(70, 133, 154), Color.FromRgb(226, 213, 172), 90), null, new Rect(0, 0, 720, 480));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(251, 224, 166)), null, new Point(560, 110), 46, 46);
            var mountain = Geometry.Parse("M0,400 L160,155 L330,370 L470,190 L720,440 L720,480 L0,480 Z");
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(50, 91, 92)), null, mountain);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(102, 156, 157)), null, new Rect(0, 400, 720, 80));
        }
        var sample = new RenderTargetBitmap(720, 480, 96, 96, PixelFormats.Pbgra32);
        sample.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sample));
        string original = Path.Combine(root, "Lake morning.png");
        using (var file = File.Create(original))
            encoder.Save(file);
        var paths = new[] { original, Path.Combine(root, "Lake morning - copy.png"), Path.Combine(root, "Lake morning backup.png") };
        File.Copy(original, paths[1]);
        File.Copy(original, paths[2]);
        var window = new MainWindow(false) { ShowInTaskbar = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        var collection = (ObservableCollection<Group>)typeof(MainWindow).GetField("groups", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        for (int i = 1; i <= 5000; i++)
        {
            var group = new Group(i, paths.Select(p => new Photo(p)));
            if (i <= 2)
                group.AutoMark();
            collection.Add(group);
        }
        ((FrameworkElement)window.FindName("Empty")).Visibility = Visibility.Collapsed;
        ((TextBlock)window.FindName("Summary")).Text = "Duplicate Photos Found: 15,000  ·  5,000 groups  ·  Space to Be Saved: 8.24 GB";
        ((Button)window.FindName("ExactTab")).Content = "Exact (5,000)";
        ((TextBlock)window.FindName("FilterHint")).Text = "Showing 5,000 exact groups · Auto Mark and Unmark All apply to all exact results";
        ((TextBlock)window.FindName("ProgressText")).Text = "Scan complete · Demonstration data for layout verification";
        ((TextBlock)window.FindName("SelectionText")).Text = "Files selected: 4   ·   Original copies protected: 5,000";
        ((Button)window.FindName("RemoveButton")).Content = "Move Selected to Recycle Bin (4)";
        ((Button)window.FindName("RemoveButton")).IsEnabled = true;
        ((Image)window.FindName("PreviewImage")).Source = sample;
        ((TextBlock)window.FindName("PreviewHint")).Text = "";
        ((TextBlock)window.FindName("PreviewStatus")).Text = "KEEP · protected";
        ((TextBlock)window.FindName("Metadata")).Text = "File Name\nLake morning.png\n\nFile Size\n3.2 MB\n\nFile Created\nSeptember 20, 2026\n\nImage Size\n720 × 480\n\nFolder Name\nD:\\Master Photos";
        window.Show();
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        content.UpdateLayout();
        var list = (ListBox)window.FindName("GroupsList");
        int realized = Enumerable.Range(0, 5000).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
        Check(realized > 0 && realized < 30, $"Group virtualization: {realized} / 5,000 groups realized");
        var screenshot = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        screenshot.Render(content);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(screenshot));
        Directory.CreateDirectory("artifacts");
        using (var file = File.Create("artifacts/results-preview.png"))
            png.Save(file);
        Check(Descendants(content).OfType<Thumbnail>().Any(t => t.Source != null), "Visible thumbnail workers produce decoded images");
        var firstCards = Descendants(content).OfType<ListBox>().First(l => l.Items.Count > 0 && l.Items[0] is Photo);
        var before = collection[0].Photos.Select(p => p.Marked).ToArray();
        firstCards.SelectedIndex = 1;
        Check(before.SequenceEqual(collection[0].Photos.Select(p => p.Marked)), "Inspecting a card does not change removal selection");
        // Compact and list layouts instantiate successfully with the same large collection.
        foreach (string mode in new[] { "Compact", "ListView" })
        {
            typeof(MainWindow).GetMethod(mode, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            content.UpdateLayout();
            Check(list.ItemContainerGenerator.ContainerFromIndex(0) != null, mode + " renders");
        }
        Check(Thumbnail.Decode(original, 360).PixelWidth == 360, "Thumbnails are capped");
        window.Width = 1080;
        window.Height = 720;
        var resizeFrame = new DispatcherFrame();
        var resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        resizeTimer.Tick += (_, _) => { resizeTimer.Stop(); resizeFrame.Continue = false; };
        resizeTimer.Start();
        Dispatcher.PushFrame(resizeFrame);
        content.UpdateLayout();
        Check(content.ActualWidth <= 1080 && ((FrameworkElement)window.FindName("RemoveButton")).IsVisible, "Minimum window size lays out with the removal action visible");
        var previewScroll = (ScrollViewer)window.FindName("PreviewScroll");
        Check(previewScroll.ScrollableHeight > 0, "Preview controls and metadata remain reachable by scrolling in small windows");
        var small = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        small.Render(content);
        var smallPng = new PngBitmapEncoder();
        smallPng.Frames.Add(BitmapFrame.Create(small));
        using (var file = File.Create("artifacts/compact-window-preview.png"))
            smallPng.Save(file);
        var displayedGroup = collection[0];
        var liveCards = Descendants(content).OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, displayedGroup.Photos));
        displayedGroup.RemoveRecycled(displayedGroup.Photos.Last());
        content.UpdateLayout();
        Check(liveCards.Items.Count == 2 && Descendants(content).OfType<TextBlock>().Any(t => t.Text == "2 copies · Verified exact"), "A completed removal updates the remaining cards and group count immediately");
        var filteredGroup = new Group(5001, paths.Select(p => new Photo(p)));
        collection.Add(filteredGroup);
        var matchingCopy = filteredGroup.Photos.Last();
        ((TextBox)window.FindName("SearchBox")).Text = matchingCopy.Name;
        Invoke(window, "ApplyFilters", false);
        Check(list.Items.Contains(filteredGroup), "A group is visible when one copy matches the active search");
        typeof(MainWindow).GetMethod("ApplyRemovalResult", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
            new object[] { new RemovalResult(new[] { matchingCopy }, Array.Empty<string>(), false) });
        Check(collection.Contains(filteredGroup) && filteredGroup.CopyCount == 2 && !list.Items.Contains(filteredGroup),
            "Removing the only matching filename hides a surviving group from the active search immediately");
        window.Content = null;
        window.Close();
        foreach (string newerAction in new[] { "Fit to window", "Slider", "Actual size" })
        {
            var pendingDecode = new TaskCompletionSource<BitmapSource>();
            var zoomWindow = new PreviewWindow(original, sample, (_, _, _) => pendingDecode.Task)
            {
                ShowInTaskbar = false,
                Left = -32000,
                Top = -32000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            zoomWindow.Show();
            ((FrameworkElement)zoomWindow.Content).UpdateLayout();
            var zoomImage = Descendants(zoomWindow).OfType<Image>().Single();
            var actualButton = Descendants(zoomWindow).OfType<Button>().Single(b => Equals(b.Content, "Actual size"));
            actualButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (newerAction == "Fit to window")
                Descendants(zoomWindow).OfType<Button>().Single(b => Equals(b.Content, newerAction)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (newerAction == "Slider")
                Descendants(zoomWindow).OfType<Slider>().Single().Value = 75;
            double requestedWidth = zoomImage.Width;
            var fullSize = new TransformedBitmap(sample, new ScaleTransform(2, 2));
            fullSize.Freeze();
            pendingDecode.SetResult(fullSize);
            Pump(Task.Delay(30));
            if (newerAction == "Actual size")
                Check(ReferenceEquals(zoomImage.Source, fullSize) && Math.Abs(zoomImage.Width - fullSize.PixelWidth / VisualTreeHelper.GetDpi(zoomWindow).DpiScaleX) < 0.01,
                    "The current full-size preview request still applies at actual pixel size");
            else
                Check(ReferenceEquals(zoomImage.Source, sample) && Math.Abs(zoomImage.Width - requestedWidth) < 0.01,
                    $"A pending full-size preview cannot override a newer {newerAction} choice");
            zoomWindow.Close();
        }
        var scanWindow = new MainWindow(false) { ShowInTaskbar = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        scanWindow.Show();
        var scanTask = scanWindow.ScanFoldersAsync(new[] { Path.Combine(root, "nested media") });
        Check(((FrameworkElement)scanWindow.FindName("ScanActivity")).Visibility == Visibility.Visible && ((TextBlock)scanWindow.FindName("EmptyHeading")).Text.StartsWith("Scanning"), "Choosing folders immediately displays active scanning state");
        Check(!((FrameworkElement)scanWindow.FindName("ExactActions")).IsEnabled, "Global marking is unavailable until scanning finishes or stops");
        Pump(scanTask);
        var realList = (ListBox)scanWindow.FindName("GroupsList");
        Check(realList.Items.Count == 6 && ((FrameworkElement)scanWindow.FindName("Empty")).Visibility == Visibility.Collapsed, "Folder-to-results UI flow populates verified photo and video groups");
        Check(((TextBlock)scanWindow.FindName("Summary")).Text.Contains("6 photos + 6 videos"), "Results show detected photo and video counts separately from duplicates");
        Check(!((Button)scanWindow.FindName("RemoveButton")).IsEnabled, "New scan never starts with removal enabled");
        var unique = Path.Combine(root, "unique-only");
        Directory.CreateDirectory(unique);
        File.WriteAllText(Path.Combine(unique, "unique.MOV"), "unique video fixture");
        Pump(scanWindow.ScanFoldersAsync(new[] { unique }));
        Check(realList.Items.Count == 0 && ((TextBlock)scanWindow.FindName("EmptyHeading")).Text == "No exact duplicates found" && ((TextBlock)scanWindow.FindName("EmptyDescription")).Text.Contains("1 videos"), "Media without duplicates shows an explicit completed result, not the initial welcome screen");
        var emptyFolder = Path.Combine(root, "no-media");
        Directory.CreateDirectory(emptyFolder);
        File.WriteAllText(Path.Combine(emptyFolder, "notes.txt"), "text");
        Pump(scanWindow.ScanFoldersAsync(new[] { emptyFolder }));
        Check(((TextBlock)scanWindow.FindName("EmptyHeading")).Text == "No media files were found", "Empty scans distinguish no media from no duplicates");
        var visualFolder = Path.Combine(root, "visual-modes");
        Directory.CreateDirectory(visualFolder);
        File.Copy(original, Path.Combine(visualFolder, "original.png"));
        File.Copy(original, Path.Combine(visualFolder, "exact-copy.png"));
        var recompressed = new JpegBitmapEncoder { QualityLevel = 82 };
        recompressed.Frames.Add(BitmapFrame.Create(sample));
        using (var file = File.Create(Path.Combine(visualFolder, "recompressed.jpg")))
            recompressed.Save(file);
        ((CheckBox)scanWindow.FindName("ScanSimilarChoice")).IsChecked = true;
        Pump(scanWindow.ScanFoldersAsync(new[] { visualFolder }));
        var similarList = (ListBox)scanWindow.FindName("SimilarList");
        Check(realList.Items.Count == 1 && similarList.Items.Count == 1, "Both scan modes produce separate exact and similar groups from one folder selection");
        var exactGroup = (Group)realList.Items[0];
        var similarGroup = (SimilarityGroup)similarList.Items[0];
        Check(exactGroup.CopyCount == 2 && similarGroup.Photos.Count == 3 && exactGroup.Photos.All(p => !p.Marked), "Recompressed photo is similar but not an exact duplicate, and no file is auto-selected");
        Invoke(scanWindow, "ShowSimilar");
        Invoke(scanWindow, "AutoMark");
        Check(((FrameworkElement)scanWindow.FindName("ExactActions")).Visibility == Visibility.Collapsed && ((Button)scanWindow.FindName("RemoveButton")).Visibility == Visibility.Collapsed && exactGroup.Photos.All(p => !p.Marked), "Similar tab cannot auto-mark or expose removal controls");
        var similarContent = (FrameworkElement)scanWindow.Content;
        similarContent.UpdateLayout();
        Pump(Task.Delay(300));
        Check(Descendants(similarContent).OfType<Thumbnail>().Any(t => t.IsVisible && t.Source != null), "Similar results show decoded photo thumbnails");
        var similarShot = new RenderTargetBitmap((int)similarContent.ActualWidth, (int)similarContent.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        similarShot.Render(similarContent);
        var similarPng = new PngBitmapEncoder();
        similarPng.Frames.Add(BitmapFrame.Create(similarShot));
        using (var file = File.Create("artifacts/similar-results-preview.png"))
            similarPng.Save(file);
        Invoke(scanWindow, "ShowExact");
        exactGroup.Protect(exactGroup.Photos[1]);
        Invoke(scanWindow, "AutoMark");
        Check(exactGroup.Keep == exactGroup.Photos[1] && exactGroup.Photos.Count(p => p.Marked) == 1, "Global Auto Mark respects the manual KEEP in the live results UI");
        ((FrameworkElement)scanWindow.Content).UpdateLayout();
        var inspectedCards = Descendants(scanWindow).OfType<ListBox>().First(l => l.IsVisible && l.Items.Count > 0 && l.Items[0] is Photo);
        inspectedCards.SelectedIndex = 0;
        ((TextBox)scanWindow.FindName("SearchBox")).Text = "nothing-matches-this";
        Invoke(scanWindow, "ApplyFilters", false);
        Check(realList.Items.Count == 0 && exactGroup.Photos.Count(p => p.Marked) == 1 && ((TextBlock)scanWindow.FindName("EmptyHeading")).Text.Contains("filters"), "Filtering hides groups without changing their removal selections");
        Check(((Image)scanWindow.FindName("PreviewImage")).Source == null && !((FrameworkElement)scanWindow.FindName("PreviewExactActions")).IsEnabled, "Filtering away the inspected group clears its preview and copy actions");
        ((TextBox)scanWindow.FindName("SearchBox")).Clear();
        ((ComboBox)scanWindow.FindName("MediaScope")).SelectedIndex = 2;
        Invoke(scanWindow, "ApplyFilters", false);
        Check(realList.Items.Count == 0, "Video filter excludes photo-only groups");
        ((ComboBox)scanWindow.FindName("MediaScope")).SelectedIndex = 0;
        Invoke(scanWindow, "ApplyFilters", false);
        ((CheckBox)scanWindow.FindName("ScanExactChoice")).IsChecked = false;
        Pump(scanWindow.ScanFoldersAsync(new[] { visualFolder }));
        Check(realList.Items.Count == 0 && similarList.Items.Count == 1 && ((FrameworkElement)scanWindow.FindName("SimilarList")).Visibility == Visibility.Visible, "Similar-only scanning is independent of exact matching");
        ((CheckBox)scanWindow.FindName("ScanExactChoice")).IsChecked = true;
        var stopTask = scanWindow.ScanFoldersAsync(new[] { visualFolder });
        Invoke(scanWindow, "PauseScan");
        Invoke(scanWindow, "StopScan");
        Pump(stopTask);
        Check(((Button)scanWindow.FindName("ChooseButton")).IsEnabled && !((Button)scanWindow.FindName("StopButton")).IsEnabled && ((TextBlock)scanWindow.FindName("ProgressText")).Text.Contains("stopped"), "Stopping a paused combined scan restores usable controls without a deadlock");
        var closeTask = scanWindow.ScanFoldersAsync(new[] { visualFolder });
        Invoke(scanWindow, "PauseScan");
        scanWindow.Close();
        Pump(closeTask);
        Check(!scanWindow.IsVisible, "Closing during a paused scan cancels the worker and closes the window safely");
        app.Shutdown();
    }
    static void Invoke(MainWindow window, string method, bool eventArgs = true) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, eventArgs ? new object[] { window, new RoutedEventArgs() } : null);
    static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted || elapsed.Elapsed.TotalSeconds > 30) { timer.Stop(); frame.Continue = false; } };
        timer.Start();
        Dispatcher.PushFrame(frame);
        if (!task.IsCompleted)
            throw new Exception("UI scan timed out");
        task.GetAwaiter().GetResult();
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] static extern bool CreateHardLink(string newName, string existingName, IntPtr reserved);
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
