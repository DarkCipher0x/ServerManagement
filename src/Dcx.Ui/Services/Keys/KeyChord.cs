using System.Windows.Input;

namespace DcxManager.Services.Keys;

/// <summary>
/// One key with its modifiers, such as Ctrl+Shift+P (0.6.36, ADR-115). Text comes from the encrypted settings, Group
/// Policy and appearance imports, so <see cref="TryParse"/> reads it against an allow-list only: Ctrl, Alt and Shift plus
/// one key from <see cref="Keys"/>, at most <see cref="MaxTextLength"/> characters. Anything else is refused and never
/// guessed at. The Windows key is never used: Windows owns its combinations.
/// </summary>
public readonly record struct KeyChord(Key Key, ModifierKeys Modifiers)
{
    public const int MaxTextLength = 40;
    public const ModifierKeys Supported = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

    /// <summary>Every key a binding may use: its stored name and the name shown on screen.</summary>
    private static readonly (Key Key, string Name, string Shown)[] Keys = BuildKeys();

    private static (Key, string, string)[] BuildKeys()
    {
        var list = new List<(Key, string, string)>();
        for (var k = Key.A; k <= Key.Z; k++) list.Add((k, k.ToString(), k.ToString()));
        for (int i = 0; i <= 9; i++) list.Add((Key.D0 + i, i.ToString(), i.ToString()));
        for (int i = 1; i <= 24; i++) list.Add((Key.F1 + i - 1, "F" + i, "F" + i));
        list.AddRange(new (Key, string, string)[]
        {
            (Key.Left, "Left", "←"), (Key.Right, "Right", "→"), (Key.Up, "Up", "↑"), (Key.Down, "Down", "↓"),
            (Key.Home, "Home", "Home"), (Key.End, "End", "End"), (Key.PageUp, "PageUp", "PgUp"), (Key.PageDown, "PageDown", "PgDn"),
            (Key.Insert, "Insert", "Ins"), (Key.Delete, "Delete", "Del"), (Key.Back, "Backspace", "Backspace"),
            (Key.Tab, "Tab", "Tab"), (Key.Enter, "Enter", "Enter"), (Key.Escape, "Escape", "Esc"), (Key.Space, "Space", "Space"),
            (Key.OemComma, "Comma", ","), (Key.OemPeriod, "Period", "."), (Key.OemMinus, "Minus", "-"), (Key.OemPlus, "Equals", "="),
            (Key.OemQuestion, "Slash", "/"), (Key.OemSemicolon, "Semicolon", ";"), (Key.OemQuotes, "Quote", "'"),
            (Key.OemOpenBrackets, "OpenBracket", "["), (Key.OemCloseBrackets, "CloseBracket", "]"), (Key.OemPipe, "Backslash", "\\"),
            (Key.OemBackslash, "IntlBackslash", "\\ (102nd key)"), (Key.OemTilde, "Backquote", "`"), (Key.Pause, "Pause", "Pause"),
        });
        return list.ToArray();
    }

    /// <summary>Other names accepted when reading (what people type, and the names shown on screen).</summary>
    private static readonly Dictionary<string, Key> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["←"] = Key.Left, ["→"] = Key.Right, ["↑"] = Key.Up, ["↓"] = Key.Down, ["PgUp"] = Key.PageUp, ["PgDn"] = Key.PageDown,
        ["PageDn"] = Key.PageDown, ["Ins"] = Key.Insert, ["Del"] = Key.Delete, ["Back"] = Key.Back, ["Return"] = Key.Enter,
        ["Esc"] = Key.Escape, [","] = Key.OemComma, ["."] = Key.OemPeriod, ["-"] = Key.OemMinus, ["="] = Key.OemPlus,
        ["Plus"] = Key.OemPlus, ["+"] = Key.OemPlus, ["/"] = Key.OemQuestion, [";"] = Key.OemSemicolon, ["'"] = Key.OemQuotes, ["["] = Key.OemOpenBrackets,
        ["]"] = Key.OemCloseBrackets, ["\\"] = Key.OemPipe, ["`"] = Key.OemTilde, ["Oem102"] = Key.OemBackslash,
    };

    private static readonly Dictionary<Key, (string Name, string Shown)> ByKey = Keys.ToDictionary(k => k.Key, k => (k.Name, k.Shown));
    private static readonly Dictionary<string, Key> ByName = Keys.ToDictionary(k => k.Name, k => k.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every key a binding may use (the self-test round-trips each one).</summary>
    public static IEnumerable<Key> AllowedKeys => Keys.Select(k => k.Key);

    /// <summary>Whether a binding may use this key at all.</summary>
    public static bool IsAllowedKey(Key key) => ByKey.ContainsKey(key);

    public bool IsFunctionKey => Key is >= Key.F1 and <= Key.F24;
    public bool Ctrl => (Modifiers & ModifierKeys.Control) != 0;
    public bool Alt => (Modifiers & ModifierKeys.Alt) != 0;
    public bool Shift => (Modifiers & ModifierKeys.Shift) != 0;

    /// <summary>
    /// Keys the app may take while a terminal or remote desktop has the keyboard. Remote programs use plain Ctrl+letter,
    /// Alt+letter and the F-keys (Ctrl+W deletes a word, Ctrl+B is the tmux prefix, F1 is htop's help), so only
    /// Ctrl+Shift and Ctrl+Alt combinations, Ctrl+Tab and Ctrl+F-keys are taken there (as before 0.6.36).
    /// </summary>
    public bool WorksInRemoteSessions => Ctrl && (Shift || Alt) || Modifiers == ModifierKeys.Control && (Key == Key.Tab || IsFunctionKey);

    /// <summary>The stored form: modifiers in a fixed order, then the key's name ("Ctrl+Shift+Comma").</summary>
    public string Name => Join(ByKey.TryGetValue(Key, out var n) ? n.Name : Key.ToString());

    /// <summary>The form shown on screen and in menus ("Ctrl+Shift+,").</summary>
    public string Shown => Join(ByKey.TryGetValue(Key, out var n) ? n.Shown : Key.ToString());

    public override string ToString() => Name;

    private string Join(string key) => (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + key;

    /// <summary>Reads "Ctrl+Shift+P" (case and modifier order don't matter; "Control" and the shown names are accepted).
    /// Refuses empty or overlong text, control characters, a repeated or unknown modifier, the Windows key, a missing key,
    /// more than one key and any key outside the allow-list.</summary>
    public static bool TryParse(string? text, out KeyChord chord, out string? error)
    {
        chord = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "no key"; return false; }
        if (text.Length > MaxTextLength) { error = "too long"; return false; }
        if (text.Any(char.IsControl)) { error = "control characters"; return false; }
        var t = text.Trim();
        // Split on "+", except a "+" that starts a part: "Ctrl++" is Ctrl and the + key.
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] != '+' || i == start) continue;
            parts.Add(t[start..i].Trim());
            start = i + 1;
        }
        parts.Add(t[start..].Trim());
        if (parts.Count > 4) { error = "too many parts"; return false; }
        var mods = ModifierKeys.None;
        for (int i = 0; i < parts.Count - 1; i++)
        {
            ModifierKeys? m = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" or "ctl" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "win" or "windows" or "meta" or "super" or "cmd" => null,
                _ => ModifierKeys.None,
            };
            if (m == null) { error = "the Windows key belongs to Windows"; return false; }
            if (m == ModifierKeys.None) { error = $"“{Clip(parts[i])}” is not Ctrl, Alt or Shift"; return false; }
            if ((mods & m.Value) != 0) { error = "a modifier is repeated"; return false; }
            mods |= m.Value;
        }
        var last = parts[^1];
        if (last.Length == 0) { error = "no key after the modifiers"; return false; }
        if (!ByName.TryGetValue(last, out var key) && !Aliases.TryGetValue(last, out key))
        {
            error = last.ToLowerInvariant() is "ctrl" or "control" or "alt" or "shift" ? "no key after the modifiers" : $"“{Clip(last)}” is not a key a shortcut can use";
            return false;
        }
        chord = new KeyChord(key, mods);
        return true;
    }

    public static KeyChord? Parse(string? text) => TryParse(text, out var c, out _) ? c : null;

    private static string Clip(string s) => s.Length > 16 ? s[..16] + "…" : s;

    /// <summary>The key pressed, as a chord: Alt combinations arrive as <see cref="Key.System"/>, an IME passes its key on,
    /// a modifier alone or the Windows key held gives null (nothing to bind or run).</summary>
    public static KeyChord? FromEvent(KeyEventArgs e, ModifierKeys? modifiers = null)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, Key.DeadCharProcessed => e.DeadCharProcessedKey, _ => e.Key };
        return From(key, modifiers ?? Keyboard.Modifiers);
    }

    /// <summary>A chord from a key and the modifiers held (null for a lone modifier, the Windows key or a key outside the
    /// allow-list).</summary>
    public static KeyChord? From(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Windows) != 0) return null;
        if (!IsAllowedKey(key)) return null;
        return new KeyChord(key, modifiers & Supported);
    }

    // ---- what may be bound ------------------------------------------------------------------------------------------

    /// <summary>Keys that keep their usual meaning everywhere: Windows' own, focus movement, the context menu, and text
    /// editing (copy, cut, paste, undo, redo, select all) including the terminal's Ctrl+Shift+C / Ctrl+Shift+V.</summary>
    private static readonly Dictionary<KeyChord, string> Reserved = new()
    {
        [new(Key.F4, ModifierKeys.Alt)] = "Alt+F4 closes the window",
        [new(Key.Tab, ModifierKeys.Alt)] = "Alt+Tab switches windows",
        [new(Key.Tab, ModifierKeys.Alt | ModifierKeys.Shift)] = "Alt+Shift+Tab switches windows",
        [new(Key.Escape, ModifierKeys.Alt)] = "Alt+Esc switches windows",
        [new(Key.Space, ModifierKeys.Alt)] = "Alt+Space opens the window menu",
        [new(Key.Escape, ModifierKeys.Control)] = "Ctrl+Esc opens Start",
        [new(Key.Escape, ModifierKeys.Control | ModifierKeys.Shift)] = "Ctrl+Shift+Esc opens Task Manager",
        [new(Key.Delete, ModifierKeys.Control | ModifierKeys.Alt)] = "Ctrl+Alt+Del belongs to Windows",
        [new(Key.Tab, ModifierKeys.Shift)] = "Shift+Tab moves the focus back",
        [new(Key.F10, ModifierKeys.Shift)] = "Shift+F10 opens the context menu",
        [new(Key.C, ModifierKeys.Control)] = "Ctrl+C copies",
        [new(Key.X, ModifierKeys.Control)] = "Ctrl+X cuts",
        [new(Key.V, ModifierKeys.Control)] = "Ctrl+V pastes",
        [new(Key.Z, ModifierKeys.Control)] = "Ctrl+Z undoes",
        [new(Key.Y, ModifierKeys.Control)] = "Ctrl+Y redoes",
        [new(Key.A, ModifierKeys.Control)] = "Ctrl+A selects all",
        [new(Key.Insert, ModifierKeys.Control)] = "Ctrl+Ins copies",
        [new(Key.Insert, ModifierKeys.Shift)] = "Shift+Ins pastes",
        [new(Key.Delete, ModifierKeys.Shift)] = "Shift+Del cuts",
        [new(Key.C, ModifierKeys.Control | ModifierKeys.Shift)] = "Ctrl+Shift+C copies in a terminal",
        [new(Key.V, ModifierKeys.Control | ModifierKeys.Shift)] = "Ctrl+Shift+V pastes in a terminal",
    };

    /// <summary>Why this chord can't be a shortcut, or null when it can. Without Ctrl or Alt only the F-keys (and Pause) may
    /// be used, since anything else would stop you typing or moving around a list.</summary>
    public string? Problem()
    {
        if (!IsAllowedKey(Key)) return "not a key a shortcut can use";
        if (Reserved.TryGetValue(this, out var why)) return why + ", so it can't be a shortcut";
        if (!Ctrl && !Alt && !IsFunctionKey && Key != Key.Pause)
            return Shift ? "Shift with that key types text or selects, so add Ctrl or Alt" : "that key on its own is used for typing or moving around, so add Ctrl or Alt (or use an F-key)";
        return null;
    }
}
