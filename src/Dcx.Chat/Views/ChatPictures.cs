using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CipherChat.Crypto;
using CipherChat.Engine;

namespace DcxManager.Chat.Views;

/// <summary>
/// Pictures in chats (0.6.38, ADR-117): the same rules as CipherChat's own Windows app, so a screenshot sent from here
/// shows inline on every CipherChat client. Only PNG, JPEG and GIF — recognised by their bytes (<see cref="Files.Sniff"/>),
/// never by the name — are decoded and shown; anything else stays a file to save. A picture sent gets a JPEG thumbnail
/// (360 px at most) and its size, encrypted and uploaded with it by the engine.
/// </summary>
internal static class ChatPictures
{
    private static readonly HashSet<string> Inline = ["image/png", "image/jpeg", "image/gif"];
    public const int ThumbMax = 360;

    /// <summary>A picture this app shows (by its bytes).</summary>
    public static bool IsPicture(byte[] bytes) => Inline.Contains(Files.Sniff(bytes));

    /// <summary>A file for the next message: a picture gets its thumbnail and size; anything else goes as it is.</summary>
    public static ChatEngine.OutgoingFile Outgoing(string name, byte[] bytes, string? mime = null)
    {
        var (thumb, w, h) = Thumbnail(bytes);
        return new ChatEngine.OutgoingFile(name, bytes, mime, thumb, w, h);
    }

    /// <summary>The JPEG thumbnail (at most <see cref="ThumbMax"/> px on its long side) and the picture's size, or nulls
    /// for anything that isn't a picture this app shows (or that doesn't decode).</summary>
    public static (byte[]? Thumb, int? W, int? H) Thumbnail(byte[] bytes)
    {
        if (!IsPicture(bytes)) return (null, null, null);
        try
        {
            var src = Decode(bytes, 0);
            int w = src.PixelWidth, h = src.PixelHeight;
            var scale = Math.Min(1.0, ThumbMax / (double)Math.Max(w, h));
            var small = new TransformedBitmap(src, new ScaleTransform(scale, scale));
            var enc = new JpegBitmapEncoder { QualityLevel = 75 };
            enc.Frames.Add(BitmapFrame.Create(small));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (ms.ToArray(), w, h);
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException) { return (null, null, null); }
    }

    /// <summary>Decodes a picture (frozen, nothing kept open); <paramref name="width"/> 0 = full size.</summary>
    public static BitmapSource Decode(byte[] bytes, int width)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) bmp.DecodePixelWidth = width;
        bmp.StreamSource = new MemoryStream(bytes, writable: false);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A picture for the screen, or null when the bytes aren't one this app shows.</summary>
    public static BitmapSource? TryDecode(byte[] bytes, int width)
    {
        if (!IsPicture(bytes)) return null;
        try { return Decode(bytes, width); }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException) { return null; }
    }
}
