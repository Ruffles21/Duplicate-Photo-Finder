using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ruffles21.DuplicatePhotoFinder;

public sealed record VideoSample(TimeSpan Duration, IReadOnlyList<BitmapSource> Frames);
public sealed record VideoPreview(TimeSpan Duration, BitmapSource? Image);

/// <summary>Uses installed Windows codecs. Video sampling never opens an external player.</summary>
public static class VideoFrames
{
    static readonly SemaphoreSlim Worker = new(1);
    static readonly Lazy<Task<Dispatcher>> MediaDispatcher = new(CreateDispatcher);
    static readonly object CacheLock = new();
    static readonly Dictionary<string, (FileStamp Stamp, VideoPreview Preview)> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Queue<string> CacheOrder = new();
    readonly record struct FileStamp(long Length, long Modified, long Created);

    static FileStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
    }

    public static bool TryGetCached(string path, out VideoPreview preview)
    {
        preview = null!;
        FileStamp stamp;
        try
        {
            stamp = Stamp(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                preview = cached.Preview;
                return true;
            }
        }
        return false;
    }

    static void Remember(string path, FileStamp stamp, VideoPreview preview)
    {
        if (Stamp(path) != stamp)
            throw new IOException("This video changed while its preview was loading.");
        lock (CacheLock)
        {
            if (!Cache.ContainsKey(path))
            {
                while (Cache.Count >= 180)
                    Cache.Remove(CacheOrder.Dequeue());
                CacheOrder.Enqueue(path);
            }
            Cache[path] = (stamp, preview);
        }
    }

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
            CacheOrder.Clear();
        }
    }

    static Task<Dispatcher> CreateDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "ruffles_21 video sampling"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    public static async Task<VideoSample> SampleAsync(string path, CancellationToken cancellationToken = default)
    {
        await Worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stamp = Stamp(path);
            var dispatcher = await MediaDispatcher.Value.ConfigureAwait(false);
            var operation = dispatcher.InvokeAsync(() => SampleOnDispatcherAsync(path, cancellationToken));
            var sample = await (await operation.Task.ConfigureAwait(false)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Remember(path, stamp, new VideoPreview(sample.Duration, sample.Frames[0]));
            return sample;
        }
        finally { Worker.Release(); }
    }

    public static async Task<VideoPreview> GetPreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetCached(path, out var cached))
            return cached;
        await Worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(path, out cached))
                return cached;
            var stamp = Stamp(path);
            var dispatcher = await MediaDispatcher.Value.ConfigureAwait(false);
            var operation = dispatcher.InvokeAsync(() => SampleOnDispatcherAsync(path, cancellationToken, metadataOnly: true));
            var sample = await (await operation.Task.ConfigureAwait(false)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var preview = new VideoPreview(sample.Duration, null);
            Remember(path, stamp, preview);
            return preview;
        }
        finally { Worker.Release(); }
    }

    static async Task<VideoSample> SampleOnDispatcherAsync(string path, CancellationToken cancellationToken, bool metadataOnly = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new MediaPlayer { Volume = 0, IsMuted = true, ScrubbingEnabled = true };
        Exception? failure = null;
        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, args) =>
        {
            failure = new NotSupportedException("Windows could not decode this video. Install a compatible video codec or use Open File.", args.ErrorException);
            opened.TrySetException(failure);
        };

        try
        {
            token.ThrowIfCancellationRequested();
            player.Open(new Uri(Path.GetFullPath(path)));
            await opened.Task.WaitAsync(token);
            if (!player.HasVideo || player.NaturalVideoWidth <= 0 || player.NaturalVideoHeight <= 0 || !player.NaturalDuration.HasTimeSpan)
                throw new NotSupportedException("Windows did not expose seekable video frames for this file.");
            var duration = player.NaturalDuration.TimeSpan;
            if (duration <= TimeSpan.Zero)
                throw new NotSupportedException("This video's duration is unavailable.");
            if (metadataOnly)
                return new VideoSample(duration, Array.Empty<BitmapSource>());
            double scale = Math.Min(1d, 256d / Math.Max(player.NaturalVideoWidth, player.NaturalVideoHeight));
            int width = Math.Max(1, (int)Math.Round(player.NaturalVideoWidth * scale));
            int height = Math.Max(1, (int)Math.Round(player.NaturalVideoHeight * scale));
            var frames = new List<BitmapSource>(3);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawVideo(player, new Rect(0, 0, width, height));
            using var surface = new HwndSource(new HwndSourceParameters("ruffles_21 video decoder")
            {
                Width = width,
                Height = height,
                PositionX = -32000,
                PositionY = -32000,
                // An offscreen, nonactivating tool window lets WPF present seeked frames.
                // Without a composition surface RenderTargetBitmap repeats the first frame.
                WindowStyle = unchecked((int)0x90000000),
                ExtendedWindowStyle = 0x08000080
            });
            surface.RootVisual = visual;
            player.Play();
            await Task.Delay(200, token);
            player.Pause();

            foreach (double position in new[] { 0.2, 0.5, 0.8 })
            {
                token.ThrowIfCancellationRequested();
                player.Position = TimeSpan.FromTicks((long)(duration.Ticks * position));
                // MediaPlayer seeks asynchronously. Let the decoder present a frame before
                // sampling, and keep playback muted. A nondecoded/blank sample is rejected
                // by the similarity scanner instead of being treated as matching content.
                await Task.Delay(350, token);
                if (failure != null)
                    throw failure;
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                frames.Add(bitmap);
            }
            return new VideoSample(duration, frames.AsReadOnly());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotSupportedException("Video frame decoding timed out; this file was skipped.");
        }
        finally { player.Close(); }
    }
}
