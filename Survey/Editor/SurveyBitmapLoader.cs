using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace IDVBuff.Survey.Editor.WinUI;

internal static class SurveyBitmapLoader
{
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
