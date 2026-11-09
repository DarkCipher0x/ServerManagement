using System.Windows.Input;

namespace DcxManager.Services.Keys;

/// <summary>Where a command's keys work. The app's own (<see cref="App"/>) work in the main window and every pop-out
/// window; the others only while that kind of screen has the keyboard.</summary>
public static class KeyScopes
{
    public const string App = "app";
    public const string Portal = "portal";
    public const string Notes = "notes";
    public const string Page = "page";
    public const string Editor = "editor";

    /// <summary>Screens inside the app's windows: the app's own keys reach the window first, so they win there.</summary>
    public static bool InsideAppWindows(string scope) => scope is not Editor;

    /// <summary>Whether a key bound in both scopes would collide.</summary>
    public static bool Overlap(string a, string b) =>
        a == b || a == App && InsideAppWindows(b) || b == App && InsideAppWindows(a);

    public static string Title(string scope) => scope switch
    {
        App => "everywhere in the app",
        Portal => "in a portal tab",
        Notes => "in the notes editor",
        Page => "on the page that has the keyboard",
        Editor => "in the text and hex editors",
        _ => scope,
    };
}

/// <summary>One thing a key can do. View commands (<see cref="Run"/> null) are carried out by their screen, which asks
/// <see cref="KeyCommands.Matches"/>; app commands run here.</summary>
public sealed record KeyCommand(string Id, string Title, string Category, string Scope, IReadOnlyList<KeyChord> Defaults)
{
    public Action? Run { get; init; }
    /// <summary>When false the key isn't taken, so it reaches the screen as if unbound (e.g. Reconnect with no terminal).</summary>
    public Func<bool>? CanRun { get; init; }
    /// <summary>A line of help on the Keyboard page and in the command palette.</summary>
    public string? Detail { get; init; }
    /// <summary>Listed in the command palette (app commands only).</summary>
    public bool InPalette { get; init; } = true;
    /// <summary>The module that registered it (null: the app itself).</summary>
    public string? Module { get; init; }
}

/// <summary>
/// Every keyboard shortcut of the app in one place (0.6.36, ADR-115; DarkCipher0x: "full keyboard control … with a dedicated
/// keybindings page in the settings"). Commands are registered with their default keys; the keys in force are the
/// defaults, then the user's changes (<see cref="AppSettings.KeyBindings"/>, encrypted), and Group Policy can supply
/// that list or switch changes off (<see cref="AppSettings.KeyBindingsAllowChange"/>). The main window sends each key
/// here (<see cref="TryRun(KeyEventArgs, bool)"/>); screens ask about their own (<see cref="Matches"/>). Nothing runs
/// that isn't a registered command, and a key can only start what its menu item or button would.
/// </summary>
public static class KeyCommands
{
    private static readonly List<KeyCommand> _commands = new();
    private static int _version;
    private static (int Version, int Signature)? _built;
    private static Dictionary<string, KeyChord[]> _effective = new(StringComparer.Ordinal);
    private static Dictionary<(string Scope, KeyChord Chord), string> _index = new();
    private static IReadOnlyList<string> _problems = Array.Empty<string>();

    /// <summary>Raised after the keys in force change (a binding saved, reset or imported).</summary>
    public static event Action? Changed;

    // ---- registration ---------------------------------------------------------------------------------------------

    /// <summary>Adds a command, or replaces the one with the same id. Default keys that are malformed or not allowed are a
    /// programming error and throw, so a bad default can't ship (the self-test registers every command).</summary>
    public static KeyCommand Register(KeyCommand command)
    {
        if (!KeyBindingList.ValidId(command.Id)) throw new ArgumentException($"Bad command id “{command.Id}”.");
        foreach (var c in command.Defaults)
            if (c.Problem() is { } why) throw new ArgumentException($"{command.Id}: default {c.Shown}: {why}.");
        int at = _commands.FindIndex(x => x.Id == command.Id);
        if (at >= 0) _commands[at] = command; else _commands.Add(command);
        _version++;
        return command;
    }

    /// <summary>Default keys from text ("Ctrl+W", "Ctrl+Shift+W"); none gives a command without keys. Malformed text
    /// throws (a programming error).</summary>
    public static KeyChord[] Keys(params string[] defaults) => defaults.Select(Chord).ToArray();

    /// <summary>An app command (it runs here when its key is pressed).</summary>
    public static KeyCommand Register(string id, string title, string category, Action run, params string[] defaults) =>
        Register(new KeyCommand(id, title, category, KeyScopes.App, Keys(defaults)) { Run = run });

    /// <summary>A screen's command: its keys are looked up by the screen itself.</summary>
    public static KeyCommand RegisterView(string id, string title, string category, string scope, params string[] defaults) =>
        Register(new KeyCommand(id, title, category, scope, Keys(defaults)) { InPalette = false });

