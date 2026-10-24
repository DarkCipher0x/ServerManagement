using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace DcxManager.Services;

/// <summary>
/// Writes the Group Policy templates (0.3.0 §10, ADR-040) from <see cref="PolicySettings.Catalog"/>: an ADMX file and its
/// en-US ADML (copy both into C:\Windows\PolicyDefinitions or the domain's Central Store; Intune imports them too), and
/// the reference table in docs/POLICIES.md. Every build writes them (build.ps1 → <c>--export-policy-templates</c>); the
/// self-test checks docs/POLICIES.md is the one the catalog makes, so the docs can't drift.
/// </summary>
public static class PolicyTemplates
{
    public const string FileName = "DarkCipherServerManagement";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    private static string CatId(string category) => "Cat_" + new string(category.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Computer-only on/off policies read before any profile or store is open (they decide which profile opens), so
    /// they are not settings in the catalog (0.5.0: they were missing from the templates before).</summary>
    public static readonly (string Name, string Title, string Help)[] MachineFlags =
    {
        ("RequireDirectorySignIn", "Only profiles that sign in with the organisation (no local profiles)",
            "On: local profiles can't be opened or created; every profile signs in with Active Directory (the Windows session), LDAPS or Microsoft Entra ID."),
        ("AutoProfileForWindowsAccount", "Open the profile bound to the signed-in Windows account automatically",
            "On (or not set): when a profile is bound to the account signed in to Windows, it opens without the profile picker and signs in with that account (Windows Hello unlocks the store when set up). Off: the picker is always shown."),
    };
    public const string MachineCategory = "Identity and directory (computer)";

    /// <summary>Writes the ADMX, en-US\ADML and POLICIES.md into <paramref name="dir"/>. Returns the files written.</summary>
    public static IReadOnlyList<string> Write(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "en-US"));
        var files = new[]
        {
            (Path.Combine(dir, FileName + ".admx"), Admx().ToString()),
            (Path.Combine(dir, "en-US", FileName + ".adml"), Adml().ToString()),
            (Path.Combine(dir, "POLICIES.md"), Markdown()),
        };
        foreach (var (path, text) in files) File.WriteAllText(path, path.EndsWith(".md", StringComparison.Ordinal) ? text : "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + text, new UTF8Encoding(false));
        return files.Select(f => f.Item1).ToList();
    }

