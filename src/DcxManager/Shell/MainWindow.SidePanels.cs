using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

// MainWindow: the side-panel column (ADR-117) between the documents and the AI dock. A module's panel (the chat's pinned
// room) shows here beside whatever tab is open, so you can chat while you work in a terminal. One panel at a time; its
// width is dragged with the splitter, never more than 40 % of the window so the documents keep room. 0.6.38: the width and
// whether the panel is open are kept in the encrypted settings, and an open panel comes back at the next start.
public sealed partial class MainWindow
{
    public const double PanelDefaultWidth = 360, PanelMinWidth = 260, PanelMaxWidth = 720;
    private string? _panelId;
    private double _panelWidth = AppSettings.Current.SidePanelWidth is > 0 and var w ? w : PanelDefaultWidth;

    private void InitSidePanels()
    {
        ModulePanels.Show = ShowSidePanel;
        ModulePanels.Hide = HideSidePanel;
        ModulePanels.Shown = () => _panelId;
        PanelSplitter.DragCompleted += (_, _) =>
        {
            if (_panelId == null) return;
            _panelWidth = FitPanelWidth(PanelCol.ActualWidth, ActualWidth);
            PanelCol.Width = new GridLength(_panelWidth);
            SavePanelState();
        };
        SizeChanged += (_, _) => { if (_panelId != null) PanelCol.Width = new GridLength(FitPanelWidth(_panelWidth, ActualWidth)); };
    }

    /// <summary>The panel open at the last exit comes back (once the modules are ready), without taking the focus.</summary>
    private void RestoreSidePanel()
    {
        if (AppSettings.Current.SidePanelShown is { Length: > 0 } id && AppModules.FindSidePanel(id) != null) ShowSidePanel(id, focus: false);
    }

    private bool _exiting;

    private void SavePanelState()
    {
        if (_exiting) return;   // the panel closes with the window at exit: it stays "open" for the next start
        var s = AppSettings.Current;
        string shown = _panelId ?? "";
        if (s.SidePanelShown == shown && Math.Abs(s.SidePanelWidth - _panelWidth) < 0.5) return;
        s.SidePanelShown = shown;
        s.SidePanelWidth = Math.Round(_panelWidth);
        try { AppSettings.Save(); } catch (Exception ex) { Services.Log.Write("Side panel state", ex); }
    }

    /// <summary>The panel's width for a window: the saved width within the limits, at most 40 % of the window.</summary>
    public static double FitPanelWidth(double saved, double window) =>
        Math.Max(PanelMinWidth, Math.Min(Math.Min(PanelMaxWidth, Math.Max(saved, PanelMinWidth)), window > 0 ? window * 0.40 : PanelMaxWidth));

    private void ShowSidePanel(string id) => ShowSidePanel(id, focus: true);

    private void ShowSidePanel(string id, bool focus)
    {
        if (AppModules.FindSidePanel(id) is not { } panel) return;
        if (_panelId == id && PanelHost.Child is FrameworkElement shown) { if (focus) FocusPanel(shown); return; }
        CloseSidePanelContent();
        FrameworkElement content;
        try { content = panel.Make(); }
        catch (Exception ex) { Services.Log.Write("Side panel " + id, ex); return; }

        var title = new TextBlock { Text = panel.Title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = "✕", ToolTip = "Close the panel", Padding = new Thickness(6, 0, 6, 0), Style = (Style)FindResource("SmallButton") };
        System.Windows.Automation.AutomationProperties.SetName(close, "Close the " + panel.Title + " panel");
        close.Click += (_, _) => HideSidePanel();
        var head = new DockPanel { Margin = new Thickness(10, 8, 8, 6) };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close); head.Children.Add(title);
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        content.Margin = new Thickness(10, 0, 10, 10);
        root.Children.Add(content);
        root.KeyDown += (_, e) => { if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.Shift) { e.Handled = true; HideSidePanel(); } };
        System.Windows.Automation.AutomationProperties.SetName(root, panel.Title + " panel");

        _panelId = id;
        PanelHost.Child = root;
        PanelHost.Tag = content;
        PanelHost.Visibility = Visibility.Visible;
        PanelSplitter.Visibility = Visibility.Visible;
        PanelCol.MinWidth = PanelMinWidth;
        PanelCol.Width = new GridLength(FitPanelWidth(_panelWidth, ActualWidth));
        if (focus) FocusPanel(content);
        SavePanelState();
    }

    private void FocusPanel(FrameworkElement content) => Dispatcher.BeginInvoke(() => content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), System.Windows.Threading.DispatcherPriority.Input);

    private void HideSidePanel()
    {
        if (_panelId == null) return;
        if (PanelCol.ActualWidth > 0) _panelWidth = FitPanelWidth(PanelCol.ActualWidth, ActualWidth);
        CloseSidePanelContent();
        _panelId = null;
        PanelHost.Visibility = Visibility.Collapsed;
        PanelSplitter.Visibility = Visibility.Collapsed;
        PanelCol.MinWidth = 0;
        PanelCol.Width = new GridLength(0);
        SavePanelState();
    }

    /// <summary>At exit: the panel's content is closed, but the panel is remembered as open.</summary>
    private void CloseSidePanelAtExit()
    {
        if (_panelId != null && PanelCol.ActualWidth > 0) { _panelWidth = FitPanelWidth(PanelCol.ActualWidth, ActualWidth); SavePanelState(); }
        _exiting = true;
        HideSidePanel();
    }

    private void CloseSidePanelContent()
    {
        if (PanelHost.Tag is IDisposable d) { try { d.Dispose(); } catch (Exception ex) { Services.Log.Write("Side panel close", ex); } }
        PanelHost.Tag = null;
        PanelHost.Child = null;
    }
}
