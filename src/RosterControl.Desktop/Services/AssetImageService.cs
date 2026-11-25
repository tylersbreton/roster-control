using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using RosterControl.Core.Abstractions;
using SkiaSharp;

namespace RosterControl.Desktop.Services;

/// <summary>
/// Decodes cached images into Avalonia bitmaps off the UI thread and keeps them in memory. Grayscale variants (used for
/// unselected runes, like the League client) are produced with SkiaSharp.
/// </summary>
public sealed class AssetImageService
{
    private readonly IAssetCache _cache;
    private readonly ConcurrentDictionary<string, Bitmap?> _bitmaps = new();
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _loading = new();

    public AssetImageService(IAssetCache cache) => _cache = cache;

    public static string Key(string url, int width, bool grayscale) => $"{url}|{width}|{(grayscale ? 1 : 0)}";

    public Bitmap? TryGet(string? url, int width, bool grayscale)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var key = Key(url, width, grayscale);
        if (_bitmaps.TryGetValue(key, out var bmp)) return bmp;
        return null;
    }

    public Task<Bitmap?> GetAsync(string? url, int width, bool grayscale)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<Bitmap?>(null);
        var key = Key(url, width, grayscale);
        if (_bitmaps.TryGetValue(key, out var bmp)) return Task.FromResult(bmp);
        return _loading.GetOrAdd(key, k => Task.Run(async () =>
        {
            try
            {
                var path = _cache.TryGetCachedPath(url) ?? await _cache.GetAsync(url, CancellationToken.None).ConfigureAwait(false);
                var result = path is null ? null : Decode(path, width, grayscale);
                if (result is not null) _bitmaps[key] = result;
                return result;
            }
            finally
            {
                _loading.TryRemove(key, out _);
            }
        }));
    }

    /// <summary>Downloads (if needed) and decodes a set of images; used to warm caches before showing a page.</summary>
    public Task WarmAsync(IEnumerable<string> urls, int width, TimeSpan timeout) =>
        Task.WhenAll(urls.Distinct().Select(u => GetAsync(u, width, false))).WaitAsync(timeout).ContinueWith(_ => { }, TaskScheduler.Default);

    private static Bitmap? Decode(string path, int width, bool grayscale)
    {
        try
        {
            if (!grayscale)
            {
                using var fs = File.OpenRead(path);
                using var probe = SKCodec.Create(path);
                var actual = probe?.Info.Width ?? width;
                return actual <= width ? new Bitmap(fs) : Bitmap.DecodeToWidth(fs, width, BitmapInterpolationMode.HighQuality);
            }

            using var source = SKBitmap.Decode(path);
            if (source is null) return null;
            var w = Math.Min(width, source.Width);
            var h = Math.Max(1, (int)Math.Round(source.Height * (w / (double)source.Width)));
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                // Luminance (Rec. 709) with a slight darkening, like the client's unselected runes.
                ColorFilter = SKColorFilter.CreateColorMatrix(
                [
                    0.2126f * 0.85f, 0.7152f * 0.85f, 0.0722f * 0.85f, 0, 0,
                    0.2126f * 0.85f, 0.7152f * 0.85f, 0.0722f * 0.85f, 0, 0,
                    0.2126f * 0.85f, 0.7152f * 0.85f, 0.0722f * 0.85f, 0, 0,
                    0, 0, 0, 1, 0,
                ]),
            };
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data.ToArray());
            return new Bitmap(ms);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
