using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace BudzetDomowy.App.Infrastructure;

/// <summary>Draws a QR code the way the recommendation of the Polish Bank Association asks: error correction L, UTF-8, black on white with a quiet zone.</summary>
public static class QrImage
{
    public static BitmapSource Create(string payload, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.L, forceUtf8: true);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule, [0, 0, 0, 255], [255, 255, 255, 255], drawQuietZones: true);

        using var stream = new MemoryStream(png);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }
}
