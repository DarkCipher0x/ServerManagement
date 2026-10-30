using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// The hosts panel beside every tab (0.6.31; DarkCipher0x: "it should all be on the same frame... it shouldnt be embedded like
/// that. But i still want the ability to pin the hosts panel (that looks perfect - it just needs to be for the whole
/// frame)", and "the nested tabs, when i reconnect, arent nested anymore. Id rather just get rid of that nested look, so
/// they are all in the same tab bar").
/// <para>The main window owns one Hosts page for its whole life (<see cref="HostsPage"/>). Its folder pane (📌 / 📍, the
/// filter, the tree with folders, hosts, ★ Favourites and shared libraries, ＋ Host, ＋ Folder) is moved out of the page
/// into this column, so it stays beside whatever tab is in front. The Hosts tab shows the page's list, filtered by what is
/// picked in the panel; closing the tab keeps the page (and the panel) alive.</para>
/// <list type="bullet">
/// <item>📌 <b>pinned</b> (default): the panel has its own resizable column.</item>
/// <item>📍 <b>auto</b>: the column is a slim "Hosts" strip at the left of the tabs. Pointing at it (or clicking it, or
/// dragging hosts onto it) opens the panel; it folds back shortly after the pointer leaves, or on a click into the tabs.
/// The column widens rather than drawing over the tabs: terminals and desktops are native windows WPF can't draw over.</item>
/// </list>
/// <see cref="AppSettings.HostsDock"/> holds the mode and <see cref="AppSettings.HostsDockWidth"/> the width (both as
/// before: the same panel, one level up). Extra windows keep their own host pane (<see cref="FloatWindow"/>).
/// </summary>
public partial class MainWindow
{
    private const double HostStripWidth = 22;
    private HostsView? _hostsPage;
    private bool _hostPaneOpen;
    private readonly DispatcherTimer _hostPaneClose = new() { Interval = TimeSpan.FromMilliseconds(450) };

    private bool _hostPaneWired;

    /// <summary>The one Hosts page: its list is the Hosts tab, its folder pane is the hosts panel. Made just after the
    /// window's first frame (the page and its tree cost ~200 ms, which start-up shouldn't wait for), or sooner when asked.</summary>
    internal HostsView HostsPage
    {
        get
        {
            if (_hostsPage == null)
            {
                _hostsPage = new HostsView();
                if (_hostPaneWired) { HostPaneHost.Child = _hostsPage.TakeFolderPane(); _hostsPage.ApplyFolderPane(); }
            }
            return _hostsPage;
        }
    }

    /// <summary>The panel is showing (always when pinned; opened when auto).</summary>
    internal bool HostPaneShowing => HostsView.FolderMode == "pinned" || _hostPaneOpen;
    /// <summary>Self-test: the panel's parts.</summary>
    internal (ColumnDefinition Col, Border Host, Border Strip, GridSplitter Splitter) HostPaneParts => (HostPaneCol, HostPaneHost, HostPaneStrip, HostPaneSplitter);