    /// <summary>Removes the commands that match (pages that are gone, a module's commands in the self-test).</summary>
    public static void RemoveWhere(Func<KeyCommand, bool> match)
    {
        if (_commands.RemoveAll(c => match(c)) > 0) _version++;
    }

    internal static void ResetModulesForTest() => RemoveWhere(c => c.Module != null);

    private static KeyChord Chord(string text) =>
        KeyChord.TryParse(text, out var c, out var err) ? c : throw new ArgumentException($"Bad default key “{text}”: {err}.");

    public static IReadOnlyList<KeyCommand> All => _commands;
    public static KeyCommand? Find(string id) => _commands.FirstOrDefault(c => c.Id == id);

    // ---- the keys in force ----------------------------------------------------------------------------------------

    /// <summary>Whether shortcuts may be changed here (not when Group Policy sets the list or switches changes off).</summary>
    public static bool CanEdit => WhyLocked == null;

    public static string? WhyLocked =>
        PolicySettings.IsManaged(nameof(AppSettings.KeyBindings)) ? "Your organisation sets the keyboard shortcuts (Group Policy)."
        : !AppSettings.Current.KeyBindingsAllowChange ? (PolicySettings.IsManaged(nameof(AppSettings.KeyBindingsAllowChange)) ? "Your organisation keeps the default keyboard shortcuts (Group Policy)." : "Changing keyboard shortcuts is switched off.")
        : null;

    /// <summary>Lines of the stored list (or the policy's) that were skipped, and keys bound twice.</summary>
    public static IReadOnlyList<string> Problems { get { Build(); return _problems; } }

    /// <summary>The keys in force for a command (empty: none).</summary>
    public static IReadOnlyList<KeyChord> Effective(string id)
    {
        Build();
        return _effective.TryGetValue(id, out var k) ? k : Array.Empty<KeyChord>();
    }

    /// <summary>The first key in force, as shown in menus ("Ctrl+Shift+P"), or null.</summary>
    public static string? Text(string id) => Effective(id) is { Count: > 0 } k ? k[0].Shown : null;

    /// <summary>Every key in force, "Ctrl+W or Ctrl+Shift+W", or "no key".</summary>
    public static string TextAll(string id) => Effective(id) is { Count: > 0 } k ? string.Join(" or ", k.Select(c => c.Shown)) : "no key";

    /// <summary>Whether the user (or policy) changed this command's keys.</summary>
    public static bool IsChanged(string id) => Overrides().ContainsKey(id);

    private static IReadOnlyDictionary<string, KeyChord[]> Overrides() => KeyBindingList.Parse(AppSettings.Current.KeyBindings).Overrides;

    private static int Signature()
    {
        var list = AppSettings.Current.KeyBindings;
        var h = new HashCode();
        h.Add(list.Count);
        foreach (var l in list) h.Add(l, StringComparer.Ordinal);
        return h.ToHashCode();
    }

    /// <summary>Works the keys out again when a command was registered or the stored list changed (the list is short:
    /// this costs microseconds per key press).</summary>
    private static void Build()
    {
        var sig = Signature();
        if (_built is { } b && b.Version == _version && b.Signature == sig) return;
        var parsed = KeyBindingList.Parse(AppSettings.Current.KeyBindings);
        var problems = parsed.Problems.ToList();
        var effective = new Dictionary<string, KeyChord[]>(StringComparer.Ordinal);
        var index = new Dictionary<(string, KeyChord), string>();
        foreach (var c in _commands)
        {
            var keys = parsed.Overrides.TryGetValue(c.Id, out var o) ? o : c.Defaults.ToArray();
            var kept = new List<KeyChord>();
            foreach (var k in keys)
            {
                // A key already taken where this command works stays with the first (listed so it can be fixed).
                var clash = index.FirstOrDefault(x => x.Key.Item2 == k && KeyScopes.Overlap(x.Key.Item1, c.Scope));
                if (clash.Value != null) { problems.Add($"{k.Shown} is bound to both “{Find(clash.Value)?.Title}” and “{c.Title}”: the first keeps it"); continue; }
                index[(c.Scope, k)] = c.Id;
                kept.Add(k);
            }
            effective[c.Id] = kept.ToArray();
        }
        _effective = effective; _index = index; _problems = problems;
        _built = (_version, sig);
    }

    // ---- pressing a key -------------------------------------------------------------------------------------------

    /// <summary>The command in <paramref name="scope"/> bound to this key, or null.</summary>
    public static KeyCommand? Match(KeyChord chord, string scope)
    {
        Build();
        return _index.TryGetValue((scope, chord), out var id) ? Find(id) : null;
    }

