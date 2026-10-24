using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;
using DcxManager.Documents;

namespace DcxManager;

// SelfTest: the off-screen render helper every area uses (a static local function of RunFullAsync until 0.2.0 phase 6½).
public static partial class SelfTest
{
    static void Snap(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        // The UI text size is a LayoutTransform on the root: size the bitmap after it, or the right/bottom get cropped.
        var size = root.LayoutTransform.TransformBounds(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle((System.Windows.Media.Brush)Application.Current.Resources["Bg"], null, new Rect(0, 0, size.Width, size.Height));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
