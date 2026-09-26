using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ruffles21.DuplicatePhotoFinder;

public static class SafeRemoval
{
    public static void Remove(Photo photo, Action<string> recycle) =>
        Remove(photo, recycle, CancellationToken.None);

    public static void Remove(Photo photo, Action<string> recycle, CancellationToken token,
        Action<string>? progress = null) =>
        VerifyAndRemove(photo, (path, _, _) => recycle(path), token, progress);

    public static void Remove(Photo photo, CancellationToken token, Action<string>? progress = null) =>
        VerifyAndRemove(photo, Recycle.MoveVerified, token, progress);

    private static void VerifyAndRemove(Photo photo, Action<string, string, CancellationToken> recycle,
        CancellationToken token, Action<string>? progress)
    {
        token.ThrowIfCancellationRequested();
        if (photo.IsKeep || !photo.Marked || photo.Group == null)
            throw new InvalidOperationException("Only selected redundant exact copies can be recycled.");

        var keep = photo.Group.Keep;
        using var survivor = Scanner.Open(keep.Path);
        using var candidate = Scanner.Open(photo.Path, allowDelete: true);
        Scanner.Check(keep);
        Scanner.Check(photo);
        string identity = Scanner.Identity(candidate);
        if (Scanner.Identity(survivor) == identity)
            throw new IOException("These paths refer to the same file.");

        var clock = Stopwatch.StartNew();
        Action<long> Report(string stage) => bytes =>
        {
            token.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds < 150)
                return;
            clock.Restart();
            double percent = photo.Size == 0 ? 100 : bytes * 100d / photo.Size;
            progress?.Invoke($"{stage} · {photo.Name} · {percent:0}%");
        };

        progress?.Invoke("Checking protected copy · " + keep.Name);
        if (Scanner.Hash(survivor, token, Report("Checking protected copy")) != keep.Hash ||
            Scanner.Hash(candidate, token, Report("Checking selected copy")) != photo.Hash ||
            !Scanner.Equal(survivor, candidate, token, Report("Comparing every byte")))
            throw new IOException("A copy has changed. Nothing was removed; scan again.");

        token.ThrowIfCancellationRequested();
        progress?.Invoke("Moving to Recycle Bin · " + photo.Name);
        token.ThrowIfCancellationRequested();
        // Survivor remains locked against writes and deletion through the Shell operation.
        recycle(photo.Path, identity, token);
    }
}

public static class Recycle
{
    private const uint RecycleOnDelete = 0x00080000;
    private const uint EarlyFailure = 0x00100000;
    private const uint AddUndoRecord = 0x20000000;
    private const uint NoErrorUi = 0x00000400;
    private const uint Silent = 0x00000004;
    private const uint NoConfirmation = 0x00000010;
    private const uint NoConnectedElements = 0x00002000;
    private const uint TransferRecycleIfPossible = 0x00000080;
    private const int Abort = unchecked((int)0x80004004);

    public static void Move(string path)
    {
        using var candidate = Scanner.Open(path, allowDelete: true);
        MoveVerified(path, Scanner.Identity(candidate), CancellationToken.None);
    }

    public static void MoveVerified(string path, string expectedIdentity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw new IOException("Recycle Bin removal is supported only on local fixed drives.");

        EnsureIdentity(path, expectedIdentity);
        var operation = (IFileOperation)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"))!)!;
        IShellItem? item = null;
        var sink = new RecycleProgressSink(expectedIdentity, token);
        try
        {
            operation.SetOperationFlags(RecycleOnDelete | EarlyFailure | AddUndoRecord |
                NoErrorUi | Silent | NoConfirmation | NoConnectedElements);
            Guid iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            operation.DeleteItem(item, sink);
            bool aborted = false;
            ExecuteShellOperation(() =>
            {
                operation.PerformOperations();
                if (!sink.Recycled)
                    operation.GetAnyOperationsAborted(out aborted);
            }, () => sink.Recycled, token);
            if (sink.Recycled)
                return;
            throw new IOException(sink.Error ?? (aborted
                ? "Windows canceled the Recycle Bin operation."
                : "Windows did not confirm that the file reached the Recycle Bin."));
        }
        finally
        {
            GC.KeepAlive(sink);
            if (item != null)
                Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(operation);
        }
    }

    internal static void ExecuteShellOperation(Action operation, Func<bool> recycled, CancellationToken token)
    {
        try
        {
            operation();
        }
        catch (COMException) when (recycled()) { return; }
        catch (COMException ex) when (token.IsCancellationRequested)
        {
            // A canceled PreDeleteItem callback can surface as a COM HRESULT rather than an OCE.
            throw new OperationCanceledException("The Recycle Bin operation was canceled.", ex, token);
        }
        // Once Windows confirms a move, retain that success even if cancellation arrived afterward.
        if (!recycled())
            token.ThrowIfCancellationRequested();
    }

    private static void EnsureIdentity(string path, string expected)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The selected path is now a link. Scan again.");
        using var current = Scanner.Open(path, allowDelete: true);
        if (Scanner.Identity(current) != expected)
            throw new IOException("The selected file was replaced. Nothing was removed; scan again.");
    }

    private static string FileSystemPath(IShellItem item)
    {
        item.GetDisplayName(0x80058000, out IntPtr name); // SIGDN_FILESYSPATH
        try
        {
            return Marshal.PtrToStringUni(name) ?? throw new IOException("File path unavailable.");
        }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class RecycleProgressSink : IFileOperationProgressSink
    {
        private readonly string expectedIdentity;
        private readonly CancellationToken token;
        public bool Recycled
        {
            get; private set;
        }
        public string? Error
        {
            get; private set;
        }

        public RecycleProgressSink(string identity, CancellationToken token)
        {
            expectedIdentity = identity;
            this.token = token;
        }

        public int PreDeleteItem(uint flags, IShellItem item)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if ((flags & TransferRecycleIfPossible) == 0)
                    throw new IOException("Windows did not offer a Recycle Bin operation. This file was left untouched.");
                EnsureIdentity(FileSystemPath(item), expectedIdentity);
                return 0;
            }
            catch (Exception ex) { Error = ex.Message; return Abort; }
        }

        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? recycledItem)
        {
            Recycled = result >= 0 && recycledItem != null;
            if (!Recycled)
                Error = result < 0
                ? Marshal.GetExceptionForHR(result)?.Message ?? "Windows could not recycle this file."
                : "Windows did not report a Recycle Bin destination. Check the original folder and Recycle Bin.";
            return Recycled ? 0 : Abort;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string name) => Abort;
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? newItem) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem folder, string name) => Abort;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem folder, string name, int result, IShellItem? newItem) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem folder, string name) => Abort;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem folder, string name, int result, IShellItem? newItem) => 0;
        public int PreNewItem(uint flags, IShellItem folder, string name) => Abort;
        public int PostNewItem(uint flags, IShellItem folder, string name, string template, uint attributes, int result, IShellItem? newItem) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem);
        [PreserveSig] int PreNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem? newItem);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr window);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}

