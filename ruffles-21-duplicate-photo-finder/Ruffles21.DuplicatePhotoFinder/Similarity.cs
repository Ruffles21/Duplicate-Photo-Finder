using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ruffles21.DuplicatePhotoFinder;

public sealed record SimilarityMatch(Photo Photo, int Distance);

/// <summary>A review-only group. Each member independently matches the fixed representative.</summary>
public sealed class SimilarityGroup
{
    public int Id
    {
        get;
    }
    public Photo Representative
    {
        get;
    }
    public IReadOnlyList<SimilarityMatch> Matches
    {
        get;
    }
    public IReadOnlyList<Photo> Photos
    {
        get;
    }
    public bool IsVideo
    {
        get;
    }
    public int MaximumDistance => Matches.Max(match => match.Distance);
    public string Title => $"Similar {(IsVideo ? "videos" : "photos")} · Group {Id}";
    public string Detail => IsVideo
        ? $"{Photos.Count} videos · Similar sampled frames and duration · Review each clip"
        : $"{Photos.Count} photos · Visually similar to the first photo · Review each copy";

    public SimilarityGroup(int id, Photo representative, IEnumerable<SimilarityMatch> matches, bool isVideo)
    {
        Id = id;
        Representative = representative;
        Matches = Array.AsReadOnly(matches.ToArray());
        Photos = Array.AsReadOnly(new[] { representative }.Concat(Matches.Select(match => match.Photo)).ToArray());
        IsVideo = isVideo;
        if (Matches.Count == 0)
            throw new ArgumentException("A similarity group requires a matching file.", nameof(matches));
    }
}

public readonly record struct VisualFingerprint(ulong Hash, ulong DifferenceHash, double AspectRatio,
    double MeanRed, double MeanGreen, double MeanBlue, double Contrast);

public sealed class SimilarityScanner
{
    public ConcurrentQueue<string> Warnings { get; } = new();
    public int Processed
    {
        get; private set;
    }
    public int Skipped
    {
        get; private set;
    }
    public int PhotosCompared
    {
        get; private set;
    }
    public int VideosCompared
    {
        get; private set;
    }
    public int GroupsFound
    {
        get; private set;
    }
    static readonly double[,] Cosines = CreateCosines();

    sealed record Signature(Photo Photo, VisualFingerprint[] Frames, TimeSpan Duration, bool IsVideo);
    sealed class Cluster(Signature representative)
    {
        public int Id
        {
            get; set;
        }
        public Signature Representative { get; } = representative;
        public List<SimilarityMatch> Matches { get; } = new();
    }

    // A BK-tree finds all representative hashes within the threshold without comparing
    // every file pair. The separate secondary checks make admission more conservative.
    sealed class HashIndex
    {
        sealed class Node(ulong hash, int index)
        {
            public ulong Hash { get; } = hash;
            public List<int> Indices { get; } = new() { index };
            public Dictionary<int, Node> Children { get; } = new();
        }
        Node? root;

        public void Add(ulong hash, int index)
        {
            if (root == null)
            {
                root = new Node(hash, index);
                return;
            }
            var node = root;
            while (true)
            {
                int distance = HammingDistance(hash, node.Hash);
                if (distance == 0)
                {
                    node.Indices.Add(index);
                    return;
                }
                if (node.Children.TryGetValue(distance, out var child))
                    node = child;
                else
                {
                    node.Children.Add(distance, new Node(hash, index));
                    return;
                }
            }
        }

        public IEnumerable<int> Find(ulong hash, int threshold)
        {
            if (root == null)
                yield break;
            var pending = new Stack<Node>();
            pending.Push(root);
            while (pending.TryPop(out var node))
            {
                int distance = HammingDistance(hash, node.Hash);
                if (distance <= threshold)
                    foreach (int index in node.Indices)
                        yield return index;
                foreach (var child in node.Children)
                    if (child.Key >= distance - threshold && child.Key <= distance + threshold)
                        pending.Push(child.Value);
            }
        }
    }

