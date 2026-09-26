using Ruffles21.DuplicatePhotoFinder;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class MediaRegressionTests
{
    const int Width = 96;
    const int Height = 64;

    // Run on the WPF dispatcher with an Application already created. The caller must
    // await this method or pump the dispatcher while it runs.
    public static async Task<int> RunAsync(string root)
    {
        Application.Current.Dispatcher.VerifyAccess();
        var checks = new Checks();
        string folder = Path.Combine(root, "media-regressions");
        Directory.CreateDirectory(folder);
        byte[] pixels = ImagePixels();
        var source = Bitmap(pixels);
        string original = SaveImage(Path.Combine(folder, "original.png"), source);

        var decoded = Thumbnail.Decode(original, 256);
        checks.Check(decoded.PixelWidth == Width && decoded.PixelHeight == Height && decoded.IsFrozen,
            "Small image previews remain at native size and are safe to share across threads");

        // Map all four output corners back to the source, including mirrored EXIF cases.
        int[][] cornerOrder =
        {
            new[] { 0, 1, 2, 3 }, new[] { 1, 0, 3, 2 },
            new[] { 2, 3, 0, 1 }, new[] { 3, 2, 1, 0 },
            new[] { 0, 3, 2, 1 }, new[] { 3, 0, 1, 2 },
            new[] { 2, 1, 0, 3 }, new[] { 1, 2, 3, 0 }
        };
        for (int orientation = 1; orientation <= 8; orientation++)
        {
            var metadata = new BitmapMetadata("tiff");
            metadata.SetQuery("/ifd/{ushort=274}", (ushort)orientation);
            string path = SaveImage(Path.Combine(folder, $"orientation-{orientation}.tif"), source, metadata);
            var oriented = Thumbnail.Decode(path, 256);
            byte[] output = Pixels(oriented);
            bool correct = oriented.PixelWidth == (orientation >= 5 ? Height : Width) &&
                oriented.PixelHeight == (orientation >= 5 ? Width : Height);
            for (int corner = 0; corner < 4; corner++)
            {
                int expectedOffset = CornerOffset(cornerOrder[orientation - 1][corner], Width, Height);
                int actualOffset = CornerOffset(corner, oriented.PixelWidth, oriented.PixelHeight);
                correct &= output.AsSpan(actualOffset, 3).SequenceEqual(pixels.AsSpan(expectedOffset, 3));
            }
            checks.Check(correct, $"EXIF orientation {orientation} applies correct dimensions and all four corners");
        }

        string rotatedPath = Path.Combine(folder, "orientation-6.tif");
        var small = Thumbnail.Decode(rotatedPath, 48);
        checks.Check(small.PixelWidth == 32 && small.PixelHeight == 48,
            "Portrait thumbnails bound the longest edge after EXIF rotation");
        var preview = await Thumbnail.PreviewAsync(new Photo(rotatedPath));
        checks.Check(preview.Image is { } previewImage && previewImage.PixelWidth == Height && previewImage.PixelHeight == Width &&
            preview.Metadata.Contains($"{Height} × {Width}"), "Preview metadata reports the oriented dimensions");

        string recompressed = SaveImage(Path.Combine(folder, "recompressed.jpg"), source);
        byte[] unrelatedPixels = new byte[pixels.Length];
        new Random(27).NextBytes(unrelatedPixels);
        string unrelated = SaveImage(Path.Combine(folder, "unrelated.png"), Bitmap(unrelatedPixels));
        var photos = new[] { new Photo(original), new Photo(recompressed), new Photo(unrelated) };
        var results = new Dictionary<int, SimilarityGroup>();
        var similarity = new SimilarityScanner();
        await similarity.ScanAsync(photos, 8, group => results[group.Id] = group, _ => { }, CancellationToken.None);
        checks.Check(results.Count == 1 && results.Values.Single().Photos.Count == 2 &&
            results.Values.Single().Photos.Any(p => p.Path == recompressed),
            "Recompressed content matches while unrelated image content stays out");
        checks.Check(photos.All(photo => photo.Group == null && !photo.Marked && !photo.CanMark),
            "Similarity review neither assigns exact groups nor enables removal selection");
        checks.Check(results.Values.All(group => group.Matches.All(match =>
            SimilarityScanner.HammingDistance(
                SimilarityScanner.Fingerprint(Thumbnail.Decode(group.Representative.Path, 256)).Hash,
                SimilarityScanner.Fingerprint(Thumbnail.Decode(match.Photo.Path, 256)).Hash) <= 8)),
            "Every similar result matches its fixed representative within the requested threshold");

        var exact = new List<Group>();
        new Scanner().ScanFiles(new[] { new Photo(original), new Photo(recompressed) },
            exact.Add, _ => { }, CancellationToken.None);
        checks.Check(exact.Count == 0, "JPEG recompression is not treated as an exact duplicate");

        string blank = SaveImage(Path.Combine(folder, "blank.png"), Bitmap(Enumerable.Repeat((byte)128, pixels.Length).ToArray()));
        var blankResults = new Dictionary<int, SimilarityGroup>();
        var blankScanner = new SimilarityScanner();
        await blankScanner.ScanAsync(new[] { new Photo(blank) }, 8, group => blankResults[group.Id] = group, _ => { }, CancellationToken.None);
        checks.Check(blankResults.Count == 0 && blankScanner.Skipped == 1 && blankScanner.Warnings.Count == 1,
            "Blank undecodable-looking content is skipped instead of forming a misleading similar group");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await checks.CanceledAsync(() => Thumbnail.DecodeAsync(original, 256, canceled.Token),
            "An already-canceled image decode does not enter the decoder");
        await checks.CanceledAsync(() => Thumbnail.PreviewAsync(new Photo(original), canceled.Token),
            "An already-canceled preview does not load metadata or pixels");
        await checks.CanceledAsync(() => new SimilarityScanner().ScanAsync(photos, 8, _ => { }, _ => { }, canceled.Token),
            "Similarity scanning honors an already-canceled request");
        await checks.CanceledAsync(() => VideoFrames.SampleAsync(Path.Combine(folder, "not-opened.avi"), canceled.Token),
            "An already-canceled video sample does not open a native player");

        string cachedPath = SaveImage(Path.Combine(folder, "cached.bmp"), source);
        var thumbnail = new Thumbnail { Width = Width, Height = Height, FilePath = cachedPath };
        var window = new Window
        {
            Content = thumbnail,
            Width = 160,
            Height = 120,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000
        };
        try
        {
            Thumbnail.ClearCache();
            window.Show();
            await UntilAsync(() => thumbnail.Source != null, "The initial thumbnail did not load.");
            var first = thumbnail.Source;
            thumbnail.FilePath = null;
            thumbnail.FilePath = cachedPath;
            await UntilAsync(() => thumbnail.Source != null, "The cached thumbnail did not load.");
            checks.Check(ReferenceEquals(first, thumbnail.Source), "Unchanged media reuses its cached thumbnail");

            DateTime oldTime = File.GetLastWriteTimeUtc(cachedPath);
            var inverted = Bitmap(pixels.Select(value => (byte)(255 - value)).ToArray());
            SaveImage(cachedPath, inverted);
            File.SetLastWriteTimeUtc(cachedPath, oldTime.AddSeconds(5));
            thumbnail.FilePath = null;
            thumbnail.FilePath = cachedPath;
            await UntilAsync(() => thumbnail.Source != null, "The replaced thumbnail did not load.");
            var replaced = thumbnail.Source;
            checks.Check(!ReferenceEquals(first, replaced) && Pixels((BitmapSource)replaced!).SequenceEqual(Pixels(inverted)),
                "A replaced file invalidates its cached image even when the path and size stay the same");

            Thumbnail.ClearCache();
            thumbnail.FilePath = null;
            thumbnail.FilePath = cachedPath;
            await UntilAsync(() => thumbnail.Source != null, "The thumbnail did not reload after clearing its cache.");
            checks.Check(!ReferenceEquals(replaced, thumbnail.Source), "Starting a new scan can discard cached preview objects");

            thumbnail.FilePath = original;
            thumbnail.FilePath = cachedPath;
            await UntilAsync(() => thumbnail.Source != null, "The final thumbnail request did not load.");
            checks.Check(Pixels((BitmapSource)thumbnail.Source!).SequenceEqual(Pixels(inverted)),
                "Rapid card recycling cannot publish the previous file's preview");

            string corrupt = Path.Combine(folder, "corrupt.png");
            File.WriteAllText(corrupt, "This is intentionally not an image.");
            thumbnail.FilePath = corrupt;
            await UntilAsync(() => thumbnail.StatusText.Contains("unavailable"), "A corrupt thumbnail did not show an unavailable state.");
            checks.Check(thumbnail.Source == null, "A corrupt replacement clears old pixels and reports preview unavailable");
        }
        finally { window.Close(); }
        await UntilAsync(() => !thumbnail.IsLoaded, "The thumbnail did not unload.");
        checks.Check(thumbnail.Source == null, "Unloading a thumbnail releases its displayed source");
        return checks.Count;
    }

    // This exercises the installed Windows AVI decoder and composition surface.
    // Keep it optional in environments without desktop media components.
    public static async Task<int> RunVideoAsync(string root)
    {
        var checks = new Checks();
        string folder = Path.Combine(root, "video-regressions");
        Directory.CreateDirectory(folder);
        string video = Path.Combine(folder, "generated.avi");
        WriteAvi(video);
        var sample = await VideoFrames.SampleAsync(video);
        checks.Check(sample.Frames.Count == 3 && Math.Abs(sample.Duration.TotalSeconds - 4) < 0.05,
            "Native AVI sampling returns three frames and the generated clip's duration");
        int[] expectedFrames = { 8, 20, 32 };
        var observed = new List<ulong>();
        for (int i = 0; i < expectedFrames.Length; i++)
        {
            var expected = SimilarityScanner.Fingerprint(Bitmap(VideoPixels(expectedFrames[i])));
            var actual = SimilarityScanner.Fingerprint(sample.Frames[i]);
            observed.Add(actual.Hash);
            checks.Check(actual.Hash == expected.Hash && actual.Contrast >= 3 && sample.Frames[i].IsFrozen &&
                sample.Frames[i].PixelWidth == Width && sample.Frames[i].PixelHeight == Height,
                $"Video sample {i + 1} contains the expected frame at {expectedFrames[i] / 10d:0.0} seconds");
        }
        checks.Check(observed.Distinct().Count() == 3, "Video sampling seeks to distinct positions instead of repeating the first frame");
        checks.Check(VideoFrames.TryGetCached(video, out var cached) && cached.Duration == sample.Duration &&
            ReferenceEquals(cached.Image, sample.Frames[0]), "Sampled videos reuse a cached frame and duration for preview");
        var preview = await Thumbnail.PreviewAsync(new Photo(video));
        checks.Check(preview.Image != null && preview.Metadata.Contains("Video duration") && preview.Metadata.Contains("00:00:04"),
            "Video inspection displays a cached frame with its duration");
        File.SetLastWriteTimeUtc(video, File.GetLastWriteTimeUtc(video).AddSeconds(5));
        checks.Check(!VideoFrames.TryGetCached(video, out _), "Changed video metadata invalidates its previous frame cache");
        var metadata = await VideoFrames.GetPreviewAsync(video);
        checks.Check(metadata.Image == null && Math.Abs(metadata.Duration.TotalSeconds - 4) < 0.05,
            "A video without cached frames still exposes duration without requiring a full sample");
        Thumbnail.ClearCache();
        checks.Check(!VideoFrames.TryGetCached(video, out _), "Clearing scan previews also clears cached video metadata");
        string remuxed = Path.Combine(folder, "same-frames-different-container.avi");
        File.Copy(video, remuxed);
        using (var stream = new FileStream(remuxed, FileMode.Open, FileAccess.ReadWrite))
        using (var writer = new BinaryWriter(stream, Encoding.ASCII))
        {
            stream.Position = stream.Length;
            writer.Write(Chunk("JUNK", Encoding.ASCII.GetBytes("test")));
            stream.Position = 4;
            writer.Write(checked((uint)(stream.Length - 8)));
        }
        var possible = new Dictionary<int, SimilarityGroup>();
        await new SimilarityScanner().ScanAsync(new[] { new Photo(video), new Photo(remuxed) }, 8,
            group => possible[group.Id] = group, _ => { }, CancellationToken.None);
        var exact = new List<Group>();
        new Scanner().Scan(new[] { folder }, exact.Add, _ => { }, CancellationToken.None);
        checks.Check(possible.Count == 1 && possible.Values.Single().IsVideo && possible.Values.Single().Photos.Count == 2 && exact.Count == 0,
            "Videos with the same frames but different container bytes form a similar group without becoming exact duplicates");
        return checks.Count;
    }

    static async Task UntilAsync(Func<bool> condition, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
                throw new Exception(failure);
            await Task.Delay(15);
        }
    }

    static int CornerOffset(int corner, int width, int height) => corner switch
    {
        0 => 0,
        1 => (width - 1) * 3,
        2 => (width * height - 1) * 3,
        _ => (height - 1) * width * 3
    };

    static byte[] ImagePixels()
    {
        var pixels = new byte[Width * Height * 3];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * Width + x) * 3;
                pixels[offset] = (byte)((x * 7 + y * 11) % 255);
                pixels[offset + 1] = (byte)(y * 4);
                pixels[offset + 2] = (byte)(x * 2);
            }
        return pixels;
    }

    static byte[] VideoPixels(int frame)
    {
        var pixels = new byte[Width * Height * 3];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * Width + x) * 3;
                pixels[offset] = (byte)((x * 3 + y * 7) % 256);
                pixels[offset + 1] = (byte)(y * 4);
                pixels[offset + 2] = (byte)((x * 2 + frame * 3) % 256);
                if ((x - frame * 2 % 70) * (x - frame * 2 % 70) + (y - 24) * (y - 24) < 120)
                {
                    pixels[offset] = 40;
                    pixels[offset + 1] = 80;
                    pixels[offset + 2] = 235;
                }
            }
        return pixels;
    }

    static BitmapSource Bitmap(byte[] pixels)
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr24, null, pixels, Width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 3];
        converted.CopyPixels(pixels, converted.PixelWidth * 3, 0);
        return pixels;
    }

    static string SaveImage(string path, BitmapSource source, BitmapMetadata? metadata = null)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".tif" => new TiffBitmapEncoder(),
            ".jpg" => new JpegBitmapEncoder { QualityLevel = 75 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    static byte[] Data(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    static void FourCc(BinaryWriter writer, string value) => writer.Write(Encoding.ASCII.GetBytes(value));

    static byte[] Chunk(string tag, byte[] data) => Data(writer =>
    {
        FourCc(writer, tag);
        writer.Write((uint)data.Length);
        writer.Write(data);
        if ((data.Length & 1) != 0)
            writer.Write((byte)0);
    });

    static void WriteAvi(string path)
    {
        const int frameCount = 40, fps = 10, frameSize = Width * Height * 3;
        byte[] mainHeader = Data(writer =>
        {
            foreach (uint value in new uint[] { 100000, frameSize * fps, 0, 16, frameCount, 0, 1, frameSize, Width, Height, 0, 0, 0, 0 })
                writer.Write(value);
        });
        byte[] streamHeader = Data(writer =>
        {
            FourCc(writer, "vids");
            FourCc(writer, "DIB ");
            writer.Write(0u);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            foreach (uint value in new uint[] { 0, 1, fps, 0, frameCount, frameSize, uint.MaxValue, 0 })
                writer.Write(value);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((short)Width);
            writer.Write((short)Height);
        });
        byte[] format = Data(writer =>
        {
            writer.Write(40u);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write((ushort)1);
            writer.Write((ushort)24);
            writer.Write(0u);
            writer.Write(frameSize);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0u);
            writer.Write(0u);
        });
        byte[] header = Chunk("LIST", Data(writer =>
        {
            FourCc(writer, "hdrl");
            writer.Write(Chunk("avih", mainHeader));
            writer.Write(Chunk("LIST", Data(stream =>
            {
                FourCc(stream, "strl");
                stream.Write(Chunk("strh", streamHeader));
                stream.Write(Chunk("strf", format));
            })));
        }));
        var offsets = new List<uint>();
        byte[] movie = Chunk("LIST", Data(writer =>
        {
            FourCc(writer, "movi");
            uint offset = 4;
            for (int frame = 0; frame < frameCount; frame++)
            {
                byte[] topDown = VideoPixels(frame);
                byte[] bottomUp = new byte[frameSize];
                for (int y = 0; y < Height; y++)
                    Buffer.BlockCopy(topDown, y * Width * 3, bottomUp, (Height - 1 - y) * Width * 3, Width * 3);
                byte[] chunk = Chunk("00db", bottomUp);
                offsets.Add(offset);
                writer.Write(chunk);
                offset += (uint)chunk.Length;
            }
        }));
        byte[] index = Chunk("idx1", Data(writer =>
        {
            foreach (uint offset in offsets)
            {
                FourCc(writer, "00db");
                writer.Write(16u);
                writer.Write(offset);
                writer.Write(frameSize);
            }
        }));
        File.WriteAllBytes(path, Chunk("RIFF", Data(writer =>
        {
            FourCc(writer, "AVI ");
            writer.Write(header);
            writer.Write(movie);
            writer.Write(index);
        })));
    }

    sealed class Checks
    {
        public int Count
        {
            get; private set;
        }
        public void Check(bool condition, string label)
        {
            if (!condition)
                throw new Exception("FAILED: " + label);
            Console.WriteLine("PASS: " + label);
            Count++;
        }
        public async Task CanceledAsync(Func<Task> action, string label)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException) { Check(true, label); return; }
            throw new Exception("FAILED: " + label);
        }
    }
}
