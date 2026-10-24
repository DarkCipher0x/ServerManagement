using System.IO;

namespace DcxManager.Services;

/// <summary>Paths named inside an add-on or theme package (moved below the add-ons in 0.2.0 phase 6: theme parsing in the
/// UI kit uses the same rule as the package installer).</summary>
public static class PackagePath
{
    /// <summary>A relative path inside the package, or null when it could escape the install folder.</summary>
    public static string? SafeRelative(string p)
    {
        var s = p.Replace('\\', '/');
        if (s.Length == 0 || s.Length > 200 || s.StartsWith('/') || s.Contains(':') || s.Split('/').Any(x => x is "" or "." or "..")) return null;
        if (s.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || s.Split('/').Any(x => x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        return s.Replace('/', '\\');
    }
}
