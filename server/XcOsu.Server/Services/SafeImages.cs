using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace XcOsu.Server.Services;

public static class SafeImages
{
    private static readonly SemaphoreSlim slots = new(2, 2);
    public static async Task<byte[]> Reencode(byte[] bytes, bool jpeg, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        if (!await slots.WaitAsync(0, timeout.Token)) throw new BadHttpRequestException("Image service busy. Please retry.", 429);
        try
        {
            var options = new DecoderOptions { SkipMetadata = true, MaxFrames = 121 };
            var info = Image.Identify(options, bytes);
            if (info.Width > 8192 || info.Height > 8192 || (long)info.Width * info.Height > 32_000_000
                || info.FrameMetadataCollection.Count > 120
                || (long)info.Width * info.Height * info.FrameMetadataCollection.Count > 64_000_000)
                throw new BadHttpRequestException("Image dimensions or animation exceed limits", 422);
            using var input = new MemoryStream(bytes);
            using var image = await Image.LoadAsync(options, input, timeout.Token);
            var format = image.Metadata.DecodedImageFormat;
            if (format?.Name is not ("PNG" or "JPEG" or "GIF" or "Webp"))
                throw new BadHttpRequestException("Unsupported image format", 415);
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            using var output = new MemoryStream();
            if (jpeg) await image.SaveAsync(output, new JpegEncoder(), timeout.Token);
            else await image.SaveAsync(output, format, timeout.Token);
            return output.ToArray();
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException)
        {
            throw new BadHttpRequestException("Invalid image", 422);
        }
        finally { slots.Release(); }
    }
    public static async Task WriteAtomic(string path, byte[] bytes, CancellationToken ct)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
