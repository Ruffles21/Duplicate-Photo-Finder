using Ruffles21.DuplicatePhotoFinder;
using System.IO;
using System.Reflection;

internal static class CoreRegressionTests
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool result, string name)
        {
            if (!result)
                throw new Exception(name);
            Console.WriteLine("PASS: " + name);
            passed++;
        }
        void Throws<T>(Action action, string name) where T : Exception
        {
            try
            {
                action();
            }
            catch (T) { Check(true, name); return; }
            throw new Exception(name);
        }
        var folder = Path.Combine(root, "core-regressions");
        var nested = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nested);
        var longName = Path.Combine(folder, "long-original.jpg");
        var shortName = Path.Combine(nested, "a.jpg");
        File.WriteAllText(longName, "same byte content");
        File.Copy(longName, shortName);
        File.WriteAllText(Path.Combine(folder, "unique.mov"), "unique movie");
        File.WriteAllText(Path.Combine(folder, "ignore.txt"), "not media");
        var scanner = new Scanner();
        var files = scanner.Discover(new[] { nested, folder, folder + Path.DirectorySeparatorChar }, _ => { }, CancellationToken.None);
        Check(scanner.FoldersVisited == 2 && scanner.FilesExamined == 4, "Overlapping roots enumerate directories and files once");
        Check(files.Count == 3 && files.All(p => p.Hash.Length == 0) && scanner.PhotosFound == 2 && scanner.VideosFound == 1, "Discovery returns un-hashed photo/video metadata");
        var groups = new List<Group>();
        scanner.ScanFiles(files, groups.Add, _ => { }, CancellationToken.None);
        Check(groups.Count == 1 && groups[0].CopyCount == 2 && scanner.FoldersVisited == 2, "ScanFiles finds exact groups and preserves discovery statistics");
        var group = groups[0];
        var original = group.Photos.Single(p => p.Path == longName);
        var copy = group.Photos.Single(p => p.Path == shortName);
        group.Protect(original);
        group.ApplyRule("Keep shortest filename", "", "");
        group.AutoMark();
        Check(group.Keep == original && group.HasManualKeep && copy.Marked && !original.Marked, "Auto mark preserves an explicit manual KEEP");
        group.ApplyRule("Keep shortest filename", "", "", overrideManual: true);
        Check(group.Keep == copy && !group.HasManualKeep && !copy.Marked, "Explicit override can apply a new automatic KEEP rule");
        Check(group.RecoverableBytes == original.Size && original.DisplayType == "Photo" && original.Folder == folder, "Display metadata and recoverable bytes");
        Throws<NotSupportedException>(() => ((IList<Photo>)group.Photos).RemoveAt(0), "Group collection cannot be modified externally");
        Throws<InvalidOperationException>(() => group.RemoveRecycled(copy), "Protected KEEP cannot be removed from group");
        group.RemoveRecycled(original);
        original.Marked = true;
        Check(!original.CanMark && !original.Marked && group.CopyCount == 1 && group.Keep == copy, "Detached recycled copy cannot be marked again");
        Throws<ArgumentException>(() => new Group(2, new[] { copy, new Photo(longName) }), "A Photo cannot be reassigned to another exact group");
        Throws<ArgumentException>(() => new Group(2, new[] { new Photo(longName), new Photo(longName) }), "Duplicate paths cannot form a group");
        Check(!Group.IsWithin(Path.Combine(folder, "..", "other.jpg"), folder), "Folder preference normalizes parent traversal");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Throws<OperationCanceledException>(() => Scanner.Hash(new MemoryStream(), canceled.Token), "Hash checks cancellation on empty input");
        Throws<OperationCanceledException>(() => Scanner.Equal(new MemoryStream(), new MemoryStream(), canceled.Token), "Equality checks cancellation on empty input");
        using var hashCancel = new CancellationTokenSource();
        Throws<OperationCanceledException>(() => Scanner.Hash(new MemoryStream(new byte[] { 1 }), hashCancel.Token, _ => hashCancel.Cancel()), "Hash honors cancellation on its final progress callback");
        using var equalCancel = new CancellationTokenSource();
        Throws<OperationCanceledException>(() => Scanner.Equal(new MemoryStream(new byte[] { 1 }), new MemoryStream(new byte[] { 1 }), equalCancel.Token, _ => equalCancel.Cancel()), "Equality honors cancellation on its final progress callback");
        byte[] bytes = Enumerable.Range(0, 10000).Select(i => (byte)i).ToArray();
        Check(Scanner.Equal(new ShortStream(bytes), new MemoryStream(bytes), CancellationToken.None), "Equality correctly fills short reads");
        Check(!Scanner.Equal(new TruncatedStream(bytes), new TruncatedStream(bytes), CancellationToken.None), "Premature EOF in both streams is rejected");
        Throws<IOException>(() => new Scanner().Scan(new[] { folder }, _ => throw new IOException("consumer failure"), _ => { }, CancellationToken.None), "Found callback failures propagate");
        var paused = new Scanner();
        paused.Pause.Reset();
        using var pauseCancel = new CancellationTokenSource();
        var task = Task.Run(() => paused.Discover(new[] { folder }, _ => { }, pauseCancel.Token));
        pauseCancel.Cancel();
        Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult(), "A paused scanner responds to cancellation");
        // Verify the surviving duplicates are still found after a previously hashed reference disappears.
        var first = new Photo(longName);
        var second = new Photo(shortName);
        var thirdPath = Path.Combine(nested, "third.jpg");
        File.Copy(shortName, thirdPath);
        var third = new Photo(thirdPath);
        File.Move(longName, longName + ".missing");
        var survivors = new List<List<Photo>>();
        typeof(Scanner).GetMethod("Verify", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(new Scanner(), new object[] { new List<Photo> { first, second, third }, (Action<List<Photo>>)survivors.Add, (Action<Photo, long>)((_, _) => { }), CancellationToken.None });
        Check(survivors.Count == 1 && survivors[0].Count == 2, "Missing verification reference does not hide surviving duplicate copies");
        return passed;
    }

    sealed class ShortStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(7, buffer.Length)]);
    }
    sealed class TruncatedStream(byte[] data) : MemoryStream(data)
    {
        public override long Length => base.Length + 100;
    }
}
