using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The Hosts page's folder pane, pinned or auto-collapsing INSIDE the page (0.6.2, ADR-080; DarkCipher0x: "i only want it to be
/// auto collapsing etc INSIDE the tab/window of hosts... so like it was before, but able to collapse within itself").
/// SecureCRT's push-pin, on the pane's own header (left):
/// <list type="bullet">
/// <item>📌 <b>pinned</b> (default): the pane has its column beside the host list, resizable, as always.</item>
/// <item>📍 <b>auto</b>: the column shrinks to a narrow "FOLDERS" strip. Pointing at the strip (or clicking it, or dragging
/// hosts onto it) slides the pane out over the host list; it slides back shortly after the pointer leaves it, or when you
/// click into the list.</item>
/// </list>
/// <see cref="AppSettings.HostsDock"/> holds the mode (a Group Policy: pinned / auto); <see cref="AppSettings.HostsDockWidth"/>
/// the pane's width. Nothing outside this page changes: sessions open in their tabs as before.
/// </summary>
public partial class HostsView
{
    private const double FolderStripWidth = 22;
    private bool _folderOpen;
    private DispatcherTimer? _folderClose;

    /// <summary>"pinned" or "auto" (0.5.8–0.6.1's "tab" and anything else read as pinned).</summary>
    internal static string FolderMode => AppSettings.Current.HostsDock == "auto" ? "auto" : "pinned";
    /// <summary>The pane is showing (always when pinned; slid out when auto).</summary>
    internal bool FolderPaneOpen => FolderMode == "pinned" || _folderOpen;

    private static double FolderWidth
    {
        get
        {
            double w = AppSettings.Current.HostsDockWidth;
            if (w <= 0 || Math.Abs(w - 640) < 0.5 || Math.Abs(w - 260) < 0.5) w = 230;   // the old host-pane widths
            return Math.Clamp(w, 140, 520);
        }
    }

    /// <summary>Wires the strip, the pane and the splitter (from the constructor).</summary>
    private void InitFolderPane()
    {
        _folderClose = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _folderClose.Tick += (_, _) =>
        {
            // Stay open while the pointer is back on it, a menu of it holds the mouse, or a drag is in progress.
            if (FolderHost.IsMouseOver || FolderStrip.IsMouseOver || Mouse.Captured != null || Mouse.LeftButton == MouseButtonState.Pressed) return;
            _folderClose.Stop();
            SetFolderOpen(false);
        };
        FolderStrip.MouseLeftButtonUp += (_, _) => SetFolderOpen(true);
        BuildFolderStrip();
        FolderStrip.MouseEnter += (_, _) => SetFolderOpen(true);
        FolderStrip.DragEnter += (_, _) => SetFolderOpen(true);   // drag hosts onto a folder while collapsed
        FolderHost.MouseEnter += (_, _) => _folderClose.Stop();
        FolderHost.MouseLeave += (_, _) => { if (FolderMode == "auto" && _folderOpen) _folderClose.Start(); };
        FolderHost.DragLeave += (_, _) => { if (FolderMode == "auto" && _folderOpen) _folderClose.Start(); };
        List.PreviewMouseDown += (_, _) => { if (FolderMode == "auto" && _folderOpen) SetFolderOpen(false); };
        FolderSplitter.DragCompleted += (_, _) =>
        {
            if (FolderMode != "pinned") return;
            AppSettings.Current.HostsDockWidth = Math.Round(FolderCol.ActualWidth);
            try { AppSettings.Save(); } catch (Exception ex) { App.Log("Hosts folder pane width", ex); }
        };
        Loaded += (_, _) => ApplyFolderPane();
        ApplyFolderPane();
    }

