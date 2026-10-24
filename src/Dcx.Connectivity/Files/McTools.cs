using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Ssh;

namespace DcxManager.Files;

/// <summary>
/// The Midnight Commander parts of the File commander that are pure logic (0.3.0 §15): shell patterns for "+ select group",
/// the user-menu macros (%f %d %s %t %F %D, every value shell-quoted), and the remote commands for Find file, directory
/// sizes, chown and links — each built from quoted values with "--" before paths, so a file name is never read as an option
/// or as shell code. Local equivalents (find, sizes, links) work on this PC without a shell.
/// </summary>
public static class McTools
{
    /// <summary>A shell pattern ("*.log", "data-??.csv", "*.[ch]", several separated by ';' or ',') as a regex; null = empty.</summary>
    public static Regex? Glob(string pattern, bool caseSensitive = false)
    {
        var parts = pattern.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        var sb = new StringBuilder("^(?:");
        for (int p = 0; p < parts.Length; p++)
        {
            if (p > 0) sb.Append('|');
            var s = parts[p];
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else if (c == '[' && s.IndexOf(']', i + 1) is var end and > 0)
                {
                    var set = s[(i + 1)..end];
                    sb.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]).Replace("\\-", "-") : Regex.Escape(set).Replace("\\-", "-")).Append(']');
                    i = end;
                }
                else sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append(")$");
        return new Regex(sb.ToString(), (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>The items a "+" / "−" group pattern matches (folders only when <paramref name="dirs"/>, like MC's option).</summary>
    public static IEnumerable<FsItem> Matching(IEnumerable<FsItem> items, string pattern, bool dirs, bool caseSensitive = false)
    {
        var rx = Glob(pattern, caseSensitive);
        return rx == null ? Enumerable.Empty<FsItem>() : items.Where(i => !i.IsParent && !i.IsDrive && (dirs || !i.IsDir) && rx.IsMatch(i.Name));
    }

    /// <summary>
    /// A user-menu command with its macros filled in (null + why when it can't be):
    /// %f current file name, %F the other pane's current file, %d this folder, %D the other pane's folder,
    /// %s the selected names (or the current one), %t the selected names only, %% a percent sign.
    /// Every value is shell-quoted; names with a line break are refused.
    /// </summary>
    public static string? Expand(string template, string dir, FsItem? current, IReadOnlyList<FsItem> selected, string otherDir, FsItem? otherCurrent, out string? why)
    {
        why = null;
        var all = selected.Concat(current != null ? new[] { current } : []).Concat(otherCurrent != null ? new[] { otherCurrent } : []).Select(i => i.Name).Append(dir).Append(otherDir);
        if (all.Any(n => n.Contains('\n') || n.Contains('\r') || n.Contains('\0'))) { why = "a name contains a line break"; return null; }
        var sb = new StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '%' || i + 1 >= template.Length) { sb.Append(template[i]); continue; }
            var m = template[++i];
            switch (m)
            {
                case '%': sb.Append('%'); break;
                case 'f': if (current == null) { why = "%f needs a current file"; return null; } sb.Append(RemoteExec.Quote(current.Name)); break;
                case 'F': if (otherCurrent == null) { why = "%F needs a current file in the other pane"; return null; } sb.Append(RemoteExec.Quote(otherCurrent.Name)); break;
                case 'd': sb.Append(RemoteExec.Quote(dir)); break;
                case 'D': sb.Append(RemoteExec.Quote(otherDir)); break;
                case 's':
                    var s = selected.Count > 0 ? selected : current != null ? new[] { current } : [];
                    if (s.Count == 0) { why = "%s needs a selection"; return null; }
                    sb.Append(string.Join(" ", s.Select(x => RemoteExec.Quote(x.Name)))); break;
                case 't': sb.Append(string.Join(" ", selected.Select(x => RemoteExec.Quote(x.Name)))); break;
                default: sb.Append('%').Append(m); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The user menu as editable text: "# Title [file|dir|any]" then the command on the next line(s, joined by "; ").</summary>
    public static string FormatUserMenu(IEnumerable<Services.UserMenuEntry> items) =>
        "# File commander user menu. Each entry: a line \"# Title [file|dir|any]\", then the command.\n" +
        "# Macros (shell-quoted): %f current file, %F other pane's file, %d this folder, %D other pane's folder, %s selection (or current), %t selection only, %% a percent sign.\n\n" +
        string.Join("\n\n", items.Select(u => $"# {u.Title} [{u.For}]\n{u.Command}")) + "\n";

    public static List<Services.UserMenuEntry> ParseUserMenu(string text, out List<string> errors)
    {
        errors = new();
        var list = new List<Services.UserMenuEntry>();
        Services.UserMenuEntry? cur = null;
        int n = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            n++;
            var line = raw.TrimEnd();
            if (line.StartsWith("# "))
            {
                var m = Regex.Match(line[2..], @"^(.+?)\s*\[(file|dir|any)\]\s*$");
                if (!m.Success) { if (cur == null && list.Count == 0) continue; errors.Add($"line {n}: a title line looks like “# Title [file|dir|any]”"); continue; }
                cur = new Services.UserMenuEntry { Title = m.Groups[1].Value.Trim(), For = m.Groups[2].Value };
                list.Add(cur);
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                if (cur == null) { errors.Add($"line {n}: a command before any “# Title [file|dir|any]” line"); continue; }
                cur.Command = cur.Command.Length == 0 ? line.Trim() : cur.Command + "; " + line.Trim();
            }
        }
        foreach (var e in list.Where(e => e.Command.Length == 0)) errors.Add($"“{e.Title}” has no command");
        list.RemoveAll(e => e.Command.Length == 0 || e.Title.Length > 80);
        return list;
    }

    // ---- remote commands (run with SshPool) --------------------------------------------------------------------------

    /// <summary>find under <paramref name="dir"/>: names matching a pattern, optionally containing text (grep -F, no regex).</summary>
    public static string FindCommand(string dir, string namePattern, string? content, int max = 2000)
    {
        if (namePattern.Contains('\n') || content?.Contains('\n') == true) throw new ArgumentException("line breaks aren't allowed");
        var name = namePattern.Trim().Length == 0 ? "*" : namePattern.Trim();
        var f = $"find {RemoteExec.Quote(dir)} -xdev -type f -iname {RemoteExec.Quote(name)} 2>/dev/null";
        if (!string.IsNullOrEmpty(content)) f += $" | tr '\\n' '\\0' | xargs -0 -r grep -l -I -i -F -- {RemoteExec.Quote(content)} 2>/dev/null";
        return f + $" | head -n {Math.Clamp(max, 1, 20000)}";
    }

    public static string DuCommand(IEnumerable<string> paths) => "du -sb -- " + string.Join(" ", paths.Select(RemoteExec.Quote)) + " 2>/dev/null";

    /// <summary>"du -sb" output → bytes per path.</summary>
    public static Dictionary<string, long> ParseDu(string output)
    {
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0 && long.TryParse(line[..tab], out var n)) d[line[(tab + 1)..].TrimEnd('\r')] = n;
        }
        return d;
    }

    /// <summary>user, user:group or :group (POSIX names or numeric ids).</summary>
    public static bool ValidOwner(string spec) => Regex.IsMatch(spec, @"^(?:[A-Za-z_][A-Za-z0-9_.-]{0,31}\$?|\d{1,10})?(?::(?:[A-Za-z_][A-Za-z0-9_.-]{0,31}\$?|\d{1,10}))?$") && spec.Trim(':').Length > 0;

    public static string ChownCommand(string spec, IEnumerable<string> paths, bool recursive)
    {
        if (!ValidOwner(spec)) throw new ArgumentException("owner looks like user, user:group or :group");
        return $"chown {(recursive ? "-R " : "")}-- {RemoteExec.Quote(spec)} " + string.Join(" ", paths.Select(RemoteExec.Quote));
    }

    public static string LinkCommand(string target, string link, bool symbolic) =>
        $"ln {(symbolic ? "-s " : "")}-- {RemoteExec.Quote(target)} {RemoteExec.Quote(link)}";

    // ---- this PC ------------------------------------------------------------------------------------------------------

    /// <summary>Find file on this PC (inaccessible folders skipped; the text search reads files up to 64 MB).</summary>
    public static List<string> FindLocal(string dir, string namePattern, string? content, int max, CancellationToken ct)
    {
        var rx = Glob(namePattern.Trim().Length == 0 ? "*" : namePattern);
        var hits = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested();
            if (!rx!.IsMatch(Path.GetFileName(f))) continue;
            if (!string.IsNullOrEmpty(content) && !FileContains(f, content, ct)) continue;
            hits.Add(f);
            if (hits.Count >= max) break;
        }
        return hits;
    }

    private static bool FileContains(string path, string text, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > 64L * 1024 * 1024) return false;
            using var r = new StreamReader(path, Encoding.UTF8, true);
            string? line;
            while ((line = r.ReadLine()) != null) { ct.ThrowIfCancellationRequested(); if (line.Contains(text, StringComparison.OrdinalIgnoreCase)) return true; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>Total bytes in a folder on this PC (links not followed; inaccessible parts skipped).</summary>
    public static long DirSizeLocal(string dir, CancellationToken ct)
    {
        long n = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested();
            n += f.Length;
        }
        return n;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>A link on this PC: symbolic (needs Developer Mode or administrator rights on Windows) or hard (same volume).</summary>
    public static void LinkLocal(string target, string link, bool symbolic)
    {
        if (File.Exists(link) || Directory.Exists(link)) throw new IOException("something with that name already exists");
        if (symbolic)
        {
            if (Directory.Exists(target)) Directory.CreateSymbolicLink(link, target); else File.CreateSymbolicLink(link, target);
            return;
        }
        if (Directory.Exists(target)) throw new IOException("hard links are for files only");
        if (!CreateHardLinkW(link, target, IntPtr.Zero)) throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
}
