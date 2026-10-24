using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DcxManager.Services;

/// <summary>
/// The AI features lock (0.2.0 phase 5, docs/history/PLAN-0.2.0.md §3.1): a password that guards three changes — turning AI
/// features on or off, "Allow internet AI providers", and changing or removing the lock itself. Only a salted
/// PBKDF2-SHA256 verifier (600,000 rounds) is kept, in its own encrypted store file; the password is never stored, logged
/// or shown. After 3 wrong attempts each further try waits 30 s, doubling to at most 10 minutes; the count and the wait
/// are kept in the store, so restarting the app doesn't reset them. Every attempt is audited (target "ai-lock"), never
/// with the text typed. There is no recovery: whoever can delete the store file can remove the lock (and could read
/// everything the app stores anyway) — the app notices and audits it at the next start (SECURITY.md).
/// Add-ons and the local API have no access to any of this.
/// </summary>
public static class FeatureLock
{
    public const string FileName = "ai-lock.dcx";
    public const int MinLength = 12;   // the app's rule for every password (master password, backups, library keys)
    private const int Rounds = 600_000;
    private const int FreeTries = 3;
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(30), MaxWait = TimeSpan.FromMinutes(10);

    /// <summary>The verifier and the throttle state, as stored (encrypted).</summary>
    public sealed class Record
    {
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Rounds { get; set; } = FeatureLock.Rounds;
        public int Failures { get; set; }
        public DateTime NotBeforeUtc { get; set; }
        public DateTime SetUtc { get; set; }
        /// <summary>"password" (a verifier above) or "hello" (0.3.0 §2: Windows Hello confirms instead; no password).</summary>
        public string Mode { get; set; } = "password";
    }

    public enum Result { Ok, Wrong, Wait, NotSet, TooShort }

    /// <summary>The clock (the self-test injects one, so throttling is tested without waiting).</summary>
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

    private static readonly object Sync = new();

    private static Record? Load()
    {
        try { return File.Exists(Path.Combine(SecureStore.DataDir, FileName)) ? SecureStore.Read<Record>(FileName) : null; }
        catch (Exception ex) { Log.Write("AI lock read", ex); return null; }
    }

    private static void Store(Record? r)
    {
        var path = Path.Combine(SecureStore.DataDir, FileName);
        if (r == null) { if (File.Exists(path)) File.Delete(path); return; }
        SecureStore.Write(FileName, r);
    }

    public static bool IsSet { get { lock (Sync) return Load() is { } r && (r.Hash.Length > 0 || r.Mode == "hello"); } }

    /// <summary>The lock is a Windows Hello lock (0.3.0 §2): Hello confirms each gated change; there is no password.</summary>
    public static bool IsHello { get { lock (Sync) return Load() is { Mode: "hello" }; } }

    /// <summary>The app's Windows Hello check (true = the user verified), set at start-up. Without it a Hello lock refuses.</summary>
    public static Func<string, bool>? HelloCheck { get; set; }

    /// <summary>Sets a Windows Hello lock when there is none (Hello has confirmed already).</summary>
    public static Result SetHello()
    {
        lock (Sync)
        {
            Store(new Record { Mode = "hello", SetUtc = UtcNow() });
            MarkSet(true);
            AuditLog.Write("you", "ai-lock", "AI features lock set (Windows Hello)");
            return Result.Ok;
        }
    }

    /// <summary>Removes a Windows Hello lock after Hello confirms.</summary>
    public static Result RemoveHello()
    {
        if (!IsHello) return Result.NotSet;
        if (HelloCheck?.Invoke("remove the AI features lock") != true) { AuditLog.Write("you", "ai-lock", "Windows Hello not confirmed", "remove the lock"); return Result.Wrong; }
        lock (Sync) { Store(null); MarkSet(false); }
        AuditLog.Write("you", "ai-lock", "AI features lock removed (Windows Hello)");
        return Result.Ok;
    }

