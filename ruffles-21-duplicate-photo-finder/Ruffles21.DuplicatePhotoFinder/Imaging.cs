using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ruffles21.DuplicatePhotoFinder;

/// <summary>Loads only realized thumbnails and releases their file handles after decoding.</summary>
public sealed class Thumbnail : Image
{
    const int ThumbnailSize = 360;
    const int CacheCapacity = 180;
    const long MaximumSourcePixels = 200_000_000;
    const long MaximumFullResolutionPixels = 64_000_000;
    static readonly SemaphoreSlim Workers = new(2);
    static readonly object CacheLock = new();
    static readonly Dictionary<CacheKey, BitmapSource> Cache = new();
    static readonly Queue<CacheKey> CacheOrder = new();
    static int cacheGeneration;

    readonly record struct CacheKey(string Path, long Length, long ModifiedTicks, long CreatedTicks);
    CancellationTokenSource? loading;

    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(Thumbnail), new PropertyMetadata(null, FilePathChanged));
    static readonly DependencyPropertyKey StatusTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(StatusText), typeof(string), typeof(Thumbnail), new PropertyMetadata(""));
    public static readonly DependencyProperty StatusTextProperty = StatusTextPropertyKey.DependencyProperty;

    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value);
    }
    public string StatusText => (string)GetValue(StatusTextProperty);

    public Thumbnail()
    {
        Stretch = Stretch.Uniform;
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => CancelLoad();
    }

    static void FilePathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var thumbnail = (Thumbnail)sender;
        thumbnail.CancelLoad();
        if (thumbnail.IsLoaded)
            thumbnail.Load();
    }

    void CancelLoad()
    {
        // The owning Load invocation disposes its token after its worker has finished.
        loading?.Cancel();
        loading = null;
        Source = null;
        SetValue(StatusTextPropertyKey, "");
    }

    async void Load()
    {
        CancelLoad();
        string? path = FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return;
        using var request = new CancellationTokenSource();
        loading = request;
        SetValue(StatusTextPropertyKey, "Loading preview…");
        try
        {
            var bitmap = await RunDecodeAsync(() => GetThumbnail(path, request.Token), request.Token);
            if (loading != request || !IsLoaded)
                return;
            Source = bitmap;
            SetValue(StatusTextPropertyKey, bitmap == null && IsVideo(path) ? "Video · Open File to play" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (IsPreviewFailure(exception))
        {
            if (loading == request)
                SetValue(StatusTextPropertyKey, "Preview unavailable · Open File to view");
        }
        finally
        {
            if (loading == request)
                loading = null;
        }
    }

    static BitmapSource? GetThumbnail(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsVideo(path))
            return VideoFrames.TryGetCached(path, out var video) ? video.Image : null;
        var info = new FileInfo(path);
        var key = new CacheKey(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
        int generation;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;
            generation = cacheGeneration;
        }

        var bitmap = Decode(path, ThumbnailSize);
        cancellationToken.ThrowIfCancellationRequested();
        // A concurrently replaced file must not be cached under its previous file stamp.
        info.Refresh();
        if (!info.Exists || info.Length != key.Length || info.LastWriteTimeUtc.Ticks != key.ModifiedTicks || info.CreationTimeUtc.Ticks != key.CreatedTicks)
            throw new IOException("This media file changed while its preview was loading.");

        lock (CacheLock)
        {
            if (generation == cacheGeneration && !Cache.ContainsKey(key))
            {
                while (Cache.Count >= CacheCapacity)
                    Cache.Remove(CacheOrder.Dequeue());
                Cache.Add(key, bitmap);
                CacheOrder.Enqueue(key);
            }
        }
        return bitmap;
    }

    /// <summary>Call when beginning a new scan to discard previews from previous scan results.</summary>
    public static void ClearCache()
    {
        VideoFrames.ClearCache();
        lock (CacheLock)
        {
            Cache.Clear();
            CacheOrder.Clear();
            cacheGeneration++;
        }
    }

    /// <summary>
    /// Decodes a frozen image with its EXIF orientation applied. A positive size bounds both
    /// dimensions without enlarging small images; zero requests the original resolution.
    /// </summary>
    public static BitmapSource Decode(string path, int width)
    {
        if (width < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (IsVideo(path))
            throw new NotSupportedException("Open this video in its default application to play it.");
        using var stream = Scanner.Open(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        ValidateDimensions(frame, width);
        int orientation = ReadOrientation(frame);
        int longestEdge = Math.Max(frame.PixelWidth, frame.PixelHeight);
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (width > 0 && width < longestEdge)
        {
            if (frame.PixelHeight > frame.PixelWidth)
                bitmap.DecodePixelHeight = width;
            else
                bitmap.DecodePixelWidth = width;
        }
        bitmap.EndInit();
        bitmap.Freeze();
        return Orient(bitmap, orientation);
    }

    public static Task<BitmapSource> DecodeAsync(string path, int width, CancellationToken cancellationToken = default) =>
        RunDecodeAsync(() => Decode(path, width), cancellationToken);

    public static async Task<(BitmapSource? Image, string Metadata)> PreviewAsync(Photo photo, CancellationToken cancellationToken = default)
    {
        var result = await RunDecodeAsync(() => Preview(photo), cancellationToken).ConfigureAwait(false);
        if (!IsVideo(photo.Path))
            return result;
        try
        {
            var video = await VideoFrames.GetPreviewAsync(photo.Path, cancellationToken).ConfigureAwait(false);
            return (video.Image, result.Metadata + "\n\nVideo duration\n" + video.Duration.ToString(@"hh\:mm\:ss"));
        }
        catch (Exception exception) when (IsPreviewFailure(exception))
        {
            return (result.Image, result.Metadata + "\n\nVideo metadata unavailable\n" + exception.Message);
        }
    }

    static async Task<T> RunDecodeAsync<T>(Func<T> decode, CancellationToken cancellationToken)
    {
        await Workers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // WIC cannot cancel a decoder already in native code. Limit concurrency and
            // cancel queued work; never publish a decoded result after cancellation.
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = decode();
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { Workers.Release(); }
    }

    static void ValidateDimensions(BitmapSource frame, int width)
    {
        long pixels = (long)frame.PixelWidth * frame.PixelHeight;
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || pixels > MaximumSourcePixels)
            throw new NotSupportedException("This image is too large for a safe in-app preview. Use Open File to view it.");
        int longestEdge = Math.Max(frame.PixelWidth, frame.PixelHeight);
        double scale = width > 0 ? Math.Min(1d, (double)width / longestEdge) : 1d;
        if (pixels * scale * scale > MaximumFullResolutionPixels)
            throw new NotSupportedException("This image is too large to load at full resolution in the app. Use the fitted preview or Open File.");
    }

    static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata metadata)
                return 1;
            foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "/eXIf/ifd/{ushort=274}" })
            {
                try
                {
                    object? value = metadata.GetQuery(query);
                    if (value == null)
                        continue;
                    int orientation = Convert.ToInt32(value);
                    if (orientation >= 1 && orientation <= 8)
                        return orientation;
                }
                catch (Exception exception) when (IsPreviewFailure(exception)) { }
            }
        }
        catch (Exception exception) when (IsPreviewFailure(exception)) { }
        return 1;
    }

    static BitmapSource Orient(BitmapSource bitmap, int orientation)
    {
        if (orientation == 1)
            return bitmap;
        // EXIF includes four mirrored orientations as well as the usual quarter turns.
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0),
            3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0),
            7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0),
            _ => Matrix.Identity
        };
        var oriented = new TransformedBitmap(bitmap, new MatrixTransform(matrix));
        oriented.Freeze();
        return oriented;
    }

    public static (BitmapSource? Image, string Metadata) Preview(Photo photo)
    {
        var text = new StringBuilder();
        void Add(string label, object? value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
                return;
            if (text.Length > 0)
                text.Append("\n\n");
            text.Append(label).Append('\n').Append(value);
        }
        Add("File Name", photo.Name);
        Add("File Size", Format.Size(photo.Size));
        Add("File Created", photo.Created.ToLocalTime().ToString("F"));
        Add("File Modified", photo.Modified.ToLocalTime().ToString("F"));
        Add("Folder Name", Path.GetDirectoryName(photo.Path));
        Add("File extension", Path.GetExtension(photo.Path));
        if (IsVideo(photo.Path))
        {
            Add("Video", "Use Open File to play this video in your default application.");
            return (null, text.ToString());
        }

        try
        {
            using (var stream = Scanner.Open(photo.Path))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                int orientation = ReadOrientation(frame);
                bool rotated = orientation >= 5;
                Add("Image Size", rotated ? $"{frame.PixelHeight} × {frame.PixelWidth}" : $"{frame.PixelWidth} × {frame.PixelHeight}");
                Add("Image DPI", $"{frame.DpiX:0.#} × {frame.DpiY:0.#}");
                Add("Bit Depth", frame.Format.BitsPerPixel);
                try
                {
                    if (frame.Metadata is BitmapMetadata metadata)
                    {
                        void Read(string label, Func<object?> value)
                        {
                            try
                            {
                                Add(label, value());
                            }
                            catch (Exception exception) when (IsPreviewFailure(exception)) { }
                        }
                        Read("Camera make", () => metadata.CameraManufacturer);
                        Read("Camera model", () => metadata.CameraModel);
                        Read("Date Taken / EXIF date", () => metadata.DateTaken);
                    }
                }
                catch (Exception exception) when (IsPreviewFailure(exception)) { }
                if (orientation != 1)
                    Add("EXIF Orientation", $"{orientation} · applied to preview");
            }
            return (Decode(photo.Path, 1400), text.ToString());
        }
        catch (Exception exception) when (IsPreviewFailure(exception))
        {
            string reason = exception switch
            {
                FileNotFoundException or DirectoryNotFoundException => "This file is no longer at its scanned location.",
                UnauthorizedAccessException => "Windows did not allow the app to read this file.",
                NotSupportedException => exception.Message,
                _ => "Open this media in its default application. RAW and HEIC previews require compatible Windows image codecs."
            };
            Add("Preview unavailable", reason);
            return (null, text.ToString());
        }
    }

    static bool IsVideo(string path) => Scanner.VideoExtensions.Contains(Path.GetExtension(path));

    static bool IsPreviewFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or NotSupportedException or ArgumentException or InvalidOperationException or FormatException
        or OverflowException or COMException;
}