    public static XElement Admx()
    {
        var cats = new XElement(Ns + "categories",
            new XElement(Ns + "category", new XAttribute("name", "DarkCipherSystems"), new XAttribute("displayName", "$(string.DarkCipherSystems)")),
            new XElement(Ns + "category", new XAttribute("name", "ServerManagement"), new XAttribute("displayName", "$(string.ServerManagement)"),
                new XElement(Ns + "parentCategory", new XAttribute("ref", "DarkCipherSystems"))));
        foreach (var c in PolicySettings.CategoryNames.Append(MachineCategory))
            cats.Add(new XElement(Ns + "category", new XAttribute("name", CatId(c)), new XAttribute("displayName", $"$(string.{CatId(c)})"),
                new XElement(Ns + "parentCategory", new XAttribute("ref", "ServerManagement"))));

        var policies = new XElement(Ns + "policies");
        foreach (var (name, _, _) in MachineFlags)
            policies.Add(new XElement(Ns + "policy", new XAttribute("name", name), new XAttribute("class", "Machine"),
                new XAttribute("displayName", $"$(string.{name})"), new XAttribute("explainText", $"$(string.{name}_Help)"),
                new XAttribute("key", PolicySettings.Key), new XAttribute("valueName", name),
                new XElement(Ns + "parentCategory", new XAttribute("ref", CatId(MachineCategory))),
                new XElement(Ns + "supportedOn", new XAttribute("ref", "SUPPORTED_DCXSM_030")),
                new XElement(Ns + "enabledValue", new XElement(Ns + "decimal", new XAttribute("value", 1))),
                new XElement(Ns + "disabledValue", new XElement(Ns + "decimal", new XAttribute("value", 0)))));
        foreach (var e in PolicySettings.Catalog)
        {
            var p = new XElement(Ns + "policy", new XAttribute("name", e.Name), new XAttribute("class", "Both"),
                new XAttribute("displayName", $"$(string.{e.Name})"), new XAttribute("explainText", $"$(string.{e.Name}_Help)"),
                new XAttribute("key", PolicySettings.Key));
            if (e.Kind != PolicySettings.Kind.OnOff) p.Add(new XAttribute("presentation", $"$(presentation.{e.Name})"));
            else p.Add(new XAttribute("valueName", e.Name));
            p.Add(new XElement(Ns + "parentCategory", new XAttribute("ref", CatId(e.Category))),
                  new XElement(Ns + "supportedOn", new XAttribute("ref", "SUPPORTED_DCXSM_030")));
            switch (e.Kind)
            {
                case PolicySettings.Kind.OnOff:
                    p.Add(new XElement(Ns + "enabledValue", new XElement(Ns + "decimal", new XAttribute("value", 1))),
                          new XElement(Ns + "disabledValue", new XElement(Ns + "decimal", new XAttribute("value", 0))));
                    break;
                case PolicySettings.Kind.Number:
                    var r = e.Range ?? (0, int.MaxValue);
                    p.Add(new XElement(Ns + "elements", new XElement(Ns + "decimal", new XAttribute("id", e.Name), new XAttribute("valueName", e.Name),
                        new XAttribute("minValue", (long)r.Min), new XAttribute("maxValue", (long)Math.Min(r.Max, uint.MaxValue)))));
                    break;
                case PolicySettings.Kind.Text when e.Choices != null:
                    p.Add(new XElement(Ns + "elements", new XElement(Ns + "enum", new XAttribute("id", e.Name), new XAttribute("valueName", e.Name),
                        e.Choices.Select(c => new XElement(Ns + "item", new XAttribute("displayName", $"$(string.{e.Name}_{Safe(c)})"),
                            new XElement(Ns + "value", new XElement(Ns + "string", c)))))));
                    break;
                case PolicySettings.Kind.Text or PolicySettings.Kind.Decimal:
                    p.Add(new XElement(Ns + "elements", new XElement(Ns + "text", new XAttribute("id", e.Name), new XAttribute("valueName", e.Name),
                        new XAttribute("maxLength", 2048), IsPath(e.Name) ? new XAttribute("expandable", "true") : null)));
                    break;
                case PolicySettings.Kind.List:
                    p.Add(new XElement(Ns + "elements", new XElement(Ns + "multiText", new XAttribute("id", e.Name), new XAttribute("valueName", e.Name))));
                    break;
            }
            policies.Add(p);
        }
        return new XElement(Ns + "policyDefinitions", new XAttribute("revision", "1.0"), new XAttribute("schemaVersion", "1.0"),
            new XElement(Ns + "policyNamespaces",
                new XElement(Ns + "target", new XAttribute("prefix", "dcxsm"), new XAttribute("namespace", "DarkCipherSystems.Policies.ServerManagement")),
                new XElement(Ns + "using", new XAttribute("prefix", "windows"), new XAttribute("namespace", "Microsoft.Policies.Windows"))),
            new XElement(Ns + "resources", new XAttribute("minRequiredRevision", "1.0")),
            new XElement(Ns + "supportedOn", new XElement(Ns + "definitions",
                new XElement(Ns + "definition", new XAttribute("name", "SUPPORTED_DCXSM_030"), new XAttribute("displayName", "$(string.SUPPORTED_DCXSM_030)")))),
            cats, policies);
    }

    public static XElement Adml()
    {
        var strings = new XElement(Ns + "stringTable");
        void S(string id, string text) => strings.Add(new XElement(Ns + "string", new XAttribute("id", id), text));
        S("DarkCipherSystems", "DarkCipher Systems");
        S("ServerManagement", "DarkCipher ServerManagement");
        S("SUPPORTED_DCXSM_030", "DarkCipher ServerManagement 0.3.0 and later");
        foreach (var c in PolicySettings.CategoryNames.Append(MachineCategory)) S(CatId(c), c);
        foreach (var (name, title, help) in MachineFlags) { S(name, title); S(name + "_Help", help + $"\n\nRegistry value {name} (REG_DWORD 0 / 1) under HKLM\\{PolicySettings.Key} (computer only)."); }
        var presentations = new XElement(Ns + "presentationTable");
        foreach (var e in PolicySettings.Catalog)
        {
            S(e.Name, e.Title);
            S(e.Name + "_Help", Explain(e));
            if (e.Choices != null) foreach (var c in e.Choices) S($"{e.Name}_{Safe(c)}", c);
            XElement? control = e.Kind switch
            {
                PolicySettings.Kind.Number => new XElement(Ns + "decimalTextBox", new XAttribute("refId", e.Name), new XAttribute("defaultValue", Default(e) is int d ? d : 0), e.Title),
                PolicySettings.Kind.Text when e.Choices != null => new XElement(Ns + "dropdownList", new XAttribute("refId", e.Name), new XAttribute("noSort", "true"), e.Title),
                PolicySettings.Kind.Text or PolicySettings.Kind.Decimal => new XElement(Ns + "textBox", new XAttribute("refId", e.Name), new XElement(Ns + "label", e.Title)),
                PolicySettings.Kind.List => new XElement(Ns + "multiTextBox", new XAttribute("refId", e.Name), e.Title + " (one per line)"),
                _ => null,
            };
            if (control != null) presentations.Add(new XElement(Ns + "presentation", new XAttribute("id", e.Name), control));
        }
        return new XElement(Ns + "policyDefinitionResources", new XAttribute("revision", "1.0"), new XAttribute("schemaVersion", "1.0"),
            new XElement(Ns + "displayName", "DarkCipher ServerManagement"),
            new XElement(Ns + "description", "Policies for DarkCipher ServerManagement (DarkCipher Systems)."),
            new XElement(Ns + "resources", strings, presentations));
    }

