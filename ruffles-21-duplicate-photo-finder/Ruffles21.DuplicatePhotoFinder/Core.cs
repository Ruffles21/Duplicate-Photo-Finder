using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace Ruffles21.DuplicatePhotoFinder;

public sealed class Photo : INotifyPropertyChanged
{
    bool marked;

    public string Path
    {
        get;
    }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public bool IsVideo => Scanner.VideoExtensions.Contains(System.IO.Path.GetExtension(Path));
    public string DisplayType => IsVideo ? "Video" : "Photo";
    public long Size
    {
        get;
    }
    public DateTime Created
    {
        get;
    }
    public DateTime Modified
    {
        get;
    }
    public string Hash { get; internal set; } = "";
    public Group Group { get; internal set; } = null!;
    public bool IsKeep => Group?.Keep == this;
    public bool CanMark => Group != null && !IsKeep;

    public bool Marked
    {
        get => marked;
        set
        {
            bool next = value && CanMark;
            if (marked == next)
                return;
            marked = next;
            Refresh();
        }
    }

    public string Status => IsKeep ? "KEEP · protected" : Marked ? "REMOVE" : "Unselected";
    public string BorderColor => IsKeep ? "#26956B" : Marked ? "#D34C57" : "#DCE2E8";
    public string Detail => $"{Modified.ToLocalTime():g}  ·  {Format.Size(Size)}";

    public Photo(string path) : this(new FileInfo(System.IO.Path.GetFullPath(path))) { }

    internal Photo(FileInfo file)
    {
        Path = file.FullName;
        Size = file.Length;
        Created = file.CreationTimeUtc;
        Modified = file.LastWriteTimeUtc;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed class Group : INotifyPropertyChanged
{
    readonly ObservableCollection<Photo> photos;

    public int Id
    {
        get;
    }
    public IReadOnlyList<Photo> Photos
    {
        get;
    }
    public Photo Keep
    {
        get; private set;
    }
    public bool HasManualKeep
    {
        get; private set;
    }
    public int CopyCount => photos.Count;
    public long RecoverableBytes => photos.Where(p => p != Keep).Sum(p => p.Size);
    public string Title => $"Duplicate Group {Id} · {CopyCount} files · Verified exact";
    public event PropertyChangedEventHandler? PropertyChanged;

    public Group(int id, IEnumerable<Photo> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        this.photos = new ObservableCollection<Photo>(photos);
        if (this.photos.Count < 2)
            throw new ArgumentException("A group needs two copies.", nameof(photos));
        if (this.photos.Any(p => p == null || p.Group != null))
            throw new ArgumentException("Each copy must be a file that does not already belong to a group.", nameof(photos));
        if (this.photos.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != this.photos.Count)
            throw new ArgumentException("A group cannot contain the same file path twice.", nameof(photos));

        Id = id;
        Photos = new ReadOnlyObservableCollection<Photo>(this.photos);
        Keep = this.photos[0];
        foreach (var photo in this.photos)
            photo.Group = this;
    }

    public void Protect(Photo photo, bool manual = true)
    {
        if (!photos.Contains(photo))
            throw new ArgumentException("The protected file must belong to this group.", nameof(photo));
        var previous = Keep;
        Keep = photo;
        HasManualKeep = manual;
        photo.Marked = false;
        previous.Refresh();
        if (previous != photo)
            photo.Refresh();
    }

    public void AutoMark()
    {
        foreach (var photo in photos)
            photo.Marked = !photo.IsKeep;
    }

    public void Clear()
    {
        foreach (var photo in photos)
            photo.Marked = false;
    }

    public void RemoveRecycled(Photo photo)
    {
        if (photo == Keep)
            throw new InvalidOperationException("The protected copy cannot be removed from its group.");
        if (!photos.Remove(photo))
            throw new ArgumentException("The file does not belong to this group.", nameof(photo));
        photo.Group = null!;
        photo.Marked = false;
        photo.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void ApplyRule(string rule, string preferred, string backups, bool overrideManual = false)
    {
        if (HasManualKeep && !overrideManual)
            return;
        // Parse folder preferences once per group, rather than once per candidate.
        var preferredFolders = Folders(preferred).ToArray();
        var backupFolders = Folders(backups).ToArray();
        Func<Photo, object> key = rule switch
        {
            "Keep shortest filename" => p => p.Name.Length,
            "Keep oldest file" => p => p.Created,
            "Keep newest file" => p => -p.Created.Ticks,
            "Keep files in preferred folder" => p => preferredFolders.Any(f => IsWithin(p.Path, f)) ? 0 : 1,
            "Keep files outside backup folders" => p => backupFolders.Any(f => IsWithin(p.Path, f)) ? 1 : 0,
            _ => p => p.Path.Length
        };
        Protect(photos.OrderBy(key).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase).First(), manual: false);
    }

    static IEnumerable<string> Folders(string text)
    {
        foreach (var value in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var folder = value.Trim();
            if (!System.IO.Path.IsPathFullyQualified(folder))
                continue;
            string normalized;
            try
            {
                normalized = System.IO.Path.GetFullPath(folder);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            yield return normalized;
        }
    }

    public static bool IsWithin(string path, string folder)
    {
        var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        if (!System.IO.Path.EndsInDirectorySeparator(parent))
            parent += System.IO.Path.DirectorySeparatorChar;
        return System.IO.Path.GetFullPath(path).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }
}

public static class Format
{
    public static string Size(long n) => n >= 1073741824 ? $"{n / 1073741824d:0.00} GB"
        : n >= 1048576 ? $"{n / 1048576d:0.0} MB"
        : n >= 1024 ? $"{n / 1024d:0.0} KB" : $"{n} B";
}