    /// <summary>The collapsed pane's tab: the hosts icon and a small "Hosts" written upwards, flush with the page; no button
    /// frame. Its edge line takes the accent colour while pointed at.</summary>
    private void BuildFolderStrip()
    {
        var icon = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        if (Icons.For("▣") is { } c) { icon.Text = c.ToString(); icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont"); icon.Fs(FontStep.Caption); }
        else icon.Text = "▣";
        var label = new TextBlock { Text = "Hosts", HorizontalAlignment = HorizontalAlignment.Center, LayoutTransform = new RotateTransform(-90) }.Fs(FontStep.Small);
        foreach (var t in new[] { icon, label }) t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        FolderStrip.Child = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), Children = { icon, label } };
        void Hot(bool on)
        {
            FolderStrip.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Line");
            FolderStrip.SetResourceReference(Border.BackgroundProperty, on ? "Hover" : "Bg");
            foreach (var t in new[] { icon, label }) t.SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "Muted");
        }
        FolderStrip.Background = Brushes.Transparent;
        FolderStrip.MouseEnter += (_, _) => Hot(true);
        FolderStrip.MouseLeave += (_, _) => { Hot(false); FolderStrip.Background = Brushes.Transparent; };
        FolderStrip.BorderThickness = new Thickness(0, 0, 2, 0);
    }

    /// <summary>Lays the pane out for the current mode.</summary>
    internal void ApplyFolderPane()
    {
        bool auto = FolderMode == "auto";
        bool managed = PolicySettings.Get(nameof(AppSettings.HostsDock)) != null;
        PinButton.Content = auto ? "📍" : "📌";
        PinButton.IsEnabled = !managed;
        PinButton.ToolTip = managed ? "🔒 Set by your organisation (Group Policy: HostsDock)."
            : auto ? "Auto-collapse: the folder pane folds to a strip on the left of this page and slides out when you point at it. Click to pin it open."
                   : "Pinned: the folder pane stays open beside the hosts. Click to auto-collapse it to a strip (it slides out when you point at it).";
        System.Windows.Automation.AutomationProperties.SetName(PinButton, auto ? "Pin the folder pane open" : "Auto-collapse the folder pane");
        FolderStrip.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        FolderSplitter.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        if (auto)
        {
            FolderCol.MinWidth = 0;
            FolderCol.Width = new GridLength(FolderStripWidth);
            Grid.SetColumnSpan(FolderHost, 3);
            FolderHost.HorizontalAlignment = HorizontalAlignment.Left;
            FolderHost.Width = FolderWidth;
            FolderHost.Margin = new Thickness(FolderStripWidth, 0, 0, 0);
            FolderHost.Padding = new Thickness(6, 0, 6, 0);
            Panel.SetZIndex(FolderHost, 10);
            // A solid surface with an edge line, no drop shadow: an effect renders the pane as a bitmap and blurs its text
            // (DarkCipher0x: "looks blurry").
            FolderHost.SetResourceReference(Border.BackgroundProperty, "Surface");
            FolderHost.SetResourceReference(Border.BorderBrushProperty, "Line");
            FolderHost.BorderThickness = new Thickness(0, 0, 1, 0);
            FolderHost.Visibility = _folderOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _folderOpen = false;
            _folderClose?.Stop();
            Grid.SetColumnSpan(FolderHost, 1);
            FolderHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            FolderHost.Width = double.NaN;
            FolderHost.Margin = new Thickness(0);
            FolderHost.Padding = new Thickness(0);
            Panel.SetZIndex(FolderHost, 0);
            FolderHost.Background = Brushes.Transparent;   // pinned: exactly as before, part of the page
            FolderHost.BorderThickness = new Thickness(0);
            FolderHost.Visibility = Visibility.Visible;
            FolderCol.MinWidth = 120;
            FolderCol.Width = new GridLength(FolderWidth);
        }
    }

    /// <summary>Slides the auto-collapsing pane out (or back). Does nothing while pinned.</summary>
    internal void SetFolderOpen(bool open)
    {
        if (FolderMode != "auto" || open == _folderOpen) return;
        _folderOpen = open;
        if (!open) _folderClose?.Stop();
        // 0.6.7: a terminal (WebView2) or desktop (ActiveX) is a native window WPF can't draw over ("airspace"), so over a
        // session the pane opens beside it (the column widens) instead of on top; over the host list it slides over.
        bool beside = open && _listTab != null && !_listTab.IsSelected;
        FolderCol.Width = new GridLength(beside ? FolderStripWidth + FolderWidth : FolderStripWidth);
        FolderHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Pinned or auto-collapsing, for every open Hosts page (refused when Group Policy sets it).</summary>
    internal static void SetFolderMode(string mode)
    {
        if (PolicySettings.Get(nameof(AppSettings.HostsDock)) != null) { MainWindow.Instance?.Status("🔒 The folder pane's mode is set by your organisation (Group Policy: HostsDock)."); return; }
        AppSettings.Current.HostsDock = mode == "auto" ? "auto" : "pinned";
        try { AppSettings.Save(); } catch (Exception ex) { App.Log("Hosts folder pane mode", ex); }
        foreach (var hv in MainWindow.Instance?.Panes.AllTabs.Select(t => t.Content).OfType<HostsView>() ?? Enumerable.Empty<HostsView>()) { hv._folderOpen = false; hv.ApplyFolderPane(); }
        MainWindow.Instance?.Status(mode == "auto" ? "The folder pane auto-collapses: point at the FOLDERS strip to slide it out." : "The folder pane is pinned open.");
    }

    private void OnPinToggle(object sender, RoutedEventArgs e) => SetFolderMode(FolderMode == "auto" ? "pinned" : "auto");
}
