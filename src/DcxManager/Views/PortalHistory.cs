using System.Windows;
using System.Windows.Interop;
using DcxManager.Browser;
using DcxManager.Services;
using Microsoft.Web.WebView2.Core;

namespace DcxManager.Views;

/// <summary>
/// The portal browser's history (0.6.37, ADR-116): the pages and downloads WebView2 keeps in its (encrypted) profile.
/// Sign-ins, cookies and site data are not history and stay. While history is off, each profile's history is cleared the
/// first time a portal tab starts it in a run, so pages last only for that run; "Clear" clears every profile now.
/// </summary>
public static class PortalHistory
{
    private const CoreWebView2BrowsingDataKinds Kinds = CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory;
    private static readonly HashSet<string> TrimmedThisRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called when a portal tab's browser has started: while history is off, its profile's earlier pages go
    /// (once per profile per run).</summary>
    public static async Task OnStartedAsync(CoreWebView2 core, string profile)
    {
        if (ActivityHistory.Saving || !TrimmedThisRun.Add(profile)) return;
        try { await core.Profile.ClearBrowsingDataAsync(Kinds); }
        catch (Exception ex) { App.Log("Portal history", ex); }
    }

    /// <summary>Every profile the portals use (and the default one): their page and download history, now. A hidden
    /// browser controller per profile does it, so it works with no portal tab open.</summary>
    public static async Task ClearAllAsync()
    {
        if (Application.Current?.MainWindow is not { } w || new WindowInteropHelper(w).Handle == IntPtr.Zero) return;
        try
        {
            var env = await WebHost.EnvironmentAsync();
            foreach (var profile in Portals.ProfilesInUse().Prepend("").Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var opts = env.CreateCoreWebView2ControllerOptions();
                opts.ProfileName = profile.Length == 0 ? null : profile;
                opts.IsInPrivateModeEnabled = false;
                var controller = await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(w).Handle, opts);
                try { controller.IsVisible = false; await controller.CoreWebView2.Profile.ClearBrowsingDataAsync(Kinds); }
                finally { controller.Close(); }
            }
        }
        catch (Exception ex) { App.Log("Portal history", ex); }
    }
}