    /// <summary>For screens: whether this key press is bound to <paramref name="id"/> right now.</summary>
    public static bool Matches(KeyEventArgs e, string id)
    {
        if (Recording(e) || KeyChord.FromEvent(e) is not { } chord) return false;
        Build();
        return _effective.TryGetValue(id, out var k) && k.Contains(chord);
    }

    /// <summary>Runs the app command bound to this key press. While a terminal or remote desktop has the keyboard only keys
    /// that <see cref="KeyChord.WorksInRemoteSessions"/> are taken; the rest go to the remote program.</summary>
    public static bool TryRun(KeyEventArgs e, bool remoteHasKeyboard) =>
        !Recording(e) && KeyChord.FromEvent(e) is { } chord && !Views.ScreenKeys.Claimed(e, chord) && TryRun(chord, remoteHasKeyboard);

    /// <summary>A key going to a <see cref="Views.KeyCaptureBox"/> is being recorded, not run.</summary>
    private static bool Recording(KeyEventArgs e) => Keyboard.FocusedElement is Views.KeyCaptureBox || e.OriginalSource is Views.KeyCaptureBox;

    public static bool TryRun(KeyChord chord, bool remoteHasKeyboard)
    {
        if (remoteHasKeyboard && !chord.WorksInRemoteSessions) return false;
        if (Match(chord, KeyScopes.App) is not { Run: { } run } cmd) return false;
        try { if (cmd.CanRun?.Invoke() == false) return false; }
        catch (Exception ex) { Log.Write("Shortcut " + cmd.Id, ex); return false; }
        try { run(); }
        catch (Exception ex) { Log.Write("Shortcut " + cmd.Id, ex); }
        return true;
    }

    // ---- changing keys --------------------------------------------------------------------------------------------

    /// <summary>The other commands that hold <paramref name="chord"/> where <paramref name="id"/> works.</summary>
    public static IReadOnlyList<KeyCommand> Conflicts(string id, KeyChord chord)
    {
        var me = Find(id);
        if (me == null) return Array.Empty<KeyCommand>();
        Build();
        return _commands.Where(c => c.Id != id && KeyScopes.Overlap(c.Scope, me.Scope) && _effective.TryGetValue(c.Id, out var k) && k.Contains(chord)).ToList();
    }

    /// <summary>Gives a command these keys (none: no keys). With <paramref name="takeFromOthers"/> a key another command
    /// holds moves here; otherwise a clash is refused. Returns why it was refused, or null. Changes the settings in
    /// memory only: the caller saves them.</summary>
    public static string? Set(string id, IReadOnlyList<KeyChord> chords, bool takeFromOthers = false)
    {
        if (WhyLocked is { } locked) return locked;
        var cmd = Find(id);
        if (cmd == null) return "There is no such command.";
        var list = chords.Distinct().ToList();
        if (list.Count > KeyBindingList.MaxChordsPerCommand) return $"A command can have at most {KeyBindingList.MaxChordsPerCommand} keys.";
        foreach (var c in list) if (c.Problem() is { } why) return $"{c.Shown}: {why}.";
        var overrides = Overrides().ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal);
        foreach (var c in list)
        {
            var others = Conflicts(id, c);
            if (others.Count == 0) continue;
            if (!takeFromOthers) return $"{c.Shown} is already “{others[0].Title}”.";
            foreach (var o in others) overrides[o.Id] = Effective(o.Id).Where(k => k != c).ToArray();
        }
        overrides[id] = list.ToArray();
        // A command back on its defaults needs no line.
        foreach (var o in overrides.Where(o => Find(o.Key) is { } x && x.Defaults.SequenceEqual(o.Value)).Select(o => o.Key).ToList()) overrides.Remove(o);
        if (overrides.Count > KeyBindingList.MaxEntries) return "Too many changed shortcuts.";
        AppSettings.Current.KeyBindings = KeyBindingList.Lines(overrides);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Puts one command back on its default keys (a default another command took is not taken back).</summary>
    public static string? Reset(string id)
    {
        if (WhyLocked is { } locked) return locked;
        var cmd = Find(id);
        if (cmd == null) return "There is no such command.";
        var overrides = Overrides().ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal);
        foreach (var c in cmd.Defaults)
            if (Conflicts(id, c).FirstOrDefault() is { } holder) return $"{c.Shown} is now “{holder.Title}”: change that one first, or reset everything.";
        overrides.Remove(id);
        AppSettings.Current.KeyBindings = KeyBindingList.Lines(overrides);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Every command back on its default keys.</summary>
    public static string? ResetAll()
    {
        if (WhyLocked is { } locked) return locked;
        AppSettings.Current.KeyBindings = new List<string>();
        Changed?.Invoke();
        return null;
    }

    /// <summary>Tells listeners the stored list was replaced from outside (an import, a restore).</summary>
    public static void NotifyChanged() => Changed?.Invoke();
}
