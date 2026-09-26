using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Ruffles21.DuplicatePhotoFinder;

public partial class MainWindow
{
    private async void ChooseFolders(object sender, RoutedEventArgs e)
    {
        if (scanning || removing)
            return;
        if (ScanExactChoice.IsChecked != true && ScanSimilarChoice.IsChecked != true)
        {
            ProgressText.Text = "Select Exact, Similar, or both before choosing folders.";
            return;
        }
        var picker = new OpenFolderDialog
        {
            Multiselect = true,
            Title = "Select folders to scan for photos and videos",
            InitialDirectory = settings.Folders.FirstOrDefault(Directory.Exists) ?? ""
        };
        if (picker.ShowDialog(this) == true)
            await ScanFoldersAsync(picker.FolderNames);
    }

    private async void ManageFolders(object sender, RoutedEventArgs e)
    {
        if (scanning || removing)
            return;
        var dialog = Dialog("Choose folders to scan", 700, 450);
        var dock = new DockPanel { Margin = new Thickness(24) };
        dialog.Content = dock;
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        var hint = new TextBlock
        {
            Text = "Add folders from different locations, then click Start scan.\nPhotos and videos in subfolders are included.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        DockPanel.SetDock(hint, Dock.Top);
        dock.Children.Add(hint);
        var folders = new ObservableCollection<string>(settings.Folders);
        var list = new ListBox { ItemsSource = folders };
        dock.Children.Add(list);
        buttons.Children.Add(Btn("Add folders", () =>
        {
            var picker = new OpenFolderDialog { Multiselect = true };
            if (picker.ShowDialog(dialog) == true)
                foreach (var folder in picker.FolderNames)
                    if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                        folders.Add(folder);
        }));
        buttons.Children.Add(Btn("Remove from list", () => { if (list.SelectedItem is string folder) folders.Remove(folder); }));
        var start = Btn("Start scan", () => dialog.DialogResult = true);
        start.IsEnabled = folders.Count > 0;
        folders.CollectionChanged += (_, _) => start.IsEnabled = folders.Count > 0;
        buttons.Children.Add(start);
        if (dialog.ShowDialog() == true)
            await ScanFoldersAsync(folders);
    }

    private void AssistantSettings(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        for (int i = 0; i < Rules.Items.Count; i++)
        {
            int index = i;
            var item = new MenuItem { Header = ((ComboBoxItem)Rules.Items[i]).Content, IsCheckable = true, IsChecked = Rules.SelectedIndex == i };
            item.Click += (_, _) => { Rules.SelectedIndex = index; SaveSettings(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var configure = new MenuItem { Header = "Configure preferred / backup folders…" };
        configure.Click += (_, _) => ConfigureAssistant();
        menu.Items.Add(configure);
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
    }

    private void ConfigureAssistant()
    {
        var window = Dialog("Selection Assistant", 650, 410);
        var stack = new StackPanel { Margin = new Thickness(24) };
        window.Content = stack;
        stack.Children.Add(new TextBlock
        {
            Text = "Your rule is applied when you click Auto Mark. Copies you manually protect stay protected. Oldest/newest uses file creation time.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        });
        stack.Children.Add(new TextBlock { Text = "Preferred folders (separate paths with semicolons)" });
        var preferred = new TextBox { Text = settings.Preferred, Margin = new Thickness(0, 6, 0, 16), Padding = new Thickness(8) };
        stack.Children.Add(preferred);
        stack.Children.Add(new TextBlock { Text = "Backup folders (separate paths with semicolons)" });
        var backup = new TextBox { Text = settings.Backups, Margin = new Thickness(0, 6, 0, 16), Padding = new Thickness(8) };
        stack.Children.Add(backup);
        stack.Children.Add(Btn("Save preferences", () =>
        {
            var paths = (preferred.Text + ";" + backup.Text).Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (paths.Any(p => !Path.IsPathFullyQualified(p.Trim())))
            {
                MessageBox.Show(window, "Use full folder paths, for example D:\\Master Photos.");
                return;
            }
            settings.Preferred = preferred.Text;
            settings.Backups = backup.Text;
            SaveSettings();
            window.Close();
        }));
        window.ShowDialog();
    }

    private void OpenFile(object sender, RoutedEventArgs e)
    {
        if (selected != null)
            Shell(selected.Path);
    }

    private void OpenFolder(object sender, RoutedEventArgs e)
    {
        if (selected == null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + selected.Path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open folder"); }
    }

    private void Shell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open file"); }
    }

    private void ZoomClick(object sender, RoutedEventArgs e) => ShowZoom();
    private void Enlarge(object sender, MouseButtonEventArgs e) => ShowZoom();
    private void ShowZoom()
    {
        if (selected != null && PreviewImage.Source != null)
            new PreviewWindow(selected.Path, PreviewImage.Source) { Owner = this }.Show();
    }

    private void ShowLog(object sender, RoutedEventArgs e)
    {
        if (scanner == null)
        {
            ShowText("Scan log", "No scan yet.");
            return;
        }
        string text = $"Folders: {string.Join("; ", settings.Folders)}\n{scanner.FoldersVisited:N0} folders visited\n{scanner.FilesExamined:N0} files examined\n{scanner.PhotosFound:N0} photos and {scanner.VideosFound:N0} videos found\n{scanner.UnsupportedFiles:N0} other files ignored\n{groups.Count:N0} exact groups, {similarGroups.Count:N0} similar groups\n\n";
        var warnings = scanner.Warnings.Concat(similarityScanner?.Warnings ?? new()).ToArray();
        ShowText("Scan log", text + (warnings.Length == 0 ? "No warnings." : string.Join("\n", warnings)));
    }

    private void ShowText(string title, string text)
    {
        var window = Dialog(title, 790, 550);
        window.Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(18) };
        window.Show();
    }

    private Window Dialog(string title, double width, double height) => new()
    {
        Owner = this,
        Title = title,
        Width = width,
        Height = height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Brushes.White,
        FontFamily = new FontFamily("Segoe UI")
    };

    internal static Button Btn(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private void ExportMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (string format in new[] { "CSV", "JSON" })
        {
            var item = new MenuItem { Header = "Export all " + (viewSimilar ? "similar" : "exact") + " results as " + format };
            item.Click += async (_, _) => await ExportAsync(format);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
    }

    private async Task ExportAsync(string format)
    {
        var picker = new SaveFileDialog
        {
            Filter = format == "CSV" ? "CSV report|*.csv" : "JSON report|*.json",
            FileName = viewSimilar ? "ruffles_21-similar-review" : "ruffles_21-exact-duplicates",
            AddExtension = true
        };
        if (picker.ShowDialog(this) != true)
            return;
        var rows = viewSimilar
            ? similarGroups.SelectMany(g => g.Photos.Select(p => new ReportRow(g.Id, p.Path, p.Size, p.Created, p.Modified, "SIMILAR · review only", p.Hash))).ToList()
            : ReportExporter.Snapshot(groups);
        try
        {
            await Task.Run(() => ReportExporter.Export(picker.FileName, rows, format));
            if (!closed)
                ProgressText.Text = "Report saved: " + picker.FileName;
        }
        catch (Exception ex) { if (!closed) MessageBox.Show(this, ex.Message, "Export failed"); }
    }
}
