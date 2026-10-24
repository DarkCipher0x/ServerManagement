using System.Windows;
using System.Windows.Controls;

// What modules contribute to the dashboard, the Privacy & network report, Settings and the tab menu (0.2.0 phase 6f).
// These were nested in the app's Overview / PrivacyReport / SettingsView; they live in the UI kit with the module
// contracts (AppModules.cs) so a module project (the AI) can contribute without referencing the app. The app's
// screens read the registries here.

namespace DcxManager.Services
{
    /// <summary>What a module's dashboard check found: a line of text, or the error to show (memory only).</summary>
    public sealed record CheckOutcome(string? Text, string? Error);

    /// <summary>A summary tile's value, detail line and alert state.</summary>
    public sealed record TileText(string Value, string Detail, bool Alert = false);

    /// <summary>A remote source contributed by a module (0.2.0 phase 3): listed with the dashboard's checks between the
    /// cloud accounts and the infrastructure providers while <see cref="Host"/> returns a host (null = nothing to check
    /// now), and drawn as a tile on the summary row by <see cref="Tile"/> (last outcome or null, "when" text).</summary>
    public sealed record ModuleSource(string Module, string Id, string Title, Func<string?> Host, Func<Task<CheckOutcome>> Check,
        Func<CheckOutcome?, string, TileText> Tile, double TileWidth = 170);

    /// <summary>The dashboard sources the modules contributed, in registration order.</summary>
    public static class DashboardSources
    {
        private static readonly List<ModuleSource> Contributed = new();
        public static IReadOnlyList<ModuleSource> All => Contributed.ToList();
        public static void Add(ModuleSource s) { if (Contributed.All(x => x.Id != s.Id)) Contributed.Add(s); }
        internal static void ResetForTest() => Contributed.Clear();
    }

    /// <summary>A Privacy &amp; network row's state.</summary>
    public enum PrivacyLevel { Good, Info, Warn }

    /// <summary>A security-posture row of the Privacy &amp; network report.</summary>
    public sealed record PrivacyPosture(string Name, string Value, PrivacyLevel Level, string? Detail = null);

    /// <summary>A destination the app is configured to reach, with its network class.</summary>
    public sealed record PrivacyEndpoint(string Service, string Destination, NetClass? Class, string State, string Purpose);

    /// <summary>Rows a module adds to the report (0.2.0 phase 3): posture rows after "Data location", endpoints first.</summary>
    public sealed record PrivacyContribution(string Module, Func<IEnumerable<PrivacyPosture>> Posture, Func<IEnumerable<PrivacyEndpoint>> Endpoints);

    /// <summary>The Privacy &amp; network rows the modules contributed, and the report's dashboard notes (registered by the app).</summary>
    public static class PrivacyRows
    {
        private static readonly List<PrivacyContribution> Contributed = new();
        public static IReadOnlyList<PrivacyContribution> All => Contributed.ToList();
        public static void Add(PrivacyContribution c) => Contributed.Add(c);
        internal static void ResetForTest() => Contributed.Clear();

        /// <summary>The report's note on a dashboard source (by id), registered by the app (<c>PrivacyReport.DashboardNote</c>).</summary>
        public static Func<string, string> DashboardNote { get; set; } = _ => "";
    }
}

namespace DcxManager.Views
{
    /// <summary>The Settings screen's card builder, as module cards use it (their bodies are built when Settings opens).
    /// The app's Settings screen implements it.</summary>
    public interface ISettingsKit
    {
        Border Card(string title, string hint, params UIElement[] children);
        TextBlock L(string text);
        WrapPanel Row(params UIElement[] items);
        Button Btn(string text, Action action, string? style = null);
        StackPanel Labeled(string label, FrameworkElement element);
        CheckBox Check(string text, bool value, Action<bool> set);
        /// <summary>A switch for an admin-protected setting (0.3.0 §1): changes go through the admin gate (UAC).</summary>
        CheckBox ProtectedCheck(string key, string text, Func<bool, Task<bool>>? change = null, Func<bool>? current = null);
        /// <summary>Writes the settings and refreshes what depends on them.</summary>
        void Save();
        /// <summary>The settings being edited.</summary>
        Services.AppSettings Settings { get; }
        object FindResource(object resourceKey);
        /// <summary>The Settings screen was shown / taken down (a card subscribes to live state only while shown).</summary>
        event RoutedEventHandler Loaded;
        event RoutedEventHandler Unloaded;
    }

    /// <summary>Context of the "tab" menu surface (a document tab's right-click menu): the tab and what kind of screen it is.</summary>
    public sealed record TabMenuContext(TabItem Tab, string ScreenName);
}
