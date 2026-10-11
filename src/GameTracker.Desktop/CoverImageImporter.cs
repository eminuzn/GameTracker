using System.IO;
using System.Windows.Media.Imaging;
using GameTracker.Infrastructure;

namespace GameTracker.Desktop;

public static class CoverImageImporter
{
    public static byte[] ReadPng(string file)
    {
        using var input = File.OpenRead(file);
        if (input.Length > CoverClient.MaxBytes) throw new IOException("Kapak dosyası en fazla 8 MB olabilir.");
        // Decode locally before saving; unsupported or corrupt images never replace
        // the current cover. The first frame of GIF/TIFF becomes a static cover.
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > 20_000_000)
            throw new IOException("Kapak en fazla 20 megapiksel olabilir.");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream(); encoder.Save(output);
        if (output.Length > CoverClient.MaxBytes) throw new IOException("Dönüştürülen kapak en fazla 8 MB olabilir.");
        return output.ToArray();
    }
}
