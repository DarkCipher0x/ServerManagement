using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The lock commands every list and the sidebar share (0.5.1, ADR-067): "🔒 Lock…" (with admin approval, Windows sign-in
/// or a password of its own), "🔓 Unlock for now", "🔒 Lock again now" and "Remove lock…" (which unlocks first), plus the
/// padlock shown next to a locked thing. Whether users may lock at all is Group Policy's call (AllowUserLocks,
/// AllowItemLocks); locks a policy sets can be opened here but never removed.
/// </summary>
public static class LockUi
{
    /// <summary>The padlock to show after a name: "🔒" closed, "🔓" open, "" not locked.</summary>
    public static string Padlock(string target) => !Locks.IsLocked(target) ? "" : Locks.IsOpen(target) ? "🔓" : "🔒";

    /// <summary>"🔒 " + name, for list rows.</summary>
    public static string Decorate(string target, string name) => Padlock(target) is { Length: > 0 } p ? p + " " + name : name;

    /// <summary>The lock submenu for <paramref name="target"/>.</summary>
    public static object? Menu(string target, string title) =>
        Menus.Sub(Locks.IsLocked(target) ? (Locks.IsOpen(target) ? "🔓 Lock (open now)" : "🔒 Lock (locked)") : "🔒 Lock", Items(target, title));

    /// <summary>The lock commands for <paramref name="target"/> (the submenu's items; Settings shows them as a drop-down).</summary>
    public static object?[] Items(string target, string title)
    {
        var own = Locks.RuleOn(target);
        var all = Locks.Governing(target);
        var why = Locks.WhyNotAllowed(target);
        var items = new List<object?>();
        if (all.Count > 0 && !Locks.IsOpen(target))
            items.Add(Menus.Item("🔓 Unlock for now…", () => _ = LockGate.EnsureAsync(target, "use " + title), tip: $"Stays open for {Minutes()} and closes when the app locks"));
        if (all.Count > 0 && Locks.IsOpen(target))
            items.Add(Menus.Item("🔒 Lock again now", () => { foreach (var r in all) Locks.CloseOne(r.Target); }));
        if (own == null)
        {
            if (all.Count > 0) items.Add(Menus.Item($"Locked through “{all[0].Title}”", () => { }, enabled: false));
            items.Add(Menus.Sub("🔒 Lock “" + Short(title) + "”",
                Menus.Item("With admin approval (UAC)", () => Set(target, title, Locks.Uac), enabled: why == null, tip: why ?? "Opening it needs Windows admin approval"),
                Menus.Item("With Windows sign-in (Hello or password)", () => Set(target, title, Locks.SignIn), enabled: why == null, tip: why ?? "Opening it needs you to sign in to Windows again"),
                Menus.Item("With a password of its own…", () => Set(target, title, Locks.Password), enabled: why == null, tip: why ?? "Opening it needs this password (at least 12 characters)")));
        }
        else if (own.Managed)
            items.Add(Menus.Item("Locked by your organisation (Group Policy)", () => { }, enabled: false));
        else
            items.Add(Menus.Item("Remove the lock…", () => _ = RemoveAsync(target, own), enabled: why == null, tip: why ?? $"Unlocks it first ({Locks.ModeTitle(own.Mode)})"));
        return items.ToArray();
    }

    private static string Minutes() => AppSettings.Current.LockOpenMinutes is var m && m > 0 ? $"{m} minute(s)" : "this one use";
    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>Adds a lock (asks for the password of a password lock, twice).</summary>
    public static void Set(string target, string title, string mode)
    {
        string? pw = null;
        if (mode == Locks.Password)
        {
            pw = Dialogs.Prompt("Lock with a password", $"A password for “{title}” (at least {FeatureLock.MinLength} characters). It can't be recovered: without it, only removing the lock's settings opens it.", password: true);
            if (pw == null) return;
            if (pw.Length < FeatureLock.MinLength) { Dialogs.Info("Lock", $"Use at least {FeatureLock.MinLength} characters."); return; }
            if (Dialogs.Prompt("Lock with a password", "The same password again.", password: true) != pw) { Dialogs.Info("Lock", "The two passwords differ. Nothing was locked."); return; }
        }
        var r = Locks.Set(target, title, mode, pw);
        if (r != Locks.Result.Ok)
            Dialogs.Info("Lock", r switch
            {
                Locks.Result.NotAllowed => Locks.WhyNotAllowed(target) ?? "Not allowed.",
                Locks.Result.Managed => "Your organisation already locks it.",
                Locks.Result.Exists => "It is locked already.",
                _ => "It could not be locked.",
            });
        else Ui.Window.Status($"🔒 “{title}” is locked ({Locks.ModeTitle(mode)}).");
    }

    /// <summary>Removes a lock after it has been opened its own way.</summary>
    public static async Task RemoveAsync(string target, Locks.Effective own)
    {
        if (!Locks.IsOpenRule(target) && !await LockGate.OpenRuleAsync(own, "remove its lock")) return;
        if (!Dialogs.Confirm("Remove the lock", $"Remove the lock on “{own.Title}”? It then opens without {Locks.ModeTitle(own.Mode)}.", "Remove lock", danger: true)) return;
        Locks.Remove(target);
        Ui.Window.Status($"“{own.Title}” is no longer locked.");
    }
}
