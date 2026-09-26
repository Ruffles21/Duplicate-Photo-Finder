using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ruffles21.DuplicatePhotoFinder;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Group> groups = new();
    private readonly ObservableCollection<SimilarityGroup> similarGroups = new();
    private readonly Dictionary<int, int> similarPositions = new();
    private readonly ICollectionView exactView;
    private readonly ICollectionView similarView;
    private readonly bool persistSettings;
    private readonly DispatcherTimer filterTimer;
    private readonly DispatcherTimer countTimer;
    private Settings settings;
    private Scanner? scanner;
    private SimilarityScanner? similarityScanner;
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? previewCancellation;
    private Photo? selected;
    private ListBox? inspectedList;
    private bool scanning, removing, batching, ready, closed, closeRequested;
    private bool viewSimilar, hasScanned, ranExact, ranSimilar, scanCanceled, similarInvalidated;
    private string? scanFailure;
    private long totalPhotos, possibleBytes;

    public MainWindow(bool persistSettings = true)
    {
        this.persistSettings = persistSettings;
        settings = persistSettings ? SettingsStore.Load(SettingsStore.DefaultPath) : new Settings();
        InitializeComponent();
        exactView = CollectionViewSource.GetDefaultView(groups);
        similarView = CollectionViewSource.GetDefaultView(similarGroups);
        exactView.Filter = item => ResultFilter.Matches(((Group)item).Photos, SearchBox.Text, MediaScope.SelectedIndex);
        similarView.Filter = item => ResultFilter.Matches(((SimilarityGroup)item).Photos, SearchBox.Text, MediaScope.SelectedIndex);
        GroupsList.ItemsSource = exactView;
        SimilarList.ItemsSource = similarView;
        Rules.SelectedIndex = settings.Rule;
        filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); ApplyFilters(); };
        countTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        countTimer.Tick += (_, _) => { countTimer.Stop(); UpdateCounts(); };
        ready = true;
        SetView(settings.View);
        SwitchResults(false);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            closed = true;
            filterTimer.Stop();
            countTimer.Stop();
            previewCancellation?.Cancel();
            SaveSettings();
        };
    }

    private void SaveSettings()
    {
        if (!persistSettings)
            return;
        try
        {
            settings.Rule = Rules.SelectedIndex;
            SettingsStore.Save(SettingsStore.DefaultPath, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProgressText.Text = "Preferences could not be saved: " + ex.Message;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!scanning && !removing)
            return;
        e.Cancel = true;
        closeRequested = true;
        cancellation?.Cancel();
        scanner?.Pause.Set();
        ProgressText.Text = "Stopping safely before closing…";
    }

    private void SetView(string mode)
    {
        settings.View = mode;
        bool compact = mode == "Compact", list = mode == "List";
        Resources["PhotoTemplate"] = Resources[list ? "RowCard" : compact ? "CompactCard" : "GalleryCard"];
        Resources["PhotoPanel"] = Resources[list ? "VerticalPanel" : "HorizontalPanel"];
        Resources["SimilarPhotoTemplate"] = Resources[list ? "SimilarRowCard" : "SimilarCard"];
        Resources["GroupHeight"] = compact ? 174d : 246d;
        Resources["SimilarCardWidth"] = compact ? 160d : 220d;
        Resources["SimilarImageHeight"] = compact ? 78d : 138d;
        SetActive(GalleryButton, mode == "Gallery");
        SetActive(CompactButton, compact);
        SetActive(ListButton, list);
    }

    private static void SetActive(Button button, bool active)
    {
        button.Background = active ? new SolidColorBrush(Color.FromRgb(222, 240, 234)) : Brushes.White;
        button.BorderBrush = active ? new SolidColorBrush(Color.FromRgb(20, 107, 88)) : new SolidColorBrush(Color.FromRgb(207, 216, 225));
    }

    private void Gallery(object sender, RoutedEventArgs e) => SetView("Gallery");
    private void Compact(object sender, RoutedEventArgs e) => SetView("Compact");
    private void ListView(object sender, RoutedEventArgs e) => SetView("List");
    private string Rule => ((ComboBoxItem)Rules.SelectedItem).Content.ToString()!;

    public async Task ScanFoldersAsync(IEnumerable<string> folders)
    {
        Dispatcher.VerifyAccess();
        if (scanning || removing)
            throw new InvalidOperationException("An operation is already running.");
        var chosen = folders.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (chosen.Count == 0)
            return;
        if (ScanExactChoice.IsChecked != true && ScanSimilarChoice.IsChecked != true)
        {
            ProgressText.Text = "Select Exact, Similar, or both before scanning.";
            return;
        }

        settings.Folders = chosen;
        SaveSettings();
        groups.Clear();
        similarGroups.Clear();
        similarPositions.Clear();
        Thumbnail.ClearCache();
        ClearPreview();
        totalPhotos = possibleBytes = 0;
        scanner = new Scanner();
        similarityScanner = new SimilarityScanner();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        ranExact = ScanExactChoice.IsChecked == true;
        ranSimilar = ScanSimilarChoice.IsChecked == true;
        int threshold = SimilarityLevel.SelectedIndex switch
        {
            0 => 4,
            2 => 12,
            _ => 8
        };
        string rule = Rule, preferred = settings.Preferred, backups = settings.Backups;
        scanning = hasScanned = true;
        scanCanceled = similarInvalidated = false;
        scanFailure = null;
        ProgressText.Text = "Starting scan…";
        SwitchResults(!ranExact);
        UpdateCounts();
        try
        {
            var files = await Task.Run(() => scanner.Discover(chosen, ReportProgress, token), token);
            if (ranExact)
            {
                await Task.Run(() => scanner.ScanFiles(files, group => Dispatch(() =>
                {
                    group.ApplyRule(rule, preferred, backups);
                    groups.Add(group);
                    totalPhotos += group.CopyCount;
                    possibleBytes += group.RecoverableBytes;
                    foreach (var photo in group.Photos)
                        photo.PropertyChanged += PhotoChanged;
                    ScheduleCounts();
                    UpdateSummary();
                    UpdateEmptyState();
                }), ReportProgress, token), token);
            }
            if (ranSimilar)
            {
                ReportProgress("Comparing visual appearance… Similar results require visual review.");
                await Task.Run(() => similarityScanner.ScanAsync(files, threshold, group => Dispatch(() =>
                {
                    if (similarPositions.TryGetValue(group.Id, out int existing))
                        similarGroups[existing] = group;
                    else
                    {
                        similarPositions.Add(group.Id, similarGroups.Count);
                        similarGroups.Add(group);
                    }
                    UpdateSummary();
                    UpdateEmptyState();
                }), ReportProgress, token, () => scanner.Pause.Wait(token)), token);
            }
            ProgressText.Text = $"Scan complete · {scanner.PhotosFound:N0} photos · {scanner.VideosFound:N0} videos · {groups.Count:N0} exact groups · {similarGroups.Count:N0} similar groups";
        }
        catch (OperationCanceledException)
        {
            scanCanceled = true;
            ProgressText.Text = "Scan stopped · Completed groups remain available for review.";
        }
        catch (Exception ex)
        {
            scanFailure = ex.Message;
            ProgressText.Text = "Scan could not finish: " + ex.Message;
        }
        finally
        {
            scanning = false;
            UpdateCounts();
            UpdateEmptyState();
            if (closeRequested)
                Close();
        }
    }

    private void Dispatch(Action action)
    {
        if (closed || Dispatcher.HasShutdownStarted)
            return;
        Dispatcher.Invoke(() => { if (!closed) action(); });
    }

    private void ReportProgress(string text) => Dispatch(() =>
    {
        if (closeRequested)
            return;
        ProgressText.Text = scanner?.Pause.IsSet == false ? "Paused · Resume when ready" : text;
        UpdateSummary();
        UpdateEmptyState();
    });

    private void PhotoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!batching)
            ScheduleCounts();
    }

    private void ScheduleCounts()
    {
        if (!countTimer.IsEnabled)
            countTimer.Start();
    }

    private void UpdateSummary()
    {
        Summary.Text = scanner == null
            ? "Find identical copies, or explore photos and videos that look alike."
            : $"Found: {scanner.PhotosFound:N0} photos + {scanner.VideosFound:N0} videos   |   Exact: {totalPhotos:N0} files in {groups.Count:N0} groups   ·   Recoverable: {Format.Size(possibleBytes)}";
        ExactTab.Content = $"Exact ({groups.Count:N0})";
        SimilarTab.Content = $"Similar ({similarGroups.Count:N0})";
        UpdateFilterHint();
    }

    private void UpdateCounts()
    {
        countTimer.Stop();
        var marked = groups.SelectMany(g => g.Photos).Where(p => p.Marked).ToList();
        SelectionText.Text = $"Selected across all exact results: {marked.Count:N0} files · {Format.Size(marked.Sum(p => p.Size))} · {groups.Count:N0} protected copies";
        RemoveButton.Content = $"Move Selected to Recycle Bin ({marked.Count:N0})";
        RemoveButton.IsEnabled = marked.Count > 0 && !scanning && !removing && !viewSimilar;
        RemoveButton.Visibility = viewSimilar ? Visibility.Collapsed : Visibility.Visible;
        ScanActivity.Visibility = scanning || removing ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsEnabled = scanning;
        if (!scanning)
            PauseButton.Content = "Pause";
        StopButton.IsEnabled = scanning || removing;
        StopButton.Content = removing ? "Cancel removal" : "Stop";
        Toolbar.IsEnabled = !removing;
        ExactActions.IsEnabled = !scanning && !removing;
        ScanChoices.IsEnabled = ChooseButton.IsEnabled = ManageButton.IsEnabled = NewScanButton.IsEnabled = !scanning && !removing;
        GroupsList.IsEnabled = SimilarList.IsEnabled = !removing;
        SearchBox.IsEnabled = MediaScope.IsEnabled = !removing;
        ExactTab.IsEnabled = SimilarTab.IsEnabled = !removing;
        PreviewExactActions.IsEnabled = selected != null && !removing;
        if (selected != null)
            PreviewStatus.Text = viewSimilar ? "SIMILAR · review only" : selected.Status;
        UpdateSummary();
        UpdateEmptyState();
    }

    private void Batch(Action action)
    {
        batching = true;
        try
        {
            action();
        }
        finally { batching = false; UpdateCounts(); }
    }

    private void AutoMark(object sender, RoutedEventArgs e)
    {
        if (scanning || removing || viewSimilar)
            return;
        Batch(() => { foreach (var group in groups) { group.ApplyRule(Rule, settings.Preferred, settings.Backups); group.AutoMark(); } });
    }

    private void UnmarkAll(object sender, RoutedEventArgs e)
    {
        if (!scanning && !removing && !viewSimilar)
            Batch(() => { foreach (var group in groups) group.Clear(); });
    }

    private void MarkGroup(object sender, RoutedEventArgs e)
    {
        if (!removing && ((FrameworkElement)sender).DataContext is Group group)
            Batch(group.AutoMark);
    }

    private void UnmarkGroup(object sender, RoutedEventArgs e)
    {
        if (!removing && ((FrameworkElement)sender).DataContext is Group group)
            Batch(group.Clear);
    }

    private void GroupDetails(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Group group)
            ShowText(group.Title, string.Join("\n\n", group.Photos.Select(p => $"{p.Status} · {p.Path}\n{p.Detail}\nSHA-256: {p.Hash}")));
    }

    private void KeepThis(object sender, RoutedEventArgs e)
    {
        if (!removing && !viewSimilar && selected?.Group != null)
            Batch(() => selected.Group.Protect(selected));
    }

    private void MarkThis(object sender, RoutedEventArgs e)
    {
        if (removing || viewSimilar || selected?.Group == null)
            return;
        if (selected.IsKeep)
        {
            MessageBox.Show(this, "Choose another copy in this group and click Keep This Copy first.");
            return;
        }
        selected.Marked = true;
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (!ready)
            return;
        filterTimer.Stop();
        filterTimer.Start();
    }

    private void ApplyFilters()
    {
        exactView.Refresh();
        similarView.Refresh();
        if (selected != null)
        {
            bool visible = viewSimilar
                ? SimilarList.Items.Cast<SimilarityGroup>().Any(g => g.Photos.Contains(selected))
                : GroupsList.Items.Cast<Group>().Any(g => g.Photos.Contains(selected));
            if (!visible)
                ClearPreview();
        }
        UpdateFilterHint();
        UpdateEmptyState();
    }

    private void UpdateFilterHint()
    {
        int count = viewSimilar ? SimilarList.Items.Count : GroupsList.Items.Count;
        int total = viewSimilar ? similarGroups.Count : groups.Count;
        FilterHint.Text = viewSimilar
            ? $"Showing {count:N0} of {total:N0} similar groups · Visual review only; these are not verified duplicates"
            : $"Showing {count:N0} of {total:N0} exact groups · Auto Mark and Unmark All apply to all exact results";
    }

    private void ShowExact(object sender, RoutedEventArgs e) => SwitchResults(false);
    private void ShowSimilar(object sender, RoutedEventArgs e) => SwitchResults(true);

    private void SwitchResults(bool similar)
    {
        viewSimilar = similar;
        GroupsList.Visibility = similar ? Visibility.Collapsed : Visibility.Visible;
        SimilarList.Visibility = similar ? Visibility.Visible : Visibility.Collapsed;
        ExactActions.Visibility = PreviewExactActions.Visibility = similar ? Visibility.Collapsed : Visibility.Visible;
        SetActive(ExactTab, !similar);
        SetActive(SimilarTab, similar);
        ClearPreview();
        UpdateCounts();
    }

    private void UpdateEmptyState()
    {
        int visible = viewSimilar ? SimilarList.Items.Count : GroupsList.Items.Count;
        int total = viewSimilar ? similarGroups.Count : groups.Count;
        Empty.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visible > 0)
            return;
        EmptyAction.Visibility = scanning ? Visibility.Collapsed : Visibility.Visible;
        EmptyAction.Content = "Choose folders & scan";
        if (total > 0)
        {
            EmptyHeading.Text = "No groups match your filters";
            EmptyDescription.Text = "Try a different file name, folder, or media type.";
            EmptyAction.Content = "Clear search & filters";
        }
        else if (scanning)
        {
            EmptyHeading.Text = "Scanning your photos and videos…";
            EmptyDescription.Text = ProgressText.Text;
        }
        else if (!hasScanned)
        {
            EmptyHeading.Text = "A fresh look at your library";
            EmptyDescription.Text = "Choose Exact, Similar, or both, then select the folders you want to scan.";
        }
        else if (viewSimilar && (!ranSimilar || similarInvalidated))
        {
            EmptyHeading.Text = similarInvalidated ? "Refresh similar results" : "Similar scan hasn't run yet";
            EmptyDescription.Text = similarInvalidated
                ? "Files changed after recycling. Run another scan to update visual matches."
                : "Select Similar photos & videos above, then choose your folders.";
        }
        else if (!viewSimilar && !ranExact)
        {
            EmptyHeading.Text = "Exact scan hasn't run yet";
            EmptyDescription.Text = "Select Exact duplicates above to verify identical copies.";
        }
        else
        {
            EmptyHeading.Text = scanFailure != null ? "The scan could not finish" : scanCanceled ? "Scan stopped"
                : scanner?.MediaFiles == 0 ? "No media files were found"
                : viewSimilar ? "No similar groups found" : "No exact duplicates found";
            EmptyDescription.Text = scanFailure ?? $"Found {scanner?.PhotosFound ?? 0:N0} photos and {scanner?.VideosFound ?? 0:N0} videos. " +
                (viewSimilar ? "Try a broader similarity setting, or check the scan log for unsupported media."
                : "No identical groups are available in these results.");
            int warnings = (scanner?.Warnings.Count ?? 0) + (similarityScanner?.Warnings.Count ?? 0);
            if (warnings > 0)
                EmptyDescription.Text += $" {warnings:N0} warnings — open Scan log for details.";
        }
    }

    private void EmptyActionClick(object sender, RoutedEventArgs e)
    {
        if ((viewSimilar ? similarGroups.Count : groups.Count) > 0)
        {
            SearchBox.Clear();
            MediaScope.SelectedIndex = 0;
            ApplyFilters();
        }
        else
            ChooseFolders(sender, e);
    }

    private async void Inspect(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not Photo photo)
            return;
        if (inspectedList != sender)
        {
            if (inspectedList != null)
                inspectedList.SelectedItem = null;
            inspectedList = (ListBox)sender;
        }
        selected = photo;
        previewCancellation?.Cancel();
        using var request = new CancellationTokenSource();
        previewCancellation = request;
        PreviewImage.Source = null;
        PreviewHint.Text = "Loading preview…";
        PreviewStatus.Text = viewSimilar ? "SIMILAR · review only" : photo.Status;
        HashText.Text = photo.Hash.Length == 0 ? "No full hash was computed for this item." : "SHA-256\n" + photo.Hash;
        Metadata.Text = "Loading metadata…";
        PreviewExactActions.IsEnabled = !removing;
        PreviewScroll.ScrollToTop();
        try
        {
            var result = await Thumbnail.PreviewAsync(photo, request.Token);
            if (previewCancellation != request || closed)
                return;
            PreviewImage.Source = result.Image;
            PreviewHint.Text = result.Image == null ? photo.IsVideo ? "Video · Open File to play" : "Preview unavailable · Open File to view" : "";
            Metadata.Text = result.Metadata;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (previewCancellation == request)
            {
                PreviewHint.Text = "Preview unavailable";
                Metadata.Text = ex.Message;
            }
        }
        finally { if (previewCancellation == request) previewCancellation = null; }
    }

    private void ClearPreview()
    {
        previewCancellation?.Cancel();
        previewCancellation = null;
        if (inspectedList != null)
            inspectedList.SelectedItem = null;
        inspectedList = null;
        selected = null;
        PreviewImage.Source = null;
        PreviewHint.Text = "Select a photo or video to inspect";
        PreviewStatus.Text = HashText.Text = "";
        Metadata.Text = "File details will appear here.";
        PreviewExactActions.IsEnabled = false;
    }

    private void PauseScan(object sender, RoutedEventArgs e)
    {
        if (!scanning || scanner == null)
            return;
        if (scanner.Pause.IsSet)
        {
            scanner.Pause.Reset();
            PauseButton.Content = "Resume";
            ProgressText.Text = "Paused";
        }
        else
        {
            scanner.Pause.Set();
            PauseButton.Content = "Pause";
            ProgressText.Text = "Resuming…";
        }
    }

    private void StopScan(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        scanner?.Pause.Set();
        ProgressText.Text = removing ? "Canceling removal after the current Windows operation…" : "Stopping scan…";
    }

    private async void RemoveSelected(object sender, RoutedEventArgs e)
    {
        if (scanning || removing || viewSimilar)
            return;
        var candidates = groups.SelectMany(g => g.Photos).Where(p => p.Marked && !p.IsKeep).ToList();
        if (candidates.Count == 0)
            return;
        if (MessageBox.Show(this, $"Move {candidates.Count:N0} selected exact copies ({Format.Size(candidates.Sum(p => p.Size))}) to the Recycle Bin?\n\nThis includes selections hidden by search or media filters. Protected copies remain. Both files will be verified again.",
            "Review removal", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        removing = true;
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        UpdateCounts();
        try
        {
            var result = await RemovalRunner.RunAsync(candidates, cancellation.Token, ReportProgress);
            ApplyRemovalResult(result);
            ProgressText.Text = $"Moved {result.Removed.Count:N0} files to Recycle Bin · {result.Errors.Count:N0} errors" + (result.Canceled ? " · Canceled" : "");
            if (result.Errors.Count > 0 && !closeRequested)
                ShowText("Files not moved", string.Join("\n\n", result.Errors));
        }
        catch (Exception ex) { ProgressText.Text = "Removal stopped: " + ex.Message; }
        finally
        {
            removing = false;
            UpdateCounts();
            if (closeRequested)
                Close();
        }
    }

    private void ApplyRemovalResult(RemovalResult result)
    {
        Batch(() =>
        {
            foreach (var photo in result.Removed)
                photo.Group.RemoveRecycled(photo);
            foreach (var group in groups.Where(g => g.CopyCount < 2).ToList())
                groups.Remove(group);
        });
        totalPhotos = groups.Sum(g => (long)g.CopyCount);
        possibleBytes = groups.Sum(g => g.RecoverableBytes);
        if (result.Removed.Count > 0)
        {
            similarGroups.Clear();
            similarPositions.Clear();
            similarInvalidated = true;
        }
        ClearPreview();
        // A surviving group's remaining filenames may no longer match the search.
        ApplyFilters();
    }
}

