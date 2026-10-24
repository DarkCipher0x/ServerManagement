using System.IO;
using System.Windows;
using System.Windows.Threading;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Copies a password or secret to the clipboard (0.6.14, ADR-093) the way password managers do: marked so Windows keeps
/// it out of clipboard history and cloud clipboard sync (and clipboard monitors skip it), and cleared again after a few
/// seconds unless something else was copied meanwhile.
/// </summary>
public static class SecretClipboard
{
    private static DispatcherTimer? _timer;

    /// <summary>The clipboard data for a secret: the text plus the formats that keep it out of history and sync.</summary>
    internal static DataObject Make(string text)
    {
        var d = new DataObject();
        d.SetText(text, TextDataFormat.UnicodeText);
        d.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0 }));
        d.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        d.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        return d;
    }

    /// <summary>Copies it and clears it after <paramref name="seconds"/>. False when the clipboard was busy.</summary>
    public static bool Copy(string text, int seconds)
    {
        if (TestMode.Running) return true;   // the self-test never touches the real clipboard
        try { Clipboard.SetDataObject(Make(text), false); }
        catch (System.Runtime.InteropServices.COMException) { return false; }
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 300)) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear(); }
            catch (System.Runtime.InteropServices.COMException) { }
        };
        _timer.Start();
        return true;
    }
}
