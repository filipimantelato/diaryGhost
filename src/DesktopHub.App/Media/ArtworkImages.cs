using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopHub.Core.Media;

namespace DesktopHub.App.Media;

/// <summary>
/// Decodifica capas de álbum (bytes não confiáveis vindos de outro app) em imagens pequenas e congeladas.
/// Decodifica já reduzida (<see cref="DecodeWidth"/>) para não manter bitmaps grandes em memória, e
/// guarda poucas entradas, identificadas pela impressão digital dos bytes.
/// </summary>
internal static class ArtworkImages
{
    /// <summary>Suficiente para o maior uso (cartão de 128 px) e próximo dos tamanhos exibidos — menos escala por quadro.</summary>
    private const int DecodeWidth = 128;
    private const int Capacity = 6;
    private static readonly LinkedList<(string Key, ImageSource Image)> Cache = new();

    public static ImageSource? Get(MediaArtwork? artwork)
    {
        if (artwork is null)
        {
            return null;
        }
        for (var node = Cache.First; node is not null; node = node.Next)
        {
            if (node.Value.Key == artwork.Fingerprint)
            {
                Cache.Remove(node);
                Cache.AddFirst(node);
                return node.Value.Image;
            }
        }

        var image = Decode(artwork.Bytes);
        if (image is null)
        {
            return null;
        }
        Cache.AddFirst((artwork.Fingerprint, image));
        while (Cache.Count > Capacity)
        {
            Cache.RemoveLast();
        }
        return image;
    }

    private static BitmapImage? Decode(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = DecodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or IOException)
        {
            return null; // imagem inválida: a UI mostra o ícone padrão
        }
    }
}
