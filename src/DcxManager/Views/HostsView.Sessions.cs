using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Sessions inside the Hosts tab (0.6.7, ADR-086; DarkCipher0x: "the terminal windows you open, should open within the tab... so
/// that you can still pop out the hosts while viewing the terminal ... give the options to change it"). With
/// <see cref="AppSettings.HostsOpenIn"/> = "hosts" (default) the area right of the folder pane is a document area of its
/// own (<see cref="PaneHost.AttachEmbedded"/>): its first, fixed tab "All hosts" holds the host list, and whatever you
/// connect from this page (Connect, double-click, Files, Manage, Remote Desktop, VNC, File commander) opens as a tab in
/// there, so the folder pane (pinned, or the slim Hosts tab that slides out) stays beside the session. While a session tab
/// is shown, the page's title and toolbar step aside to give it the room. The tabs split, drag out to the main window or
/// another window, and "switch to the open tab" finds them like any other. "tab" puts sessions in separate tabs, as
/// before. Closing the Hosts tab moves its sessions out to ordinary tabs; nothing is closed.
/// </summary>
public partial class HostsView
{
    private DocPane? _sessionPane;
    private TabItem? _listTab;
    private readonly Border _listHost = new();

    /// <summary>"hosts" or "tab".</summary>
    internal static string OpenIn => AppSettings.Current.HostsOpenIn == "tab" ? "tab" : "hosts";
    /// <summary>Self-test: the Hosts tab's own pane (null in "tab" mode) and its list tab.</summary>
    internal DocPane? SessionPane => _sessionPane;
    internal TabItem? ListTab => _listTab;

    private void InitSessions()
    {
        // Using the page arms "the next session opens in here"; using a session tab inside it doesn't need to.
        MouseButtonEventHandler arm = (_, _) => ArmSessions();
        ToolbarRow.PreviewMouseDown += arm;
        List.PreviewMouseDown += arm;
        FolderHost.PreviewMouseDown += arm;
        List.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) ArmSessions(); };
        SessionArea.PreviewMouseDown += (_, _) => { if (FolderMode == "auto") SetFolderOpen(false); };
        Loaded += (_, _) => ApplyOpenIn();
    }

    /// <summary>The next session opened goes to this page's area (connect actions call this; so do clicks on the page).</summary>
    internal void ArmSessions()
    {
        if (_sessionPane != null) MainWindow.Instance?.Panes.ArmSessionTarget(_sessionPane);
    }

    /// <summary>The tab that holds this page.</summary>
    private TabItem? OwnerTab => MainWindow.Instance?.Panes.AllTabs.FirstOrDefault(t => t.Content == this);

    /// <summary>Lays the page out for <see cref="OpenIn"/>: the host list in the area's fixed first tab, or on its own.</summary>
    internal void ApplyOpenIn()
    {
        if (MainWindow.Instance?.Panes is not { } panes) return;
        if (OpenIn == "hosts" && _sessionPane == null)
        {
            BodyGrid.Children.Remove(List);
            _listHost.Child = List;
            _listTab = new TabItem { Header = "☰ All hosts", Content = _listHost, Tag = "fixed", ToolTip = "Your hosts. Sessions you open from here open as tabs beside this one." };
            if (TryFindResource("DocTab") is Style st) _listTab.Style = st;
            _listTab.AddHandler(System.Windows.Controls.Primitives.Selector.SelectedEvent, new RoutedEventHandler((_, _) => SyncChrome()));
            _listTab.AddHandler(System.Windows.Controls.Primitives.Selector.UnselectedEvent, new RoutedEventHandler((_, _) => SyncChrome()));
            _sessionPane = panes.AttachEmbedded(SessionArea, () => OwnerTab);
            panes.AddTo(_listTab, _sessionPane, activate: false);
            _sessionPane.SelectedItem = _listTab;
            _sessionPane.SelectionChanged += (_, e) => { if (e.Source == _sessionPane) SyncChrome(); };
            SessionArea.Visibility = Visibility.Visible;
        }
        else if (OpenIn == "tab" && _sessionPane != null)
        {
            EndSessionArea();
        }
        SyncChrome();
    }

    /// <summary>Back to the list on its own: the area's sessions move to ordinary tabs.</summary>
    private void EndSessionArea()
    {
        if (_sessionPane == null) return;
        MainWindow.Instance?.Panes.DetachEmbedded(SessionArea);
        _listHost.Child = null;
        if (!BodyGrid.Children.Contains(List)) { Grid.SetColumn(List, 2); BodyGrid.Children.Add(List); }
        SessionArea.Visibility = Visibility.Collapsed;
        _sessionPane = null; _listTab = null;
    }

    /// <summary>The title and toolbar show with the host list, and step aside while a session is in front.</summary>
    private void SyncChrome()
    {
        bool list = _listTab == null || _listTab.IsSelected;
        HeaderRow.Visibility = ToolbarRow.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Inside the Hosts tab or separate tabs, for every open Hosts page.</summary>
    internal static void SetOpenIn(string mode)
    {
        if (PolicySettings.Get(nameof(AppSettings.HostsOpenIn)) != null) { MainWindow.Instance?.Status("🔒 Where sessions from Hosts open is set by your organisation (Group Policy: HostsOpenIn)."); return; }
        AppSettings.Current.HostsOpenIn = mode == "tab" ? "tab" : "hosts";
        try { AppSettings.Save(); } catch (Exception ex) { App.Log("Hosts: open sessions in", ex); }
        foreach (var hv in MainWindow.Instance?.Panes.AllTabs.Select(t => t.Content).OfType<HostsView>().ToList() ?? new()) hv.ApplyOpenIn();
        MainWindow.Instance?.Status(mode == "tab" ? "Sessions from Hosts open as separate tabs." : "Sessions from Hosts open inside the Hosts tab, beside the folder pane.");
    }
}
