using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace IDVBuff.Survey.Editor.WinUI;

internal static class SurveyBitmapLoader
{
    public static async Task<SurveySampledPixel?> ReadPixelAsync(
        Stream source,
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);
        using var randomAccess = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccess))
        {
            writer.WriteBytes(memory.ToArray());
            await writer.StoreAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }
        cancellationToken.ThrowIfCancellationRequested();
        randomAccess.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(randomAccess);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
            return null;
        var pixelX = (uint)Math.Clamp(x, 0, (int)decoder.PixelWidth - 1);
        var pixelY = (uint)Math.Clamp(y, 0, (int)decoder.PixelHeight - 1);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var bytes = pixels.DetachPixelData();
        var offset = checked((int)((pixelY * decoder.PixelWidth + pixelX) * 4));
        if (offset + 3 >= bytes.Length)
            return null;
        var alpha = bytes[offset + 3];
        return new SurveySampledPixel(
            Unpremultiply(bytes[offset + 2], alpha),
            Unpremultiply(bytes[offset + 1], alpha),
            Unpremultiply(bytes[offset], alpha),
            alpha);
    }

    private static byte Unpremultiply(byte value, byte alpha) => alpha == 0
        ? (byte)0
        : (byte)Math.Clamp(Math.Round(value * 255d / alpha), 0d, 255d);

    public static async Task<BitmapImage> LoadAsync(
        SurveyEditorSession session,
        SurveyAssetReference asset,
        CancellationToken cancellationToken = default)
    {
        await using var source = await session.OpenAssetAsync(asset, cancellationToken);
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);
        using var randomAccess = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccess))
        {
            writer.WriteBytes(memory.ToArray());
            await writer.StoreAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }
        cancellationToken.ThrowIfCancellationRequested();
        randomAccess.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(randomAccess).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }

    public static async Task<BitmapImage> LoadLayerAsync(
        SurveyEditorSession session,
        Guid layerId,
        int? decodePixelWidth = null,
        CancellationToken cancellationToken = default)
    {
        await using var source = await session.OpenRenderedLayerAsync(layerId, cancellationToken);
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);
        using var randomAccess = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccess))
        {
            writer.WriteBytes(memory.ToArray());
            await writer.StoreAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }
        cancellationToken.ThrowIfCancellationRequested();
        randomAccess.Seek(0);
        var bitmap = new BitmapImage();
        if (decodePixelWidth is > 0)
            bitmap.DecodePixelWidth = decodePixelWidth.Value;
        await bitmap.SetSourceAsync(randomAccess).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }
}

internal sealed record SurveySampledPixel(byte R, byte G, byte B, byte A);
