using System.Security.Principal;

namespace DcxManager.Services;

/// <summary>
/// The name the app shows for the person using it (0.5.7, DarkCipher0x: "it should be saying approved by DarkCipher0x"). It is
/// the profile's name (Settings → Profiles → Rename; Quick Start "This profile's name"), never the Windows account
/// ("PC\user" names the PC and the account). The Windows account stays where it is the fact being shown —
/// to another administrator approving a change, in the security records an organisation's log sinks receive — and a
/// change approved by a different Windows administrator is recorded under that administrator's account.
/// </summary>
public static class AppIdentity
{
    /// <summary>The profile's name, cleaned for display (no control characters, at most 64 characters).</summary>
    public static string Name => Clean(Profiles.Current?.Name) is { Length: > 0 } n ? n : "this profile";

    /// <summary>The Windows account running the app (DOMAIN\user).</summary>
    public static string Account => WindowsIdentity.GetCurrent().Name;

    /// <summary>A recorded "who": this user's own Windows account shows as <see cref="Name"/> (records written before
    /// 0.5.7 carry the account); anyone else's is shown as recorded.</summary>
    public static string Show(string? recorded) =>
        string.IsNullOrWhiteSpace(recorded) ? Name
        : string.Equals(recorded, Account, StringComparison.OrdinalIgnoreCase) ? Name
        : recorded;

    /// <summary>A safe file or key-name fragment of <see cref="Name"/> ("darkcipher0x").</summary>
    public static string Slug()
    {
        var s = new string(Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Length > 0 ? s : "me";
    }

    internal static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > 64 ? t[..64] : t;
    }
}
