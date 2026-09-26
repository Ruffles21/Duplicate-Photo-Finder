using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Ruffles21.DuplicatePhotoFinder;

public sealed class Scanner
{
    const int BufferSize = 128 * 1024;
    int active;

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".3g2", ".wmv", ".mpg", ".mpeg", ".mpe",
        ".mts", ".m2ts", ".webm", ".vob", ".flv", ".m2v", ".ts", ".mod", ".tod"
    };
    public static readonly HashSet<string> Extensions = new(new[]
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic",
        ".heif", ".avif", ".hif", ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".sr2",
        ".srf", ".raf", ".orf", ".rw2", ".pef", ".srw"
    }.Concat(VideoExtensions), StringComparer.OrdinalIgnoreCase);

    public ManualResetEventSlim Pause { get; } = new(true);
    public ConcurrentQueue<string> Warnings { get; } = new();
    public int PhotosFound
    {
        get; private set;
    }
    public int VideosFound
    {
        get; private set;
    }
    public int FilesExamined
    {
        get; private set;
    }
    public int UnsupportedFiles
    {
        get; private set;
    }
    public int FoldersVisited
    {
        get; private set;
    }
    public int MediaFiles => PhotosFound + VideosFound;

    public void Scan(IEnumerable<string> roots, Action<Group> found, Action<string> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(progress);
        RunExclusive(() =>
        {
            ResetStatistics();
            var reporter = new ProgressReporter(progress);
            var files = DiscoverCore(roots, reporter, token);
            ScanFilesCore(files, found, reporter, token);
        });
    }

    /// <summary>Discovers metadata once for exact scanning and separate similarity review.</summary>
    public IReadOnlyList<Photo> Discover(IEnumerable<string> roots, Action<string> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(progress);
        IReadOnlyList<Photo> result = Array.Empty<Photo>();
        RunExclusive(() =>
        {
            ResetStatistics();
            result = DiscoverCore(roots, new ProgressReporter(progress), token).AsReadOnly();
        });
        return result;
    }

    /// <summary>Finds exact groups from discovered files, preserving discovery statistics and warnings.</summary>
    public void ScanFiles(IEnumerable<Photo> files, Action<Group> found, Action<string> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(progress);
        RunExclusive(() =>
        {
            Gate(token);
            ScanFilesCore(files.ToList(), found, new ProgressReporter(progress), token);
        });
    }

    void RunExclusive(Action action)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("This scanner is already running.");
        try
        {
            action();
        }
        finally { Volatile.Write(ref active, 0); }
    }

    void ResetStatistics()
    {
        PhotosFound = VideosFound = FilesExamined = UnsupportedFiles = FoldersVisited = 0;
        Warnings.Clear();
    }

    List<Photo> DiscoverCore(IEnumerable<string> roots, ProgressReporter reporter, CancellationToken token)
    {
        Gate(token);
        reporter.Report("Starting scan… Looking for photos and videos in your folders.", force: true);
        var files = new List<Photo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            Gate(token);
            if (!Directory.Exists(root))
            {
                Warnings.Enqueue($"Folder unavailable: {root}");
                continue;
            }
            var pending = new Stack<string>();
            pending.Push(System.IO.Path.GetFullPath(root));
            while (pending.Count > 0)
            {
                Gate(token);
                var directory = pending.Pop();
                // Overlapping selections must not enumerate the same subtree again.
                if (!visited.Add(System.IO.Path.TrimEndingDirectorySeparator(directory)))
                    continue;
                try
                {
                    var folder = new DirectoryInfo(directory);
                    if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Warnings.Enqueue($"Skipped linked folder: {directory}");
                        continue;
                    }
                    FoldersVisited++;
                    reporter.Report($"Finding media… {PhotosFound:N0} photos · {VideosFound:N0} videos · {FoldersVisited:N0} folders");
                    // One enumeration supplies attributes and metadata without opening media contents.
                    foreach (var entry in folder.EnumerateFileSystemInfos())
                    {
                        Gate(token);
                        if (entry is DirectoryInfo)
                        {
                            pending.Push(entry.FullName);
                            continue;
                        }
                        if (!paths.Add(entry.FullName))
                            continue;
                        FilesExamined++;
                        reporter.Report($"Finding media… {PhotosFound:N0} photos · {VideosFound:N0} videos · {FilesExamined:N0} files checked");
                        if (!Extensions.Contains(entry.Extension))
                        {
                            UnsupportedFiles++;
                            continue;
                        }
                        try
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                Warnings.Enqueue($"Skipped linked/cloud-placeholder file: {entry.FullName}");
                                continue;
                            }
                            var photo = new Photo((FileInfo)entry);
                            files.Add(photo);
                            if (photo.IsVideo)
                                VideosFound++;
                            else
                                PhotosFound++;
                        }
                        catch (Exception ex) when (IsFileError(ex)) { Warn(entry.FullName, ex); }
                    }
                }
                catch (Exception ex) when (IsFileError(ex)) { Warn(directory, ex); }
            }
        }
        Gate(token);
        reporter.Report($"Found {PhotosFound:N0} photos and {VideosFound:N0} videos in {FoldersVisited:N0} folders.", force: true);
        return files;
    }

    void ScanFilesCore(List<Photo> files, Action<Group> found, ProgressReporter reporter, CancellationToken token)
    {
        Gate(token);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int done = 0, groupId = 0;
        reporter.Report($"Checking {files.Count:N0} media files for exact duplicates…", force: true);
        foreach (var sameSize in files.GroupBy(p => p.Size))
        {
            Gate(token);
            if (sameSize.Count() < 2)
            {
                done++;
                continue;
            }
            var hashes = new Dictionary<string, List<Photo>>(StringComparer.Ordinal);
            foreach (var photo in sameSize)
            {
                Gate(token);
                done++;
                try
                {
                    using var stream = Open(photo.Path);
                    Check(photo);
                    var identity = Identity(stream);
                    if (identities.Contains(identity))
                        continue;
                    photo.Hash = Hash(stream, token, bytes =>
                    {
                        Gate(token);
                        if (reporter.IsDue)
                            reporter.Report($"Checking media… {done:N0} / {files.Count:N0} · {photo.Name} · {Percent(bytes, photo.Size):0}%");
                    });
                    Check(photo);
                    // Another hard-link alias may still be readable if this attempt fails.
                    identities.Add(identity);
                    if (!hashes.TryGetValue(photo.Hash, out var matches))
                        hashes[photo.Hash] = matches = new List<Photo>();
                    matches.Add(photo);
                }
                catch (Exception ex) when (IsFileError(ex)) { Warn(photo.Path, ex); }
            }

            foreach (var sameHash in hashes.Values.Where(matches => matches.Count > 1))
            {
                Verify(sameHash, verified => found(new Group(++groupId, verified)), (photo, bytes) =>
                {
                    Gate(token);
                    if (reporter.IsDue)
                        reporter.Report($"Verifying exact copies… {photo.Name} · {Percent(bytes, photo.Size):0}%");
                }, token);
            }
        }
        Gate(token);
        reporter.Report($"Scan complete · {PhotosFound:N0} photos · {VideosFound:N0} videos · {groupId:N0} duplicate groups · {Warnings.Count:N0} warnings", force: true);
    }

    void Verify(List<Photo> candidates, Action<List<Photo>> found, Action<Photo, long> progress, CancellationToken token)
    {
        var remaining = new List<Photo>(candidates);
        while (remaining.Count > 1)
        {
            Gate(token);
            var reference = remaining[0];
            remaining.RemoveAt(0);
            List<Photo>? matches = null;
            try
            {
                // The reference stays locked against writes/deletion for the whole group.
                using var original = Open(reference.Path);
                Check(reference);
                matches = new List<Photo> { reference };
                var different = new List<Photo>();
                foreach (var photo in remaining)
                {
                    Gate(token);
                    try
                    {
                        using var copy = Open(photo.Path);
                        Check(photo);
                        bool equal = Equal(original, copy, token, bytes => progress(photo, bytes));
                        Check(photo);
                        if (equal)
                            matches.Add(photo);
                        else
                            different.Add(photo);
                    }
                    catch (Exception ex) when (IsFileError(ex)) { Warn(photo.Path, ex); }
                }
                Check(reference);
                remaining = different;
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // A missing reference must not hide duplicate groups among the other copies.
                Warn(reference.Path, ex);
                matches = null;
            }
            Gate(token);
            // Consumer errors must propagate, rather than being misreported as filesystem warnings.
            if (matches?.Count > 1)
                found(matches);
        }
    }

    static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException;
    static double Percent(long bytes, long length) => length == 0 ? 100 : bytes * 100d / length;
    void Warn(string path, Exception ex) => Warnings.Enqueue($"{path}: {ex.Message}");
    void Gate(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Pause.Wait(token);
    }

    public static FileStream Open(string path, bool allowDelete = false) => new(path, FileMode.Open, FileAccess.Read,
        allowDelete ? FileShare.Read | FileShare.Delete : FileShare.Read, BufferSize, FileOptions.SequentialScan);

    public static void Check(Photo photo)
    {
        var file = new FileInfo(photo.Path);
        if (!file.Exists || file.Length != photo.Size || file.LastWriteTimeUtc != photo.Modified ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("File changed since scanning; scan again.");
    }

    public static string Hash(Stream stream, CancellationToken token, Action<long>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = stream.Read(buffer, 0, BufferSize);
                if (count == 0)
                    break;
                hash.AppendData(buffer, 0, count);
                progress?.Invoke(stream.Position);
            }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static bool Equal(Stream first, Stream second, CancellationToken token, Action<long>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        long length = first.Length;
        if (length != second.Length)
            return false;
        first.Position = second.Position = 0;
        var left = ArrayPool<byte>.Shared.Rent(BufferSize);
        var right = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long compared = 0;
            while (compared < length)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(BufferSize, length - compared);
                int readLeft = first.ReadAtLeast(left.AsSpan(0, count), count, throwOnEndOfStream: false);
                int readRight = second.ReadAtLeast(right.AsSpan(0, count), count, throwOnEndOfStream: false);
                // Premature EOF is not equality, even when both streams happen to end early.
                if (readLeft != count || readRight != count || !left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count)))
                    return false;
                compared += count;
                progress?.Invoke(compared);
            }
            token.ThrowIfCancellationRequested();
            return first.Length == length && second.Length == length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(left);
            ArrayPool<byte>.Shared.Return(right);
        }
    }

    public static string Identity(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new IOException("Cannot verify file identity.");
        return $"{info.Volume}:{info.High}:{info.Low}";
    }

    sealed class ProgressReporter
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Action<string> progress;
        public bool IsDue => clock.ElapsedMilliseconds >= 150;
        public ProgressReporter(Action<string> progress) => this.progress = progress;

        public void Report(string text, bool force = false)
        {
            if (!force && !IsDue)
                return;
            clock.Restart();
            progress(text);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileInfoNative
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Modified;
        public uint Volume, SizeHigh, SizeLow, Links, High, Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoNative info);
}
