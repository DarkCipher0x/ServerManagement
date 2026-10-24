using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DcxManager.Services;

/// <summary>
/// The Windows Share sheet (Mail, Teams, Outlook, Nearby Sharing, OneDrive…) from a desktop window, via
/// IDataTransferManagerInterop. Content to share is written to a private temp folder that is emptied on the next start
/// and when the app exits — shared files are plain copies by nature, so the user is told what is being shared.
/// </summary>
public static class ShareService
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid DtmIid = new("a5caee9b-8708-49d1-8d36-67d25a8da00c");

    public static string TempDir => AppInfo.ShareDir;

    public static bool Supported => Environment.OSVersion.Version >= new Version(10, 0, 17763);

    /// <summary>Removes files left from previous shares.</summary>
    public static void Cleanup()
    {
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
    }

    /// <summary>Shares a text snippet (and optionally the same content as a file, e.g. report.html or script.sh).</summary>
    public static void ShareText(Window? owner, string title, string text, string? fileName = null)
    {
        string? path = null;
        if (fileName != null)
        {
            Directory.CreateDirectory(TempDir);
            path = Path.Combine(TempDir, Safe(fileName));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        Show(owner, title, fileName == null ? text : null, path == null ? Array.Empty<string>() : new[] { path });
    }

    public static void ShareFiles(Window? owner, string title, params string[] files) => Show(owner, title, null, files);

    private static void Show(Window? owner, string title, string? text, string[] files)
    {
        if (!Supported) { Ui.Dialogs.Info("Share", "The Windows Share sheet needs Windows 10 1809 or newer."); return; }
        owner ??= Application.Current.MainWindow;
        var hwnd = new WindowInteropHelper(owner!).Handle;
        try
        {
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = DtmIid;
            var ptr = interop.GetForWindow(hwnd, ref iid);
            var dtm = WinRT.MarshalInterface<DataTransferManager>.FromAbi(ptr);
            Windows.Foundation.TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            handler = async (s, e) =>
            {
                dtm.DataRequested -= handler;
                var def = e.Request.GetDeferral();
                try
                {
                    var data = e.Request.Data;
                    data.Properties.Title = title;
                    data.Properties.Description = "From DarkCipher ServerManagement";
                    if (text != null) data.SetText(text);
                    if (files.Length > 0)
                    {
                        var items = new List<IStorageItem>();
                        foreach (var f in files) items.Add(await StorageFile.GetFileFromPathAsync(f));
                        data.SetStorageItems(items);
                    }
                }
                catch (Exception ex) { e.Request.FailWithDisplayText(ex.Message); Log.Write("Share", ex); }
                finally { def.Complete(); }
            };
            dtm.DataRequested += handler;
            interop.ShowShareUIForWindow(hwnd);
            AuditLog.Write("you", "share", "opened Windows Share", title);
        }
        catch (Exception ex) { Log.Write("Share", ex); Ui.Dialogs.Error("Share", "Windows Share isn't available: " + ex.Message); }
    }

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
