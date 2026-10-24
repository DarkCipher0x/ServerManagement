namespace DcxManager.Services;

/// <summary>
/// Segoe Fluent Icons for the sidebar and Settings (0.2.1, the DarkCipher house theme shared across the DarkCipher Systems apps). The app's
/// screens name themselves with a Unicode glyph ("▣  Hosts"); this maps each glyph to the Fluent icon drawn in its place
/// with the <c>IconFont</c> resource (Segoe Fluent Icons, falling back to Segoe MDL2 Assets — both ship with Windows, nothing
/// is bundled). A glyph without a mapping is drawn as itself in the interface font, so a module's own glyph still shows.
/// Every code point here was checked on a rendered sheet of the font (Windows 11, 2026-10-20).
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, char> ByGlyph = new()
    {
        ["◈"] = '',   // Dashboard / OVERVIEW: home
        ["▣"] = '',   // Hosts / SERVERS / team hosts: server
        ["◫"] = '',   // Infrastructure: tower
        ["☁"] = '',   // Cloud providers
        ["🌐"] = '',  // Portals: globe
        ["⚑"] = '',   // Incidents: flag
        ["✎"] = '',   // Notes: pencil
        ["⇆"] = '',   // File commander: folder
        ["⚡"] = '',   // Snippets & multi-run: lightning
        ["☷"] = '',   // Session logs: history
        ["⇄"] = '',   // Tunnels / TEAM / shared library: two-way arrows
        ["⌕"] = '',   // LAN discovery: network tower
        ["⌨"] = '',   // LOCAL: command prompt
        ["›"] = '', [">"] = '', ["❯"] = '', ["∆"] = '',   // local shells and WSL distributions
        ["≋"] = '',   // Serial / USB consoles: connector
        ["⚿"] = '',   // Credentials / VAULT: key
        ["✓"] = '',   // Known hosts: check mark
        ["✦"] = '',   // AI items and the AI section: robot
        ["▤"] = '',   // Reports: report
        ["?"] = '',   // Help & FAQ
        ["◇"] = '',   // Add-ons: puzzle piece
        ["⛨"] = '',   // Privacy & network / Security & privacy: shield
        ["☰"] = '',   // Audit log: checklist
        ["⚙"] = '',   // Settings / SYSTEM: gear
        ["📌"] = '',  // PINNED
    };

    /// <summary>The Fluent icon for a screen's glyph, or null when there is none (draw the glyph itself).</summary>
    public static char? For(string glyph) => ByGlyph.TryGetValue(glyph.Trim(), out var c) ? c : null;

    /// <summary>The PINNED section's icon.</summary>
    public const char Pin = '';
    /// <summary>Unpin (Segoe Fluent Icons U+E77A): a pinned sidebar page's hover button (0.6.2).</summary>
    public const char Unpin = '';
    /// <summary>The expand / collapse chevrons of sidebar sections.</summary>
    public const char ChevronDown = '', ChevronRight = '';
}