    /// <summary>docs/POLICIES.md: every policy by category with its registry name, type, accepted values and app default.</summary>
    public static string Markdown()
    {
        var sb = new StringBuilder();
        sb.Append("# Group Policy reference\n\n");
        sb.Append("Generated from the app's policy catalog (`PolicySettings.Catalog`, ADR-040) by every build; don't edit by hand.\n\n");
        sb.Append($"Registry: `HKLM\\{PolicySettings.Key}` (computer, wins) and `HKCU\\{PolicySettings.Key}` (user). ");
        sb.Append("Templates: `policy\\DarkCipherServerManagement.admx` and `policy\\en-US\\DarkCipherServerManagement.adml` in each release folder. ");
        sb.Append("A managed setting can't be changed in the app; an invalid value is ignored and listed in Settings → Security & privacy → Managed by your organisation.\n\n");
        sb.Append($"{PolicySettings.Catalog.Count} policies, plus {MachineFlags.Length} computer-only ones (below). Not available as policies: {string.Join(", ", PolicySettings.Excluded.Keys.Order(StringComparer.Ordinal))} (accounts, secrets and per-PC state).\n");
        foreach (var g in PolicySettings.Catalog.GroupBy(e => e.Category))
        {
            sb.Append($"\n## {g.Key}\n\n| Value name | Setting | Type | Accepted | App default |\n|---|---|---|---|---|\n");
            foreach (var e in g) sb.Append($"| `{e.Name}` | {e.Title} | {e.RegistryType} | {Accepted(e)} | {Shown(Default(e))} |\n");
        }
        sb.Append($"\n## {MachineCategory}\n\nRead before any profile opens, so they exist only under `HKLM` (computer policy).\n\n| Value name | Setting | Type | Accepted | Not set |\n|---|---|---|---|---|\n");
        foreach (var (name, title, _) in MachineFlags) sb.Append($"| `{name}` | {title} | REG_DWORD | 0 / 1 | {(name == "AutoProfileForWindowsAccount" ? "on" : "off")} |\n");
        return sb.ToString();
    }

    private static string Explain(PolicySettings.Entry e) =>
        $"{e.Title}.\n\nRegistry value {e.Name} ({e.RegistryType}) under {PolicySettings.Key}. Accepted: {Accepted(e)}. The app's default: {Shown(Default(e))}.\n\n" +
        "Enabled: the value is set for every user (Computer Configuration) or for this user (User Configuration); the computer policy wins. " +
        "While it is set the setting can't be changed in the app. Not configured: users choose.";

    private static string Accepted(PolicySettings.Entry e) => e.Kind switch
    {
        PolicySettings.Kind.OnOff => "1 = on, 0 = off",
        PolicySettings.Kind.Number => e.Range is { } r ? $"{r.Min.ToString(CultureInfo.InvariantCulture)}–{r.Max.ToString(CultureInfo.InvariantCulture)}" : "0 or more",
        PolicySettings.Kind.Decimal => e.Range is { } r2 ? $"{r2.Min.ToString(CultureInfo.InvariantCulture)}–{r2.Max.ToString(CultureInfo.InvariantCulture)} (as text, e.g. 1.25)" : "a number as text",
        PolicySettings.Kind.Text => e.Choices is { } c ? string.Join(" / ", c.Select(x => $"`{x}`")) : IsPath(e.Name) ? "a folder path (REG_EXPAND_SZ allowed)" : "text (up to 2,048 characters)",
        _ => "one entry per line (up to 200)",
    };

    private static object? Default(PolicySettings.Entry e) => e.Property.GetValue(new AppSettings());

    private static string Shown(object? v) => v switch
    {
        null => "not set",
        bool b => b ? "on" : "off",
        string s => s.Length == 0 ? "empty" : $"`{s}`",
        List<string> l => l.Count == 0 ? "empty" : string.Join(", ", l),
        double d => d.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static bool IsPath(string name) => name.EndsWith("Dir", StringComparison.Ordinal) || name.EndsWith("Folder", StringComparison.Ordinal) || name.EndsWith("Workspace", StringComparison.Ordinal);
    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