    public async Task ScanAsync(IReadOnlyList<Photo> photos, int threshold, Action<SimilarityGroup> found,
        Action<string> progress, CancellationToken cancellationToken, Action? pauseCheck = null)
    {
        if (threshold < 0 || threshold > 16)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Choose a threshold between 0 and 16 differing hash bits.");
        Processed = Skipped = PhotosCompared = VideosCompared = GroupsFound = 0;
        Warnings.Clear();
        var clusters = new List<Cluster>();
        var photoIndex = new HashIndex();
        var videoIndex = new HashIndex();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirty = new HashSet<int>();
        var clock = Stopwatch.StartNew();
        var publishClock = Stopwatch.StartNew();
        void Gate()
        {
            cancellationToken.ThrowIfCancellationRequested();
            pauseCheck?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
        void Publish(bool force = false)
        {
            if (!force && publishClock.ElapsedMilliseconds < 250)
                return;
            foreach (int index in dirty.Order())
            {
                Gate();
                var cluster = clusters[index];
                found(new SimilarityGroup(cluster.Id, cluster.Representative.Photo, cluster.Matches, cluster.Representative.IsVideo));
            }
            dirty.Clear();
            publishClock.Restart();
        }
        void Report(bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds < 150)
                return;
            clock.Restart();
            progress($"Comparing appearance… {Processed:N0} / {photos.Count:N0} files · {PhotosCompared:N0} photos · {VideosCompared:N0} videos · {GroupsFound:N0} possible groups · {Skipped:N0} skipped");
        }
        Report(true);

        foreach (var photo in photos)
        {
            Gate();
            Publish();
            if (!seen.Add(photo.Path))
            {
                Processed++;
                continue;
            }
            try
            {
                Scanner.Check(photo);
                bool video = Scanner.VideoExtensions.Contains(Path.GetExtension(photo.Path));
                VisualFingerprint[] frames;
                TimeSpan duration = TimeSpan.Zero;
                if (video)
                {
                    var sample = await VideoFrames.SampleAsync(photo.Path, cancellationToken).ConfigureAwait(false);
                    duration = sample.Duration;
                    frames = sample.Frames.Select(Fingerprint).ToArray();
                }
                else
                {
                    var bitmap = await Thumbnail.DecodeAsync(photo.Path, 256, cancellationToken).ConfigureAwait(false);
                    frames = new[] { Fingerprint(bitmap) };
                }
                Gate();
                Scanner.Check(photo);
                if (frames.Length == 0 || frames.Any(frame => frame.Contrast < 3))
                    throw new NotSupportedException("Too little visible detail was decoded for a reliable appearance comparison.");

                var signature = new Signature(photo, frames, duration, video);
                if (video)
                    VideosCompared++;
                else
                    PhotosCompared++;
                var index = video ? videoIndex : photoIndex;
                int bestIndex = -1, bestDistance = int.MaxValue;
                foreach (int candidateIndex in index.Find(frames[frames.Length / 2].Hash, threshold))
                {
                    Gate();
                    int distance = Compare(signature, clusters[candidateIndex].Representative, threshold);
                    if (distance >= 0 && distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = candidateIndex;
                    }
                }
                if (bestIndex >= 0)
                {
                    var cluster = clusters[bestIndex];
                    if (cluster.Matches.Count == 0)
                        cluster.Id = ++GroupsFound;
                    cluster.Matches.Add(new SimilarityMatch(photo, bestDistance));
                    dirty.Add(bestIndex);
                }
                else
                {
                    index.Add(frames[frames.Length / 2].Hash, clusters.Count);
                    clusters.Add(new Cluster(signature));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException or InvalidOperationException or FormatException or COMException or OverflowException)
            {
                Skipped++;
                Warnings.Enqueue($"Similarity skipped {photo.Path}: {exception.Message}");
            }
            Processed++;
            Publish();
            Report();
        }
        Publish(force: true);
        Report(true);
    }

    static int Compare(Signature candidate, Signature representative, int threshold)
    {
        if (candidate.IsVideo != representative.IsVideo || candidate.Frames.Length != representative.Frames.Length)
            return -1;
        if (candidate.IsVideo && Math.Abs((candidate.Duration - representative.Duration).TotalSeconds) > Math.Max(0.5, representative.Duration.TotalSeconds * 0.05))
            return -1;
        int worst = 0;
        for (int i = 0; i < candidate.Frames.Length; i++)
        {
            var first = candidate.Frames[i];
            var second = representative.Frames[i];
            int distance = HammingDistance(first.Hash, second.Hash);
            if (distance > threshold || Math.Abs(Math.Log(first.AspectRatio / second.AspectRatio)) > 0.08
                || HammingDistance(first.DifferenceHash, second.DifferenceHash) > Math.Min(28, Math.Max(4, threshold * 2))
                || Math.Abs(first.MeanRed - second.MeanRed) + Math.Abs(first.MeanGreen - second.MeanGreen) + Math.Abs(first.MeanBlue - second.MeanBlue) > 105)
                return -1;
            worst = Math.Max(worst, distance);
        }
        return worst;
    }

    public static int HammingDistance(ulong first, ulong second) => BitOperations.PopCount(first ^ second);

    public static VisualFingerprint Fingerprint(BitmapSource source)
    {
        const int size = 32;
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
            throw new ArgumentException("An image must have positive dimensions.", nameof(source));
        var scaled = new TransformedBitmap(source, new ScaleTransform((double)size / source.PixelWidth, (double)size / source.PixelHeight));
        var rgba = new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[size * size * 4];
        rgba.CopyPixels(pixels, size * 4, 0);
        var gray = new double[size, size];
        double red = 0, green = 0, blue = 0, mean = 0, squared = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int offset = (y * size + x) * 4;
                int white = 255 - pixels[offset + 3];
                double r = pixels[offset + 2] + white, g = pixels[offset + 1] + white, b = pixels[offset] + white;
                double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                red += r;
                green += g;
                blue += b;
                mean += luminance;
                squared += luminance * luminance;
                gray[x, y] = luminance;
            }

        var horizontal = new double[8, size];
        for (int u = 0; u < 8; u++)
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    horizontal[u, y] += gray[x, y] * Cosines[u, x];
        var coefficients = new double[64];
        for (int v = 0; v < 8; v++)
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int y = 0; y < size; y++)
                    sum += horizontal[u, y] * Cosines[v, y];
                coefficients[v * 8 + u] = sum * (u == 0 ? Math.Sqrt(0.5) : 1) * (v == 0 ? Math.Sqrt(0.5) : 1);
            }
        double median = coefficients.Skip(1).Order().ElementAt(31);
        ulong hash = 0, difference = 0;
        for (int i = 1; i < coefficients.Length; i++)
            if (coefficients[i] > median)
                hash |= 1UL << i;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                if (gray[Math.Min(31, x * 4), y * 4 + 2] > gray[Math.Min(31, (x + 1) * 4), y * 4 + 2])
                    difference |= 1UL << (y * 8 + x);
        int count = size * size;
        double average = mean / count;
        return new VisualFingerprint(hash, difference, (double)source.PixelWidth / source.PixelHeight,
            red / count, green / count, blue / count, Math.Sqrt(Math.Max(0, squared / count - average * average)));
    }

    static double[,] CreateCosines()
    {
        var result = new double[8, 32];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 32; x++)
                result[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 64);
        return result;
    }
}