    /// <summary>How long until the next attempt is accepted (zero when it is accepted now).</summary>
    public static TimeSpan WaitLeft { get { lock (Sync) return Load() is { } r && r.NotBeforeUtc > UtcNow() ? r.NotBeforeUtc - UtcNow() : TimeSpan.Zero; } }

    /// <summary>Checks a password. Correct resets the throttle; wrong counts toward it; during a wait nothing is checked.
    /// One audit entry per attempt, never with the text.</summary>
    public static Result Verify(string password, string purpose)
    {
        lock (Sync)
        {
            if (Load() is not { Hash.Length: > 0 } r) return Result.NotSet;
            var now = UtcNow();
            if (r.NotBeforeUtc > now)
            {
                AuditLog.Write("you", "ai-lock", "attempt refused: waiting after wrong passwords", $"{purpose} · {(int)Math.Ceiling((r.NotBeforeUtc - now).TotalSeconds)} s left");
                return Result.Wait;
            }
            if (Matches(r, password))
            {
                r.Failures = 0; r.NotBeforeUtc = default;
                Store(r);
                AuditLog.Write("you", "ai-lock", "correct password", purpose);
                return Result.Ok;
            }
            r.Failures++;
            if (r.Failures >= FreeTries)
            {
                var wait = TimeSpan.FromTicks(Math.Min(MaxWait.Ticks, FirstWait.Ticks * (1L << Math.Min(20, r.Failures - FreeTries))));
                r.NotBeforeUtc = now + wait;
            }
            Store(r);
            AuditLog.Write("you", "ai-lock", "wrong password", $"{purpose} · {r.Failures} wrong in a row");
            return Result.Wrong;
        }
    }

