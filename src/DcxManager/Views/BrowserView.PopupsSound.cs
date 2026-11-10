using System.Windows;
using System.Windows.Controls;
using DcxManager.Browser;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// A portal tab's pop-ups and sound (0.6.33).
/// <list type="bullet">
/// <item><b>Pop-ups</b> (DarkCipher0x: "block popups by default, with the option to allow if wanted. make sure its liteweight
/// and secure"): <see cref="OnNewWindow"/> refuses a new window unless the site is allowed — no browser view is made for it.
/// The ⛔ ⧉ button lists what was blocked on this page: open one once in a tab (an http(s) address only, through the same
/// address and navigation rules as any page, without a link back to the opener) or allow the site from now on.</item>
/// <item><b>Sound</b> (DarkCipher0x: "the ability to mute tabs ... i want to be able to mute it or the site"): WebView2's own
/// mute. The tab shows 🔊 while the page plays audio and 🔇 while muted; the toolbar's speaker button and the tab menus
/// mute the tab, or every tab of the site (kept encrypted with the portals).</item>
/// </list>
/// Per-site choices are stored as scheme://host:port (<see cref="Portals.SiteOf"/>), so they never spread to another host or port.
/// </summary>
public sealed partial class BrowserView
{
    internal int PopupsBlocked { get; private set; }
    internal IReadOnlyList<string> BlockedPopupsForTest => _blockedPopups;
    private string? Site => Portals.SiteOf(Core?.Source);
    private string SiteLabel => Uri.TryCreate(Core?.Source, UriKind.Absolute, out var u) ? u.Authority : "this site";

    /// <summary>Records a refused pop-up and shows the ⛔ ⧉ button (UI thread).</summary>
    private void PopupRefused(string target)
    {
        _blockedPopups.Remove(target);
        _blockedPopups.Add(target);
        if (_blockedPopups.Count > 10) _blockedPopups.RemoveAt(0);
        _popupBtn.Content = _blockedPopups.Count > 1 ? $"⛔ ⧉ {_blockedPopups.Count}" : "⛔ ⧉";
        _popupBtn.ToolTip = $"{_blockedPopups.Count} pop-up(s) blocked on this page — click to open one in a tab or allow pop-ups on {SiteLabel}";
        _popupBtn.Visibility = Visibility.Visible;
        MainWindow.Instance?.Status($"⛔ Pop-up blocked on {SiteLabel} (⛔ ⧉ in the tab's toolbar opens it or allows the site)");
    }

    private void ClearBlockedPopups()
    {
        _blockedPopups.Clear();
        _popupBtn.Visibility = Visibility.Collapsed;
    }

    /// <summary>The ⛔ ⧉ button's menu: each blocked pop-up can be opened once in a tab; the site can be allowed.</summary>
    private IEnumerable<object?> PopupMenu()
    {
        foreach (var target in _blockedPopups.AsEnumerable().Reverse().ToList())
        {
            bool web = Portals.ValidateUrl(target) == null;
            yield return Menus.Item("Open in a tab: " + (web ? Short(Portals.Display(target), 70) : "(a page the site writes itself)"), () => OpenBlocked(target), enabled: web,
                tip: web ? "Opens the address once in a new tab of this profile (without a link back to this page)" : "This pop-up has no address of its own: allow pop-ups on the site and try again");
        }
        yield return Menus.Sep();
        if (Site is { } site)
            yield return Menus.Item($"Always allow pop-ups on {SiteLabel}", () =>
            {
                Portals.SetPopupsAllowed(site, true);
                ClearBlockedPopups();
                MainWindow.Instance?.Status($"Pop-ups allowed on {SiteLabel}: try again on the page");
            });
        yield return Menus.Item("Dismiss", ClearBlockedPopups);
    }

    private async void OpenBlocked(string target)
    {
        try
        {
            if (Portals.ValidateUrl(target) != null) return;
            _blockedPopups.Remove(target);
            if (_blockedPopups.Count == 0) _popupBtn.Visibility = Visibility.Collapsed;
            var v = PortalUi.OpenPopup(this);
            await v.Ready;
            v.Go(target);   // through the same address and navigation rules as any page
        }
        catch (Exception ex) { App.Log("Portal pop-up", ex); }
    }

    // ---- sound ------------------------------------------------------------------------------------------------------

    /// <summary>The page's mute state: the tab's own choice, else the site's "always mute".</summary>
    private void ApplyMute()
    {
        if (Core == null || _disposed) return;
        _siteMuted = Portals.SiteMuted(Site);
        bool mute = _muteChoice ?? _siteMuted;
        if (Core.IsMuted != mute) Core.IsMuted = mute;
        UpdateSound();
    }

    private bool _siteMuted;

    /// <summary>A change to the site's "always mute" is the newest choice: it replaces the tab's own one.</summary>
    private void OnSitesChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_disposed || Core == null) return;
        if (Portals.SiteMuted(Site) != _siteMuted) _muteChoice = null;
        ApplyMute();
    });

    internal bool IsMuted => Core?.IsMuted == true;

    /// <summary>Mutes or unmutes this tab (until changed again; the site's setting stays as it is).</summary>
    internal void ToggleMute()
    {
        if (Core == null) return;
        _muteChoice = !Core.IsMuted;
        ApplyMute();
    }

    private void UpdateSound()
    {
        if (Core == null || _disposed) return;
        bool muted = Core.IsMuted, playing = Core.IsDocumentPlayingAudio;
        _sound.Visibility = muted || playing ? Visibility.Visible : Visibility.Collapsed;
        _sound.Content = muted ? "🔇" : "🔊";
        _sound.ToolTip = muted ? "Unmute this tab" : "Mute this tab";
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        if (Tab == null || Core == null) return;
        var mark = Core.IsMuted ? " 🔇" : Core.IsDocumentPlayingAudio ? " 🔊" : "";
        Tab.Header = (Incognito ? "🕶 " : "🌐 ") + Short(_title.Length > 0 ? _title : Bookmark.Title, 36) + mark;
    }

    /// <summary>Sound and pop-up items for the tab's menus (right-click on the tab, ⋯).</summary>
    internal IEnumerable<object?> SoundMenu()
    {
        if (Core == null) yield break;
        yield return Menus.Item(Core.IsMuted ? "🔊 Unmute this tab" : "🔇 Mute this tab", ToggleMute);
        if (Site is { } site)
            yield return Portals.SiteMuted(site)
                ? Menus.Item($"🔊 Stop always muting {SiteLabel}", () => { _muteChoice = null; Portals.SetSiteMuted(site, false); })
                : Menus.Item($"🔇 Always mute {SiteLabel}", () => { _muteChoice = null; Portals.SetSiteMuted(site, true); }, tip: "Every tab of this site starts muted, now and later");
        if (Site is { } s2 && AppSettings.Current.PortalBlockPopups)
            yield return Portals.PopupsAllowed(s2)
                ? Menus.Item($"⛔ Block pop-ups on {SiteLabel} again", () => Portals.SetPopupsAllowed(s2, false))
                : Menus.Item($"⧉ Always allow pop-ups on {SiteLabel}", () => Portals.SetPopupsAllowed(s2, true));
    }
}
