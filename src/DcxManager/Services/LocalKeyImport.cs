using DcxManager.Ssh;

namespace DcxManager.Services;

/// <summary>
/// Bringing this PC's own SSH keys into the store (0.6.30, ADR-107): the import behind Quick Start's "Your SSH keys" page
/// and Credentials → ⇩ From ~/.ssh, and the pick-up at start (Settings → Terminal &amp; connections, off by default). It
/// follows the rules of ＋ Add: a viewer profile, the "adding credentials" restriction or a closed Credentials lock stop it.
/// </summary>
public static class LocalKeyImport
{
    public sealed record Result(List<Credential> Added, List<string> Skipped, Credential? Default);

    /// <summary>Why keys can't be added now (for code that never asks), or null.</summary>
    public static string? WhyNot()
    {
        if (Profiles.ReadOnly) return "this profile is read-only (viewer role)";
        if (Restrictions.Why("credential.add") is { } why) return why.TrimEnd('.');
        if (!Locks.IsOpen("cat:credentials")) return "Credentials is locked (Credentials → ⇩ From ~/.ssh asks to unlock it)";
        return null;
    }

    /// <summary>
    /// Copies <paramref name="chosen"/> into the store. A key with a passphrase asks for it (up to three tries; no answer
    /// skips it) and keeps it. With <paramref name="makeDefault"/>, the first default identity becomes quick connect's key
    /// when none is set (an identity already in the store counts too). Nothing is written to the files.
    /// </summary>
    public static Result Import(IReadOnlyList<LocalKeys.Found> chosen, bool makeDefault, Func<LocalKeys.Found, bool, string?> askPassphrase, string how)
    {
        var data = AppData.Current;
        var s = AppSettings.Current;
        var added = new List<Credential>();
        var skipped = new List<string>();
        Credential? def = null;
        bool wantDefault = makeDefault && data.FindCredential(s.DefaultKeyCredentialId) is not { Kind: CredentialKind.PrivateKey };
        foreach (var f in chosen)
        {
            if (f.State == LocalKeys.State.Unsupported) { skipped.Add($"{f.FileName}: {f.Why}"); continue; }
            if (f.State == LocalKeys.State.AlreadyStored)
            {
                skipped.Add($"{f.FileName}: already in the store");
                if (wantDefault && def == null && f.IsDefault && Stored(f) is { } same) def = same;
                continue;
            }
            Credential? c = null;
            bool wrong = false, asked = false;
            if (f.State == LocalKeys.State.Ready)
            {
                try { c = LocalKeys.ToCredential(f, data.Credentials); }
                catch (Exception ex) { skipped.Add($"{f.FileName}: {ex.Message.Split('\n')[0]}"); continue; }
            }
            else
                for (int attempt = 0; attempt < 3 && c == null; attempt++)
                {
                    if (askPassphrase(f, wrong) is not { Length: > 0 } pass) break;
                    asked = true;
                    try { c = LocalKeys.ToCredential(f, data.Credentials, pass); }
                    catch (Exception) { wrong = true; }
                }
            if (c == null) { skipped.Add($"{f.FileName}: " + (wrong ? "the passphrase didn't open it" : asked ? "not imported" : "no passphrase given")); continue; }
            data.Credentials.Add(c);
            added.Add(c);
            MarkSeen(f);
            AuditLog.Write("you", "vault", $"added SSH key {c.Name} from ~/.ssh ({how})", c.Fingerprint);
            if (wantDefault && def == null && f.IsDefault) def = c;
        }
        if (def != null)
        {
            s.DefaultKeyCredentialId = def.Id;
            AuditLog.Write("you", "vault", $"quick connect uses {def.Name}", "ok");
        }
        if (added.Count > 0) AppData.Save();
        if (added.Count > 0 || def != null) { try { AppSettings.Save(); } catch (Exception ex) { Log.Write("SSH key import: settings", ex); } }
        return new Result(added, skipped, def);
    }

    private static Credential? Stored(LocalKeys.Found f) =>
        AppData.Current.Credentials.FirstOrDefault(c => c.Kind == CredentialKind.PrivateKey && !c.IsExternal
            && (f.Fingerprint.Length > 0 && c.Fingerprint == f.Fingerprint || c.Secret.Trim() == f.Text.Trim()));

    private static void MarkSeen(LocalKeys.Found f)
    {
        var seen = AppSettings.Current.LocalSshKeysSeen;
        if (!seen.Contains(f.Id)) seen.Add(f.Id);
        if (seen.Count > 200) seen.RemoveRange(0, seen.Count - 200);
    }

    // ---- at start ----------------------------------------------------------------------------------------------------------

    /// <summary>When Settings says so: new keys in ~/.ssh are picked up a moment after start (never in the self-test, which
    /// calls <see cref="PickUpAsync"/> on a scratch folder). <paramref name="say"/> shows the result (the status bar).</summary>
    public static void AtStart(Action<string> say)
    {
        if (!AppSettings.Current.LocalSshKeysAutoImport || TestMode.Running) return;
        _ = RunAtStartAsync(say);
    }

    private static async Task RunAtStartAsync(Action<string> say)
    {
        try
        {
            var said = await PickUpAsync();
            if (said.Length > 0) say(said);
        }
        catch (Exception ex) { Log.Write("SSH keys at start", ex); }
    }

    /// <summary>
    /// The pick-up (UI thread): each key that is new in the folder is looked at once. One without a passphrase is copied in;
    /// one with a passphrase is mentioned (Credentials → ⇩ From ~/.ssh imports it); one that can't be used is passed over.
    /// A key removed from the store later isn't brought back. Quick connect's key is never changed here. Returns the
    /// status line, or "" when there was nothing new.
    /// </summary>
    internal static async Task<string> PickUpAsync()
    {
        var stored = AppData.Current.Credentials.ToList();
        var found = await Task.Run(() => LocalKeys.Scan(stored));
        var seen = AppSettings.Current.LocalSshKeysSeen;
        var fresh = found.Where(f => !seen.Contains(f.Id)).ToList();
        if (fresh.Count == 0) return "";
        // Already in the store (imported by hand, or from ~/.ssh/config): only remembered, so removing it later sticks.
        var news = fresh.Where(f => f.State != LocalKeys.State.AlreadyStored).ToList();
        if (news.Count > 0 && WhyNot() is { } why) return $"{news.Count} new SSH key(s) in ~/.ssh weren't picked up: {why}.";
        var ready = news.Where(f => f.State == LocalKeys.State.Ready).ToList();
        var locked = news.Where(f => f.State == LocalKeys.State.NeedsPassphrase).ToList();
        var r = Import(ready, makeDefault: false, (_, _) => null, "picked up at start");
        foreach (var f in fresh) MarkSeen(f);
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("SSH keys at start: settings", ex); }
        var parts = new List<string>();
        if (r.Added.Count > 0) parts.Add($"Picked up {r.Added.Count} new SSH key(s) from ~/.ssh into the encrypted store: {string.Join(", ", r.Added.Select(c => c.Name))}.");
        if (locked.Count > 0) parts.Add($"{string.Join(", ", locked.Select(f => f.FileName))} {(locked.Count == 1 ? "has" : "have")} a passphrase: Credentials → ⇩ From ~/.ssh imports {(locked.Count == 1 ? "it" : "them")}.");
        return string.Join(" ", parts);
    }
}