    private void InitHostPane()
    {
        _hostPaneWired = true;
        if (_hostsPage != null) HostPaneHost.Child = _hostsPage.TakeFolderPane();
        else Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = HostsPage);
        BuildHostStrip();
        _hostPaneClose.Tick += (_, _) =>
        {
            // Stay open while the pointer is back on it, a menu of it holds the mouse, or a drag is in progress.
            if (HostPaneHost.IsMouseOver || HostPaneStrip.IsMouseOver || Mouse.Captured != null || Mouse.LeftButton == MouseButtonState.Pressed) return;
            _hostPaneClose.Stop();
            SetHostPaneOpen(false);
        };
        HostPaneStrip.MouseLeftButtonUp += (_, _) => SetHostPaneOpen(true);
        HostPaneStrip.MouseEnter += (_, _) => SetHostPaneOpen(true);
        HostPaneStrip.DragEnter += (_, _) => SetHostPaneOpen(true);   // drag hosts onto a folder while it's folded
        HostPaneHost.MouseEnter += (_, _) => _hostPaneClose.Stop();
        HostPaneHost.MouseLeave += (_, _) => { if (HostsView.FolderMode == "auto" && _hostPaneOpen) _hostPaneClose.Start(); };
        HostPaneHost.DragLeave += (_, _) => { if (HostsView.FolderMode == "auto" && _hostPaneOpen) _hostPaneClose.Start(); };
        DocArea.PreviewMouseDown += (_, _) => { if (HostsView.FolderMode == "auto" && _hostPaneOpen) SetHostPaneOpen(false); };
        HostPaneSplitter.DragCompleted += (_, _) =>
        {
            if (HostsView.FolderMode != "pinned") return;
            AppSettings.Current.HostsDockWidth = Math.Round(HostPaneCol.ActualWidth);
            try { AppSettings.Save(); } catch (Exception ex) { App.Log("Hosts panel width", ex); }
        };
        ApplyHostPane();
    }

    /// <summary>The folded panel's tab: the hosts icon and a small "Hosts" written upwards; its edge takes the accent colour
    /// while pointed at.</summary>
    private void BuildHostStrip()
    {
        var icon = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        if (Icons.For("▣") is { } c) { icon.Text = c.ToString(); icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont"); icon.Fs(FontStep.Caption); }
        else icon.Text = "▣";
        var label = new TextBlock { Text = "Hosts", HorizontalAlignment = HorizontalAlignment.Center, LayoutTransform = new RotateTransform(-90) }.Fs(FontStep.Small);
        foreach (var t in new[] { icon, label }) t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        HostPaneStrip.Child = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), Children = { icon, label } };
        void Hot(bool on)
        {
            HostPaneStrip.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Line");
            foreach (var t in new[] { icon, label }) t.SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "Muted");
        }
        HostPaneStrip.MouseEnter += (_, _) => Hot(true);
        HostPaneStrip.MouseLeave += (_, _) => Hot(false);
    }

    private static double HostPaneWidth
    {
        get
        {
            double w = AppSettings.Current.HostsDockWidth;
            if (w <= 0 || Math.Abs(w - 640) < 0.5 || Math.Abs(w - 260) < 0.5) w = 230;   // the old host-pane widths
            return Math.Clamp(w, 160, 520);
        }
    }

    /// <summary>Lays the panel out for the current mode (also when the setting changes).</summary>
    internal void ApplyHostPane()
    {
        bool auto = HostsView.FolderMode == "auto";
        _hostPaneClose.Stop();
        if (!auto) _hostPaneOpen = false;
        HostPaneStrip.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        HostPaneSplitter.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        if (auto)
        {
            // The strip and (when open) the panel beside it, in one column that widens: never over a terminal.
            HostPaneCol.MinWidth = 0;
            HostPaneCol.Width = new GridLength(_hostPaneOpen ? HostStripWidth + HostPaneWidth : HostStripWidth);
            HostPaneHost.Margin = new Thickness(HostStripWidth, 0, 0, 0);
            HostPaneHost.Visibility = _hostPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            HostPaneCol.MinWidth = 140;
            HostPaneCol.Width = new GridLength(HostPaneWidth);
            HostPaneHost.Margin = new Thickness(0);
            HostPaneHost.Visibility = Visibility.Visible;
        }
        _hostsPage?.ApplyFolderPane();   // the 📌 / 📍 on the panel's header (once the page is made)
    }

    /// <summary>Opens the auto-collapsing panel (or folds it back). Does nothing while pinned.</summary>
    internal void SetHostPaneOpen(bool open)
    {
        if (HostsView.FolderMode != "auto" || open == _hostPaneOpen) return;
        _hostPaneOpen = open;
        if (!open) _hostPaneClose.Stop();
        HostPaneCol.Width = new GridLength(open ? HostStripWidth + HostPaneWidth : HostStripWidth);
        HostPaneHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The Hosts tab's content: the one page, taken out of a tab it was in before (closing a tab leaves the page
    /// as that tab's content).</summary>
    private HostsView HostsPageForTab()
    {
        var page = HostsPage;
        if (LogicalTreeHelper.GetParent(page) is ContentControl old) old.Content = null;
        return page;
    }
}