    /// <summary>Sets the lock when there is none (changing or removing it needs <see cref="Verify"/> first).</summary>
    public static Result Set(string password)
    {
        if (password.Length < MinLength) return Result.TooShort;
        lock (Sync)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            Store(new Record { Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(Derive(password, salt, Rounds)), SetUtc = UtcNow() });
            MarkSet(true);
            AuditLog.Write("you", "ai-lock", "AI features lock set");
            return Result.Ok;
        }
    }

    /// <summary>Replaces the password after the current one was verified.</summary>
    public static Result Change(string current, string next)
    {
        if (next.Length < MinLength) return Result.TooShort;
        var v = Verify(current, "change the lock password");
        if (v != Result.Ok) return v;
        lock (Sync)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            Store(new Record { Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(Derive(next, salt, Rounds)), SetUtc = UtcNow() });
            AuditLog.Write("you", "ai-lock", "AI features lock password changed");
            return Result.Ok;
        }
    }

    /// <summary>Removes the lock after the password was verified.</summary>
    public static Result Remove(string current)
    {
        var v = Verify(current, "remove the lock");
        if (v != Result.Ok) return v;
        lock (Sync) { Store(null); MarkSet(false); }
        AuditLog.Write("you", "ai-lock", "AI features lock removed");
        return Result.Ok;
    }

    /// <summary>At start-up: a lock that the settings say exists but whose file is gone was removed outside the app
    /// (the only recovery path, SECURITY.md) — audit it once and forget it.</summary>
    public static void ReconcileAtStart()
    {
        try
        {
            var s = AppSettings.Current;
            bool set = IsSet;
            if (s.AiLockSet && !set) { AuditLog.Write("app", "ai-lock", "AI features lock removed outside the app (its store file was deleted)"); MarkSet(false); }
            else if (!s.AiLockSet && set) MarkSet(true);
        }
        catch (Exception ex) { Log.Write("AI lock reconcile", ex); }
    }

    /// <summary>A gated change: allowed at once when there is no lock; otherwise only with the right password, asked
    /// through <paramref name="ask"/> (the in-app prompt; null = cancelled). Returns why not, or null when allowed.</summary>
    public static string? Gate(string purpose, Func<string?> ask)
    {
        if (!IsSet) return null;
        if (IsHello)
        {
            bool ok = HelloCheck?.Invoke(purpose) == true;
            AuditLog.Write("you", "ai-lock", ok ? "Windows Hello confirmed" : "Windows Hello not confirmed", purpose);
            return ok ? null : "Windows Hello didn't confirm. Nothing changed.";
        }
        if (WaitLeft is { TotalSeconds: > 0 } w) return $"Too many wrong passwords — try again in {Math.Ceiling(w.TotalSeconds):0} s.";
        if (ask() is not { } pw) return "Cancelled.";
        return Verify(pw, purpose) switch
        {
            Result.Ok => null,
            Result.Wait => $"Too many wrong passwords — try again in {Math.Ceiling(WaitLeft.TotalSeconds):0} s.",
            _ => "Wrong password.",
        };
    }

    /// <summary>Settings arriving from elsewhere (a snapshot restore, a backup's "replace everything") never change what the
    /// lock guards while it is set — AI features on/off and internet AI providers keep their current values — and never
    /// change the lock's own flag (0.2.0 audit: otherwise an older snapshot or a doctored backup would undo a lock).</summary>
    public static void KeepGuarded(AppSettings incoming)
    {
        var cur = AppSettings.Current;
        incoming.AiLockSet = cur.AiLockSet;
        // 0.5.1 (ADR-067): a restore never removes or swaps the locks set here (an older backup must not undo a lock).
        incoming.UserLocks = cur.UserLocks.Select(r => r).ToList();
        if (!IsSet) return;
        incoming.AiFeatures = cur.AiFeatures;
        incoming.AiAllowCloud = cur.AiAllowCloud;
    }

    /// <summary><see cref="KeepGuarded(AppSettings)"/> on an encrypted settings.dcx (as a snapshot holds it): opened,
    /// adjusted and sealed again with the store key. Throws when it can't be read (the restore then stops).</summary>
    public static byte[] KeepGuardedSealed(byte[] sealedSettings)
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(SecureStore.Decrypt(sealedSettings, "settings.dcx"), SecureStore.Json)
                ?? throw new InvalidDataException("the snapshot's settings are empty");
        KeepGuarded(s);
        return SecureStore.Encrypt(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(s, SecureStore.Json), "settings.dcx");
    }

    /// <summary>A settings.dcx put back from a snapshot because the current one is damaged (start-up recovery): the
    /// current guarded values can't be read, so while the lock is set they come back switched off — fail closed; turning
    /// them on again needs the lock password — and the lock flag follows the lock file (0.2.0 phase 7: without this, a
    /// damaged settings file plus an older snapshot would undo the lock).</summary>
    public static byte[] FailClosedSealed(byte[] sealedSettings)
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(SecureStore.Decrypt(sealedSettings, "settings.dcx"), SecureStore.Json)
                ?? throw new InvalidDataException("the snapshot's settings are empty");
        s.AiLockSet = IsSet;
        if (s.AiLockSet) { s.AiFeatures = false; s.AiAllowCloud = false; }
        return SecureStore.Encrypt(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(s, SecureStore.Json), "settings.dcx");
    }

    private static void MarkSet(bool on)
    {
        if (AppSettings.Current.AiLockSet == on) return;
        AppSettings.Current.AiLockSet = on;
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("AI lock flag", ex); }
    }

    private static bool Matches(Record r, string password)
    {
        try { return CryptographicOperations.FixedTimeEquals(Derive(password, Convert.FromBase64String(r.Salt), r.Rounds), Convert.FromBase64String(r.Hash)); }
        catch { return false; }
    }

    private static byte[] Derive(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA256, 32);

    /// <summary>Self-test: forget the lock and its flag (scratch store only).</summary>
    internal static void ResetForTest() { lock (Sync) { Store(null); AppSettings.Current.AiLockSet = false; } }
}
