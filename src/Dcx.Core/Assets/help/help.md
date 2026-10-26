# DarkCipher ServerManagement — built-in help

## Getting started
keywords: start, first, setup, begin, overview, tour, new
DarkCipher ServerManagement is an IT engineering workstation for Linux servers and Vultr cloud. Typical first steps:
1. Sidebar → Credentials → ＋ Add: add an SSH key (paste or load a private key) or a password. Everything is stored encrypted.
2. Sidebar → Hosts → ＋ Add host: name, address, port, user and the credential to use. Or import: "⇩ Import ~/.ssh/config", "⇩ Import from Vultr" or "⇩ Import from Contabo".
3. Double-click a host for a terminal, or right-click it for Files, Manage, External terminal and more.
4. Optional: Settings → Vultr accounts or Contabo accounts to manage cloud servers, and Settings → AI engine to turn on the local AI.
Press F1 anywhere to open this help. Right-click almost anything for its actions.

## Quick connect and ad-hoc connections
keywords: quick connect, ad-hoc, adhoc, ctrl+k, user@host, connect without saving, one-off, pin, pinned, history, recent, connect button, protocol, sftp, rdp, web ui
Pick a protocol with the button on the left of the header box ("ssh ▾"): SSH terminal, Files (SFTP), Remote Desktop, Telnet (while allowed), serial console, local shell, web UI (in-app browser) or your own SSH client. Type user@host, user@host:port, a bare host or the name of a saved host and press Enter or **Connect** (Ctrl+K focuses the box).
**Dropdown:** ↓ in the box (or ▾) lists 📌 pinned lines, then recent ones, then saved hosts matching what you type; move with the arrow keys and press Enter, or click. 📍 pins a line to the top, 📌 unpins it. Each line remembers its protocol: `rdp://admin@host`, `sftp://ops@host`, `telnet host 2323`, `COM3 115200`, `wsl Ubuntu`, `https://nas:5001`, `ext://ops@host`. Typing such a prefix picks that protocol by itself.
The "⋯" button next to it opens the full ad-hoc dialog: choose the auth method (password, key from the vault, agent), a jump host, port and whether to open a terminal, files, the manage view or an external Windows Terminal. Ad-hoc connections are not saved as hosts.
Quick connect tries the key marked "★ Use for quick connect" in Credentials first.

## Hosts
keywords: hosts, servers, add host, edit host, groups, tags, favourite, import, reachability, jump host, bastion, pin hosts, pinned, dock, side panel, auto-collapse, keep hosts open, already open, switch to tab, new connection, duplicate session, too many tabs, session manager, securecrt, host pane, host tree
Sidebar → Hosts lists saved servers in a folder tree (see "Host folders"). Filter by folder, favourites or text (name, address, user, tag).
Toolbar and right-click: Terminal, Files, Manage, External (Windows Terminal + OpenSSH), Edit, Duplicate, Toggle favourite, Install key… (ssh-copy-id from the vault), Copy address, Check reachability (tests the SSH port of every listed host), Import from Vultr / Contabo, Import ~/.ssh/config, Delete.
A host can go via a jump host (bastion). Hosts can have a separate sudo password credential.
The folder pane on the left of the Hosts page can be pinned open or auto-collapse, like SecureCRT: the button at the top of the pane (or Hosts → More ▾, or Settings → Navigation → Sidebar → Hosts folder pane). **📌 Pinned** (the default) keeps it open beside the hosts; drag its edge to resize. **📍 Auto-collapse** folds it to a narrow "FOLDERS" strip inside the Hosts page, so the host list gets the room; point at the strip (or click it, or drag hosts onto it) and the folders slide out over the list, then slide back when you move away or click into the list. Click the pin again to switch. Sessions still open in their own tabs. Administrators can set it by Group Policy (HostsDock: pinned / auto).
Sessions you open from the Hosts page (Connect, double-click, Files, Manage, Remote Desktop, VNC, File commander) open as tabs **inside the Hosts tab**, next to its first tab **All hosts** (your host list), so the folder pane stays beside the session: pinned, or the slim Hosts tab that slides out (over a session it opens beside it, as nothing can be drawn over a terminal or remote desktop). While a session is in front, the title and toolbar step aside; click All hosts to get them back. The tabs inside split, drag out to the main window or another window, and "switch to the open tab" finds them; other screens (Settings, Notes…) still open as their own tabs; closing the Hosts tab moves its sessions to ordinary tabs. Prefer separate tabs: Settings → Navigation → Sidebar → Open sessions from Hosts → As separate tabs (or Hosts → More ▾; Group Policy HostsOpenIn).
The pane on the left of the Hosts page (HOSTS) is a full host pane: your hosts sit inside their folders (and under the categories of the other views, Favourites and shared collections). Double-click or Enter connects; right-click a host for its full menu (the same as in the list) or a folder for the folder's; drag a host onto another folder to move it; Filter hosts… narrows the tree to matching hosts; ＋ Host adds one to the selected folder. Selecting a host also selects it on the All hosts tab. A folder behind a closed lock shows nothing inside until you expand and unlock it.
Already open? Connecting to a host that has a terminal, Remote Desktop or VNC tab open switches to that tab instead of opening another, and so do Files, the file commander and local shells (0.5.9). To open a second session on purpose: hold Shift as you connect, or right-click the host → ⧉ New connection (another tab), or right-click the tab → ⊞ Duplicate session. Turn it off in Settings → Terminal ("Switch to an open tab of the same host…") or by Group Policy (ReuseOpenTabs).

## Terminal sessions
keywords: terminal, ssh, shell, xterm, copy, paste, font, scrollback, session, tmux, htop, vim
Terminals use xterm.js (xterm-256color, mouse, alternate screen — htop, vim, tmux all work).
Copy: select with the mouse then Ctrl+Shift+C. Paste: Ctrl+Shift+V. Plain Ctrl+C is still an interrupt.
Right-click the terminal for the session menu: copy, paste, select all, clear, reset, font size, send control keys (Ctrl+C, Ctrl+D, Ctrl+Z, Ctrl+\\), reconnect, disconnect, duplicate, open files/manage for the host, ask the AI about the selection, split or pop out the tab.
Settings → Terminal: "Right-click" can be "menu" (Shift+right-click pastes) or "paste" PuTTY-style (Shift+right-click opens the menu).
Font size: A− / A+ in the toolbar or the right-click menu. Terminal font and default size: Settings → Typography. Scrollback size: Settings → Terminal & connections.
Links printed by a server (OSC 8 hyperlinks) never open inside the app: clicking one shows the full address and asks before opening it in your default browser (web links only).

## tmux from the terminal
keywords: tmux, multiplexer, persistent session, reattach, screen, tmux config, tmux.conf, install tmux
On an SSH session to a Linux or macOS host, the terminal's ⧉ tmux button starts tmux or reattaches your session there (default name "main"), so long jobs survive a dropped connection. If tmux isn't installed, it asks whether to install it with the host's package manager (apt, dnf, yum, zypper, pacman, apk or brew); sudo asks for its password right in the terminal. Settings → Terminals & connections → tmux: the session name, whether to offer the install, and **your own tmux config** — paste it and Save; the next ⧉ tmux writes it to ~/.config/dcx-servermanagement/tmux.conf on that host (only you can read it) and uses it. Your own ~/.tmux.conf is never changed. Inside tmux the button just says you're already in it.
**Always in tmux:** Settings → Terminals & connections → tmux → "Always start SSH terminals inside tmux" takes every SSH terminal to a Linux or macOS host straight into your tmux session, so a dropped connection or a closed laptop never loses your work. A host (host editor → tmux: Always / Never) or its folder (folder defaults) can say otherwise for itself. A host without tmux says so in the status line and connects normally: tmux is only ever installed from the ⧉ tmux button, after asking. Detach or exit tmux and that tab stays out of it until you reconnect.

## Alerts from terminals you aren't looking at
keywords: alert, notify, notification, bell, finished, long command, done, background tab, flash, taskbar, dropped, monitor output, activity
When a terminal tab you aren't looking at needs you, its tab gets a dot, the taskbar button flashes, and (when the app isn't in front) Windows shows a notification; click it to go straight to that tab. What alerts (Settings → Terminals & connections → **Alerts**):
- **A long command finished:** you pressed Enter, and the screen shows a prompt again after at least 30 seconds (your choice; 0 = never). It reads the screen, so it works inside tmux too; a question such as `[y/N]` or a password prompt isn't a prompt and keeps waiting.
- **The bell** (BEL), as many build scripts ring when they finish.
- **A dropped session.**
- **A highlight rule marked "alert"** (see keyword highlighting).
At most one alert of a kind per tab every 10 seconds. A tab's menu → Session → "🔔 Alerts for this tab" switches its own off. Alerts stay on this PC. Group Policy can set each setting.

## Keyword highlighting and Ctrl+click
keywords: highlight, highlighting, keyword, colour, color, error, warning, regex, regular expression, ip address, click, link, path, url, ctrl+click, trigger
Terminal output is coloured as it arrives: **errors and failures** red, **warnings** yellow, **success** green, **network addresses** (IPv4, MAC, IPv6) cyan, **HTTP status codes** by class. Pick the packs and add your own rules in Settings → Terminals & connections → **Highlighting & button bar**: a name, a regular expression, a colour, Match case, and "Alert when it shows in a tab I'm not looking at". Your rules draw on top of the packs. Only the drawing changes: the output, what you copy and the session log are untouched, and full-screen programs (vim, top, htop) are left alone. Every pattern is checked before it's used: empty matches, inline options like (?i) and patterns that backtrack badly on long lines are refused. Long lines are coloured up to 2,000 characters.
**Ctrl+click** an IP address, an absolute path or a web link in the output for a menu: SSH to that address, add it as a host or copy it; open a path in Files (SFTP), paste it at the prompt quoted, or copy it; open a link in your browser (after showing it) or copy it. Nothing happens without a click in that menu.

## Terminal profiles: colours, cursor, encoding, key mappings
keywords: terminal profile, colour scheme, color scheme, solarized, green screen, cursor, underline, bar, blink, encoding, latin-1, iso-8859-1, cp437, ibm437, windows-1252, key mapping, keymap, backspace, ^H, per-host profile, per-session
Settings → Terminal profiles. A profile holds:
- **Colour scheme:** the app theme, Classic, Solarized dark / light, or Green screen.
- **Cursor:** block, underline or bar; blinking or steady.
- **Encoding:** UTF-8, ISO-8859-1, Windows-1252 or IBM437, for SSH and Telnet output and input. Local shells are always UTF-8; serial consoles have their own setting.
- **Scrollback:** 1,000–200,000 lines, or 0 to use the Terminal setting.
- **Backspace sends ^H.**
- **Logging:** on or off, with or without timestamps.
- **Key mappings:** one per line, `keys = text`. For example `Ctrl+Shift+F5 = \e[15;6~` or `Alt+K = ^C`. Escapes: `\e` (Esc), `\r`, `\n`, `\t`, `\xHH`, `\uHHHH`, `^X`. Plain letters need Ctrl or Alt, so a mapping never swallows normal typing. "Apply key mappings" lists any line it can't use.
**Where a profile applies:**
- One profile is the default (★ Make default).
- A host can use its own (host editor → TERMINAL PROFILE).
- Any terminal tab can switch for its session only: right-click → Profile (this session).
- Editing a profile updates the open tabs that use it.

## Search the terminal scrollback
keywords: find, search, scrollback, ctrl+shift+f, regex, regular expression, match case, history, grep output
Press Ctrl+Shift+F in a terminal, or the ⌕ button, or right-click → Find in scrollback. Type to search the whole scrollback. Lines the terminal wrapped count as one line.
- The newest match is selected first. Enter (or ▲) goes to older matches, Shift+Enter (or ▼) to newer ones. The bar shows "n of m".
- **Aa** matches case; **.\*** treats the text as a regular expression. An invalid expression is reported, never guessed.
- Esc closes the bar.

## Session logging (encrypted, timestamped)
keywords: session log, logging, log to file, record session, timestamp, audit terminal, putty log, export log, session logs
Turn logging on for a profile (Settings → Terminal profiles → "Log every session…"), or for one tab (right-click → Session log → Log this session).
- **What's logged:** the session's output, made readable: colour codes and cursor movement removed, progress bars as their final state. With timestamps on, each line starts `[yyyy-MM-dd HH:mm:ss]`.
- **Stored encrypted like everything else,** in the store's sessionlogs folder. The file name has no host name in it. Nothing is written while the app is locked.
- **Sidebar → Session logs** lists them; select one to read it (decrypted in memory). You can also search it, delete it, or "Export as text…". The export is plain text, NOT encrypted, so the app asks first and warns if the folder is synchronised.
- **For every session, or never (0.4.0):** Settings → Terminal & connections → **Session logs**: *per profile* (as above), *on* (every session is logged whatever its profile says; the terminal menu shows it can't be turned off) or *off*. The same as the Group Policy `SessionLogs`, so a company can force logging.
- **Forwarded (0.4.0):** tick "Terminal session logs" under Settings → Integrations → Syslog (TLS) or Log sinks to send every logged line (masked, msgid / kind `session`) to your collector or SIEM as it is written.

## Broadcast input to several terminals
keywords: broadcast input, multi-input, type in many terminals, simultaneous input, cluster ssh, synchronize panes, multiexec
Right-click a terminal → ⌨ Broadcast input:
- to all open terminals;
- to the terminals in this tab group;
- to the terminals in this pane.
Once it's on, what you type or paste in any of them is sent to all of them.
- **It asks first,** listing the terminals by environment. If any is PRODUCTION, you type PRODUCTION.
- **Every member shows a yellow strip,** "INPUT GOES TO n TERMINALS", with ■ Stop.
- **Ending it:** closing members until only one is left ends it.
- **Pastes** are checked once in the terminal where you pasted (multi-line and risky pastes are shown first), then sent to all.
- **Not the same as "Broadcast to OBS":** that shares a terminal's screen as a picture for streaming.

## Tab groups
keywords: tab group, group tabs, label tabs, colour tabs, workspace group, change group
Right-click a tab → Tab group → New group… (or pick an existing group). The group's name is printed in its colour on the first tab of the group, and every tab in the group has a line in that colour underneath, so a group takes little room. Long tab titles (portal pages) end in "…" (the full title shows on hover), a little shorter inside a group. Groups last for this session, and come back with saved sessions and named workspaces (see Saved sessions and workspaces).
The "Group …" menu has: next tab in the group, broadcast input to the group's terminals, reconnect or disconnect them all, and close the group's tabs.

## Telnet (legacy devices)
keywords: telnet, port 23, legacy, switch, router, console server, unencrypted, nvt
Telnet is off by default because it sends everything, your password included, unencrypted. To allow it, go to Settings → Terminal & connections → "Allow Telnet". It asks first.
- **Opening a session:** the host editor gets a Telnet type, and quick connect accepts `telnet 192.0.2.10`, `telnet switch-01 2601` or `telnet://host:port`.
- **Signing in:** you sign in at the device's own prompt. The app never types credentials over Telnet.
- **Window size and terminal type** are sent to the device (NAWS, XTERM-256COLOR), and Enter is sent as CR LF.
- **Encoding:** use a terminal profile with ISO-8859-1 or IBM437 for old devices with box drawing or accents.
- **Visible warnings:** the status line says "⚠ Telnet: unencrypted", and the Hosts list shows "Telnet ⚠".
- **Offline mode and logging:** offline mode applies, and sessions are in the audit log.
- **Not available:** Files, Manage and the File commander need SSH, so they're not offered for Telnet hosts. Telnet through a jump host isn't supported.

## Fonts, text size and typography
keywords: font, fonts, typography, text size, ui font, monospace, editor font, terminal font, readability, zoom, scale, accents
Settings → Appearance → Typography (text size and zoom are under Layout and text). Everything applies immediately — every window, dialog, menu, tooltip, pop-out and AI panel — and is kept after restart.
- UI font: all interface text (default Segoe UI Variable / Segoe UI, Windows' own reading-optimised font).
- UI scale (zoom everything): scales the whole interface, including spacing, icons, menus and tooltips (default 110 %). Terminals keep their own size.
- UI font size (text only): makes interface text larger or smaller (85–150 %, default 100 %) without enlarging spacing, borders or icons. It combines with UI scale. Terminals keep their own size.
- Dialogs and fixed windows (host editor, credential editor, Vultr forms, document viewer, web console) grow with both settings, never beyond the screen; when they would be taller than the screen, their content scrolls. Documents open at the interface's size (zoom with − / + / 100 %). Web consoles and portals stay at 100 % (Ctrl+mouse wheel zooms them).
- Code / monospace font: addresses, ids, commands, data. Notes / editor font: the SFTP/Commander editor and other multi-line editors.
- Terminal font and size: terminals only. Changing UI fonts never changes terminal character cells, and the terminal font never changes the interface. Remote Desktop content is never touched.
- "Terminal-style accents" puts buttons, tabs, sidebar and labels in the monospace font (the original look).
If a chosen font is uninstalled, the app uses the default and Settings shows a ⚠ line; "Re-scan installed fonts" picks up new ones. "Reset typography to defaults" restores everything.

## Engineering overview (dashboard)
keywords: dashboard, overview, engineering overview, home, active sessions, sessions by type, infrastructure status, recent notes, recent connections, transfers, warnings, polling, check now, customise dashboard, hide card
The Dashboard (first tab, sidebar → OVERVIEW) is an overview of what is going on in the app:
- **Active sessions:** open session tabs by type (SSH, Remote Desktop, serial, local shell, WSL, files, server views), connected / open. PRODUCTION sessions are listed first. Click a row to go to that tab; right-click it to disconnect or close it.
- **Local application warnings:** problems the app found in itself, each with a button that goes to the fix. They cover store recovery, unreadable notes or incidents, failed or overdue snapshots, data in a synchronised folder, errors logged since start, failed transfers, an internet AI provider or the local API being on, and add-ons blocked by offline mode.
- **Environment & safety:** offline mode and blocked attempts, open PRODUCTION sessions, hosts by environment, the local API and broadcasts, and the app lock.
- **Infrastructure:** your Docker / Proxmox / Unraid providers with their last-known state, and when and where it was checked.
- **Other cards:** active incidents, transfers (running and recent), recent connections, recent notes, Vultr (balance and instances) and recent activity.
- **Rearranging (0.4.0):** Customise ▾ → **Unlock dashboard** (or the 🔓 button) lets you **drag a card onto another** to put it there; **Lock** when you're done — the order is kept. Right-click a card to move it up or down or hide it; Customise ▾ → Show all / Reset puts the default back.
**What it contacts, and when.** Everything except the remote checks is read from this PC as it happens; nothing is polled. Remote checks (the Vultr and Contabo accounts, the AI engine, each infrastructure provider) follow the rule shown under the title. The **Cloud servers** card lists the servers of every connected cloud account; if one provider's API fails, only its part of the card says so. Change the rule with **Checks ▾**:
- only when you press **↻ Check now**;
- when the dashboard opens (the default, at most once a minute);
- every 5 / 15 / 60 minutes, but only while the dashboard is on screen.
Each source is included or not in the same menu. Infrastructure providers are not included unless you add them, so until you check, the dashboard shows what the Infrastructure screen last saw. With offline mode on, a check offline mode would refuse is skipped, not attempted. The Privacy & network view shows the same rule.
**Customise:** right-click a card (or its ⋯ button, or Customise ▾) to move it up or down, hide it, show hidden cards or reset the layout. Cards sit in two columns when there is room, otherwise one.
**Pinned checks (0.3.0):** Customise ▾ → 📌 Pin a check…, or 📌 in the toolbox. Kinds: ping; a TCP port (open?); an HTTP(S) address (status and time, with its certificate's days left); a TLS certificate (days left, warn below N); the performance of a saved host over SSH (load per core, memory, disk, uptime — warn above N %); a mail domain (SPF + DMARC problems). Each row shows its state (● OK, ✕ failing, ○ not checked), the headline, when, and a small history bar; right-click to check, edit, pause, copy or remove it; the Checks tile counts failing ones. They run by the same rule as the other remote checks (Check now / when the dashboard opens / the interval while it is on screen — each tile can also set its own minimum interval), never in the background on their own. A change of state is written to the audit log (so to syslog) and e-mailed when notifications are on. Results live in the encrypted store.

## Environment labels (PRODUCTION / TEST / LAB / LOCAL)
keywords: environment, production, prod, test, staging, lab, development, dev, local, label, colour, color, tab colour, safeguard, type the host name, live
Give each host an environment in the host editor (ENVIRONMENT), or for many hosts at once: Hosts → select → right-click → Environment ▸. Choices: PRODUCTION, TEST, LAB, DEVELOPMENT, LOCAL or not set. Local PowerShell / cmd / WSL tabs are LOCAL automatically.
- Where you see it: an Environment column in the host list; on each tab for that host (terminal, Remote Desktop, server view, files), a label (PROD / TEST / LAB / DEV / LOCAL) plus a coloured top edge, including popped-out windows; a label at the top of terminal, Remote Desktop, server and file views; infrastructure providers; new incidents opened from a host. The text is always printed, so it reads without colour.
- PRODUCTION safeguards: reboot / shut down / Vultr power cycle, firewall changes, service and container actions, "Run suggested command", AI operator approvals, deletes on the server (Commander, files), sync runs that change the server, all ask you to **type the host's name** (letter case doesn't matter; press Enter or the button once it says "✓ matches"). They ask even if "Ask before every change" is off. Multi-host runs list the hosts by environment, and if any is PRODUCTION you type PRODUCTION. A multi-line paste into a PRODUCTION terminal is always shown first.
- Removing the PRODUCTION label asks first (it removes the safeguards). Every environment change is in the audit log.
- Scripts (local API) and add-ons with hosts.read see each host's environment.

## Offline / air-gapped mode
keywords: offline, air-gapped, airgap, air gap, block internet, no internet, isolated network, approved destinations, allow list, whitelist, restricted network
Settings → Offline mode (or click the lock text in the status bar → Turn offline mode on) blocks every destination outside this PC and your private network, except the ones you approve. While it is on, a green ⛔ OFFLINE MODE badge sits in the status bar with the number of blocked attempts; click it to open the settings.
- Checked before any connection opens: AI engines, the Vultr API, Proxmox / Unraid, SSH / SFTP / SCP and tunnels (and jump hosts), Remote Desktop (embedded, mstsc and RD Gateway), the external OpenSSH terminal, web windows (the Vultr portal and console, every page and request inside them), links opened in your browser, and add-ons.
- Allowed: this PC, private / LAN addresses (10.x, 172.16–31.x, 192.168.x, 100.64–127.x, fd00::/8, link-local), single-label and .lan / .local / .internal / .home.arpa names, and the approved list.
- Approved destinations, one per line: a host name (api.vultr.com), *.suffix (*.corp.example.com), an IP, or a CIDR range (203.0.113.0/24, 2001:db8::/32). A /0 range is refused.
- "Allow DNS names whose every address is private": other names are looked up, and allowed only if every address is private. A connection then goes to exactly the address that was checked. Untick it to avoid DNS lookups; such names then need approving.
- Add-ons are separate programs that the sandbox can't keep off the network yet, so they don't run while offline unless you tick "Allow add-ons while offline".
- Every block is refused immediately with the reason, listed under "Blocked this session" and written to the audit log. Redirects are never followed by the app's web clients.
- Not covered: Windows' and the WebView2 runtime's own update / SmartScreen services, what a server reaches by itself (a `curl` you type, the far end of a tunnel), and what an external program (mstsc, ssh.exe, Claude Code) does after it starts.

## Privacy & network dashboard
keywords: privacy, network activity, listening, ports, posture, security dashboard, telemetry, tunnels exposed, 0.0.0.0, what leaves
Sidebar → SYSTEM → Privacy & network shows, from live state:
- Security & privacy posture: store encryption, key protection, 2FA, app lock, sandbox, whether the data folder is synchronised, external AI, secret masking, paste protection — update checks (DISABLED until signed releases exist), and features that do not exist (local API, broadcast, add-ons) as NOT IMPLEMENTED rather than "off".
- Network activity: every destination the app is configured to reach with its class (● local, ◆ on-premises, ◇ user-configured, ⚠ internet) and when it is used. An internet AI endpoint shows BLOCKED while internet AI providers are disabled. Saved hosts are summarised by class as operational traffic.
- Listening services: tunnels (with who can reach them — "this PC only" for 127.0.0.1, ⚠ "ALL interfaces" for 0.0.0.0), the Ollama-over-SSH forward and jump-host relays.
- **Syslog (TLS)** box: off, or the state (connecting / connected / failing — queued / blocked by offline mode), the server, TLS version, whether the certificate is pinned, the certificate's name and expiry (⚠ within 30 days), when it last sent, how many messages wait in the queue, the last error, and **Send test message**.

## Syslog over TLS
keywords: syslog, siem, rfc 5424, rfc 5425, tls, 6514, audit log forwarding, log shipping, graylog, splunk, wazuh, rsyslog, collector, mutual tls, client certificate, pin
Settings → Integrations → **Syslog (TLS)** sends the audit log and the application (error) log to your collector as RFC 5424 messages over TLS (RFC 5425 framing, TLS 1.2 or 1.3; port 6514 by default). There is no UDP or unencrypted TCP option at all.
- **Trust:** the collector's certificate is checked by Windows, or — for a self-signed collector — pin its SHA-256 fingerprint (or its CA's). Optional client certificate (mutual TLS): the thumbprint of a certificate with its private key in your Personal certificate store.
- **What is sent:** each audit line (who, what, target, result) and each error log entry, masked first (keys, tokens, passwords never leave). Structured data uses `[dcx@32473 actor="…" target="…" result="…"]`; facility local0.
- **When the collector is down:** messages wait in an encrypted queue in your store (up to 5,000; the oldest go first, and the number lost is reported when it's back). Retries back off from 5 seconds to 5 minutes. Offline mode applies: a collector outside your network or approved list isn't contacted.
- **Admin protection:** every syslog setting needs administrator approval (UAC) to change, so nothing running as you can silence or redirect the audit trail. Each is also a Group Policy (category "Syslog, log sinks and notifications").
- **Session logs too (0.4.0):** tick "Terminal session logs" to forward every logged session's lines (msgid `session`); which sessions are logged is decided by Terminal & connections → Session logs (per profile, always, or never — a Group Policy can force it).

## Observability: log sinks (Elastic, Loki, Azure Monitor / Sentinel, Splunk, Graylog, webhooks)
keywords: observability, log sinks, elasticsearch, opensearch, loki, grafana loki, azure monitor, sentinel, log analytics, data collection rule, dcr, splunk, hec, graylog, gelf, webhook, siem, log shipping, bulk api
Sidebar → ◉ **Observability** shows where this app's own events go and how each sink is doing. Settings → Integrations → **Log sinks** adds them. The events are the same ones syslog gets — the audit log, the application (error) log and, if you choose, terminal session logs — one JSON document each (`@timestamp`, app, host, user, kind, severity, actor, target, result, session, message), masked before anything leaves.
- **Elasticsearch / OpenSearch:** the server URL and an index; bulk API (`/_bulk`), API key or user name + password. Per-item refusals (mapping errors) are reported as the sink's error.
- **Grafana Loki:** the server or Grafana Cloud logs endpoint; one stream per event kind with labels `app`, `host`, `kind` (and `job` when you set one); multi-tenant X-Scope-OrgID; Grafana Cloud = instance id + access-policy token as user name + password.
- **Azure Monitor / Microsoft Sentinel:** the Logs Ingestion API — the data collection endpoint URL, the rule's immutable id (dcr-…) and the stream name (Custom-…_CL). It signs in as one of your Azure subscriptions' app registrations (Settings → Cloud providers), which needs the *Monitoring Metrics Publisher* role on the rule. Sentinel reads the same Log Analytics workspace.
- **Splunk:** the HTTP Event Collector URL and its token (sourcetype `dcx:servermanagement`). **Graylog:** a GELF HTTP input, one message per event. **Webhook:** any HTTPS endpoint that takes a JSON array (bearer token, API key header or basic auth).
- **Rules:** HTTPS only (TLS 1.2+; pin a self-signed certificate's SHA-256), never user names in URLs, up to 12 sinks. While a sink can't be reached its events wait in an encrypted queue (up to 5,000; per-sink back-off from 5 s to 5 min); the number lost is reported when it's back. Offline mode applies. A sink that had worked and now fails is e-mailed about (Notifications → "syslog or a log sink is failing").
- **Admin protection and policy:** the sink list and the send switches need administrator approval; the list is also a Group Policy (`ObserveSinks`, JSON without secrets). Each sink's key, password or token is stored encrypted on this PC only and is never a policy or shown again.

## Observability: dashboards (Grafana, Kibana, Sentinel, SIEM links)
keywords: dashboards, grafana, kibana, opensearch dashboards, sentinel, splunk, siem, grafana api, service account token, in-app browser
Observability → **Dashboards** lists your Grafana dashboards (Settings → Integrations → Dashboards & SIEM links: the Grafana URL, optional organisation id and a service-account token with the Viewer role, stored encrypted) and opens any of them in the app's own browser window; double-click a row. Links to Kibana, OpenSearch Dashboards, Microsoft Sentinel, Splunk or any SIEM are listed as `Label=https://…`, one per line, and open the same way. The Grafana URL and the links can be set by Group Policy (`GrafanaUrl`, `GrafanaOrgId`, `ObserveLinks`). The app only ever opens pages on the Grafana server itself: a dashboard URL that points anywhere else is refused. The **Activity** box shows how many events were shipped per minute over the last hour.

## E-mail notifications
keywords: email, e-mail, notifications, alerts, smtp, starttls, 465, 587, app password, incident alert, host down alert, failed backup, security alert, rate limit
Settings → Integrations → **E-mail notifications** sends short e-mails when: an incident is opened, a host goes down (scheduled checks), a backup or snapshot fails, a security event happens (wrong passwords and refused sign-ins, AI lock changes, protected-setting changes, the local API locking itself, an unconfirmed sensitive action), or syslog forwarding fails.
- Only over TLS: port 465 (TLS from the start) or 587 with STARTTLS — a server without STARTTLS is refused before anything is said, and the password is only ever sent inside TLS. Use an app password where your provider offers one; it is stored encrypted on this PC and is never a Group Policy.
- At most the number per hour you set (10 by default); the rest are listed in the next e-mail. **Send test e-mail** checks the set-up.
- The server, sender, recipients and the security / syslog switches need administrator approval to change.

## Safe pasting into terminals
keywords: paste, multiline, multi-line, clipboard, bracketed paste, control characters, dangerous command, ctrl+v
Every paste into a terminal (Ctrl+Shift+V, Ctrl+V, right-click, menu, "Paste selection") is checked first:
- Hidden control characters (escape sequences, including the one that ends bracketed paste early) are always removed.
- Multi-line text, a trailing line break (which would press Enter), risky commands (rm -rf /, mkfs, dd to a disk, curl | sh, reboot/shutdown, flushing the firewall, stopping sshd) and very large pastes are shown first, with the lines and an honest note: if the shell has bracketed paste on, nothing runs until you press Enter; otherwise each line runs as it arrives. "Paste without the final Enter" leaves the last command for you to check.
- A single plain line pastes straight away. Turn the confirmation off in Settings → Terminal & connections. Dragging text onto a terminal is blocked.
The app never copies stored passwords or keys to the clipboard (only public keys and addresses), so there is nothing to clear afterwards.

## Network classes and external connections
keywords: internet, external, local, on-premises, network, privacy, telemetry, cloud, warning, endpoint, lan, trust boundary
Settings marks every address the app can send data to: ● LOCAL / OFFLINE (this PC), ◆ LOCAL NETWORK / ON-PREMISES (private addresses, LAN names), ◇ USER-CONFIGURED NETWORK ENDPOINT (a DNS name the app can't place — treated as not local) and ⚠ EXTERNAL NETWORK / INTERNET.
Anything that lets data leave your network sits in a ⚠ box with an explanation, and switching it on asks once ("Enable external connection?") naming the destination. Internet AI providers are off by default.
The app has no telemetry, analytics, crash upload or update polling. SSH, SFTP, Remote Desktop and tunnels to your own hosts are normal operational traffic you start yourself; Vultr is contacted only after you add an account.

## Local shells and WSL
keywords: local, powershell, pwsh, cmd, command prompt, wsl, ubuntu, debian, linux on windows, shell, conpty, windows terminal
Sidebar → LOCAL lists the shells on this PC: Windows PowerShell, PowerShell 7 (if installed), Command Prompt, and every installed WSL distribution (Docker Desktop's internal ones are hidden). Click one for a terminal tab; click again for another. Quick connect also accepts local, pwsh, cmd, wsl or "wsl Ubuntu".
They use the same terminal as SSH (copy/paste, safe paste, search, screenshots, AI, split, pop out) through Windows' pseudo console (ConPTY). They run as you, never elevated. Files/Manage are SSH-only and don't appear on local tabs.
When the shell exits the tab stays open with its output: press Enter or ↻ for a new shell (local shells are never restarted automatically). Closing the tab ends the program running in it. After a crash, local tabs are offered for restore like host tabs.

## Serial and USB-serial consoles
keywords: serial, com port, com3, usb serial, console cable, rollover, ftdi, prolific, cp210x, ch340, baud, 9600, 115200, 8n1, break, rommon, cisco, putty serial
Sidebar → LOCAL → Serial / USB consoles lists saved consoles and the ports Windows reports now (friendly name and USB identity; the list updates every 2 seconds while open). Double-click a port to open it at 9600 8N1, right-click for 115200 or "Save as a profile". Quick connect also takes COM3, COM3:115200 or "COM3 9600 8N1".
A profile stores port, baud, data bits, parity, stop bits, flow control (none, XON/XOFF, RTS/CTS), what Enter sends (CR for most network gear, LF, CR+LF), encoding (UTF-8, Latin-1, ASCII), local echo, DTR/RTS and auto-reconnect. Profiles are encrypted with your hosts and included in backups.
"Follow this USB adapter" remembers the adapter's identity (vendor, product, serial number): if Windows gives it a new COM number after you use another USB socket, the profile still finds it — and it never opens a different device that now has the old number.
Unplugging the cable pauses the tab ("device removed"); with auto-reconnect it reopens when the adapter is back. ⎍ Break (toolbar, or right-click → Send key → BREAK) sends a 300 ms serial break for ROMMON / password recovery. "Port is in use" means another program (PuTTY, another tab) has it open — a port can only be opened once, so serial tabs have no Duplicate.

## Notes and the knowledge base
keywords: notes, notebook, notepad, knowledge base, search notes, timestamp, log, incident notes, markdown, history, revisions, trash, tags
Sidebar → DOCUMENTS → Notes. Every note is encrypted in its own file and searchable offline — no AI needed.
Search box: plain words (all must match, prefixes work: "certif" finds "certificate"), "exact phrase", tag:nginx, host:web (by host name or address), is:fav, today, yesterday, last tuesday, this week, last week, after:2026-10-01, before:…, on:…. Natural questions work too: "everything I wrote about PROD-WEB-04", "DNS problems", "notes from last Tuesday" (filler words are ignored; a word that appears in no note is ignored and shown as such).
Per host: right-click a host → Notes for this host, or the tab menu → Notes. In a terminal select text → right-click → Add selection to notes: it is appended as a timestamped code block to that host's (or session's) "log" note.
Editor: Time ([HH:mm:ss], also Ctrl+Shift+T), Date+time, ISO, Host, Code block, Paste as code, Task, Heading. Autosave 1.2 s after you stop typing; Ctrl+S keeps a version. History… lists earlier versions (one every 10 minutes while editing, up to 30) — restoring keeps the current text as a version too.
Move to Trash is reversible; only "Empty trash" deletes notes for good (after a confirmation). Encrypted backups include notes; a host selection export includes only that host's notes.
✦ Summarise / Extract actions / Organise / Ask about selection send only this note (or the selection), only when you click.

## PDFs, documents and screenshots with AI
keywords: pdf, extract text, ocr, scanned pdf, review pdf, screenshot ai, vision, image, analyse screenshot, redact, black out, llava, qwen2.5vl, llama3.2-vision
module: ai
PDFs: the document viewer reads the PDF's real text (PdfPig) and says what it got — "PDF TEXT EXTRACTED · 3 of 3 pages", "PARTLY EXTRACTED" or "NO MACHINE-READABLE TEXT" (a scanned/image-only PDF: OCR would be required, and the app has no OCR, so nothing is sent). Password-protected PDFs are not opened.
✦ Review with AI (viewer) and ✦ Explain with AI on a PDF in File commander open "DATA BEING SENT TO AI": choose all pages, a page range or page 1; see how many secret-looking values were masked, where it goes (● local / ⚠ internet) and the exact text. Nothing is sent until you click Send; the viewer then shows "PDF CONTENT PROVIDED TO AI (pages …)".
Screenshots: every 📷 menu (terminal, tab strip, Remote Desktop) has "✦ Analyse with AI (vision model)…": preview the image, drag boxes to black out anything private (they are burned into the image), pick Analyse / Explain the error / Read the visible message / Summarise, and send. The app first checks that your model can read images — Ollama reports it (use a vision model such as qwen2.5vl, llama3.2-vision, gemma3 or llava), Claude models can, an OpenAI-compatible server only if you tick "This model accepts images" in Settings → AI engine, the NPU engine cannot. If it can't, nothing is sent and the window says why. The screenshot appears as an item in the AI sidebar's CONTEXT; untick or remove it there.

## Infrastructure: Docker, Proxmox and Unraid
keywords: infrastructure, docker, containers, docker desktop, proxmox, pve, vm, lxc, hypervisor, api token, start, stop, shutdown, verified, read-only, unraid, array, parity, api key, graphql, snapshot, rollback, clone, migrate, backup, vzdump, template, pause, kill, inspect, stats, volume, image
Sidebar → SERVERS → Infrastructure. ＋ Add: Docker on a saved host (runs docker over SSH; the login needs the docker group or sudo), Docker on this PC (Docker Desktop's named pipe), Proxmox VE (API token), Unraid (API key), VMware vCenter or a Hyper-V host (see their own sections).
Proxmox: create a token under Datacenter → Permissions → API Tokens (least privilege: a dedicated user with PVEAuditor on /, plus PVEVMUser on /vms only if you want start/stop; add VM.Snapshot, VM.Clone, VM.Migrate, VM.Backup (+ Datastore.AllocateSpace on the backup storage) or VM.Allocate only for the actions you want), store it under Credentials (user = user@realm!tokenid, password = the secret) and pick it. Proxmox uses a self-signed certificate: the first connect shows its SHA-256 — compare it with `pvenode cert info` on the host, then Trust certificate. If it ever changes, the app refuses it until you re-check.
Unraid: needs Unraid 7.x (or the Unraid Connect plugin) with the API enabled. Create an API key (Settings → Management Access → API Keys; VIEWER to look, a role with Docker/VM rights to start/stop), save it under Credentials as the password, and add "Unraid (API key)". The app first asks the server what its API offers and shows only that: server, array and disks (temperatures, status), containers and VMs; start/stop only if your Unraid version has those operations. Array, disk and share operations are never offered by this app. https is recommended; plain http is accepted only for an address on your own network (with a warning).
Every provider starts READ-ONLY. Tick "Allow changes" to offer changes.
**Proxmox (0.1.14):**
- **VMs and containers:** Start, Shut down, Reboot, Stop (hard); Pause / Resume (VMs); ◷ Snapshots (list), Take snapshot… (VMs can include RAM), Roll back to snapshot… and Delete snapshot… (type the guest's name); Clone… (empty VMID = the next free one; full copy, or linked from a template); Migrate… (another node; VMs live, containers restart); Back up now… (vzdump to a backup storage: snapshot, suspend or stop mode); Convert to template… (stopped guests, can't be undone); ⓘ Guest agent info (VMs with the QEMU agent).
- **Nodes:** ☰ Recent tasks. **Storage:** ☰ Contents. Templates offer only Clone and Back up.
- Each action names the privilege the token lacks when it is greyed out. Results are verified: the Proxmox task must end OK and the snapshot, the new guest, the node, the backup file or the template flag must be read back.
**Docker (0.1.14):** containers: Logs, ⓘ Inspect, ☰ Processes, ▤ Resource usage, Start, Stop, Restart, Pause, Unpause, ⚡ Kill, Remove (stopped only). Images, networks and volumes: Remove (Docker refuses anything still in use; the built-in bridge / host / none networks are never offered; removing a volume deletes its data, so you type its name).
**Unraid (0.1.14):** containers also Restart / Pause / Unpause, VMs also Pause / Resume / Reset — each only if your Unraid API version has it. Actions the target doesn't permit (token rights, a template, a running container you want to remove) are shown disabled with the reason.
Each change asks first with target, action, environment and consequence (type the name for PRODUCTION or anything that loses data). The result says "verified" only after the app re-read the container/guest and saw the expected state; otherwise "NOT verified". Everything is in the Audit log. ✦ Explain with AI sends the selected objects' summaries only; the AI never runs actions. Nothing polls unless you pick an auto-refresh, and then only while the screen is open.

## VMware vCenter
keywords: vmware, vcenter, vsphere, esxi, vm, power on, power off, guest shutdown, vmware tools, clone, datastore, cluster, host, certificate
Infrastructure → ＋ Add ▾ → VMware vCenter (user + password). It needs vCenter 7.0 or later (the vSphere Automation API at https://vcenter/api). A standalone ESXi host has no such API: add its vCenter instead.
**Account:** store a dedicated vCenter user under Credentials (user name such as svc-dcx@vsphere.local, and its password) and pick it. The password is sent once to sign in; after that only the session is used, and the session is ended when you close the screen.
**Least privilege:** the Read-only role to look. To make changes, add Virtual machine › Interaction › Power on / Power off, › Provisioning › Clone virtual machine, › Change configuration › Change CPU count / Change memory, › Inventory › Remove, and Host › Inventory › Modify (disconnect / reconnect), on the folders where you want them. vCenter can't tell the app in advance what an account may do, so a refused change is shown with vCenter's reason.
**Certificate:** vCenter's certificate is usually self-signed. The first connect shows its SHA-256. Compare it under vSphere Client → Administration → Certificates, then Trust certificate. A changed certificate is refused until you check it again.
**Offered:**
- VMs, hosts, clusters, datastores and networks;
- for VMs: Power on, Shut down guest OS and Restart guest OS (these need VMware Tools), Suspend, Power off (hard), Reset (hard), Clone (vCenter 7.0 U2 or later; the copy stays off), ⚙ vCPU count…, ⚙ Memory size… (while running only with hot-add; otherwise vCenter's reason is shown), ✕ Delete VM (powered off; deletes its files too; type the name), and ⓘ Guest & Tools info;
- for hosts: ⏏ Disconnect host and ⇌ Reconnect host.
Each change is verified by reading it back (power state, CPU / memory, the VM gone, the host's connection state).
**Not offered:** snapshots and maintenance mode (vCenter's REST API doesn't have them — only the older SOAP API does), disks / network adapters, templates, new VMs from scratch and vMotion. Use the vSphere Client for those.

## Hyper-V
keywords: hyper-v, hyperv, microsoft, vm, checkpoint, snapshot, virtual switch, export, import, new vm, winrm, powershell, cim, kerberos
Infrastructure → ＋ Add ▾ → Hyper-V host. Leave the host empty for this PC, or enter a Hyper-V host's computer name.
**Requirements:**
- The Hyper-V PowerShell module on this PC (Windows features → Hyper-V → Hyper-V Management Tools → Hyper-V Module for Windows PowerShell).
- For a remote host, WinRM enabled on it (`Enable-PSRemoting`).
- For changes, membership of the host's "Hyper-V Administrators" group.
**Sign-in:** by default your Windows sign-in (Kerberos in a domain), so no password is stored. Optionally choose a vault credential (DOMAIN\user); its password is handed to PowerShell on standard input, never on a command line.
**Insecure WinRM settings:** the app never enables Basic authentication, unencrypted WinRM or TrustedHosts. Outside a domain, the secure choices are HTTPS WinRM with a trusted certificate, or running the app on the Hyper-V host itself.
**Offered:**
- **Seen:** the host, its VM and disk folders, VMs, virtual switches and checkpoints.
- **VMs:** Start, Shut down, Save state, Pause, Resume, Turn off (hard), Reset (hard), Checkpoint…, Export… (to a folder on the host), ⓘ Details (adapters and their IPs, disks, integration services, automatic start / stop), ⚙ vCPU count… and ⚙ Startup memory… (VM off), ✎ Rename…, ⇄ Connect to switch… (first adapter), ✕ Delete VM (VM off; its disk files stay on the host; type the name).
- **Checkpoints:** Apply…, Rename…, Delete.
- **Host:** New VM… (with a new empty disk) and Import VM… (a .vmcx file on the host, imported as a copy).
Every value goes to PowerShell as data, never as code. Each change is verified by reading the inventory again. Export and import paths are on the Hyper-V host's disks.

## Synology DSM
keywords: synology, dsm, nas, diskstation, storage pool, volume, disk, smart, share, package, container manager, docker, virtual machine manager, hyper backup, two-factor, otp
Infrastructure → ＋ Add ▾ → Synology DSM. Enter the NAS's https address (e.g. https://nas.lan:5001) and a vault credential with a DSM user name and password. https only; the certificate is checked by Windows or pinned on first use.
**Security:** the password, session and SynoToken travel only in request bodies and headers, never in a URL; every session is signed out after use; read-only until "Allow changes". Use a dedicated DSM user rather than the built-in "admin" (you're warned if you use it). With two-factor sign-in, choose 🔑 Sign in with authenticator code once: DSM's device token is then stored sealed in the vault and later sign-ins use it.
**Seen:** the system (model, DSM version, CPU, memory, temperature), storage pools, volumes, disks (SMART), shares (encrypted or not), packages, containers, virtual machines (only if Virtual Machine Manager is installed), Hyper Backup tasks and users.
**Changes:** containers Start / Stop / Restart, packages Start / Stop, VMs Power on / Shut down / Power off (hard), Hyper Backup Back up now, and Reboot / Shut down the NAS. Each is verified by reading DSM again.

## Red Hat Satellite / Foreman + Katello
keywords: satellite, red hat, foreman, katello, errata, content view, lifecycle, promote, publish, repository, sync, subscription, remote execution, rex, job, rhel, patch
Infrastructure → ＋ Add ▾ → Red Hat Satellite / Foreman + Katello. Enter the https address and a vault credential: the Satellite user name, and a Personal Access Token (your user → Personal Access Tokens, with an expiry) as its password.
**Security:** https only; the certificate is checked by Windows (install the katello-default-ca or your CA) or pinned on first use; the token goes only in the Authorization header inside TLS. Read-only until "Allow changes", and Satellite's own roles decide the rest (a 403 is shown as such). Use a dedicated user: Viewer to look, plus Remote Execution User for commands and errata, and the content view / repository permissions for sync, publish and promote. An administrator account is warned about.
**Seen:** hosts (OS, IP, global status, security / total errata, upgradable packages, lifecycle environment and content view), organizations, lifecycle environments, content views (latest version, where it's promoted), repositories (last sync), subscriptions (usage; ending within 30 days shows "expiring") and running or failed tasks. Plain Foreman without Katello shows hosts and tasks.
**Changes:** ⟳ Sync repository, ⇪ Publish new version and ⇢ Promote latest version (to a lifecycle environment by name) are followed to the end of their Foreman task; ⛨ Install applicable security errata and ⌨ Run a command run through remote execution on that one host and show the output. PRODUCTION providers ask for the usual confirmation.
**Not covered:** provisioning (new hosts, compute resources), host groups, activation keys and Satellite's Ansible roles are left to the Satellite web UI or `hammer` — open the web UI in a Portal browser tab.

## UniFi Network and Site Manager
keywords: unifi, ubiquiti, udm, ucg, unifi os, network application, access point, switch, gateway, poe, power cycle, adopt, guest, voucher, hotspot, wifi, ssid, vlan, firewall, site manager, api.ui.com, cloud connector, isp metrics, api key
Infrastructure → ＋ Add ▾ → **UniFi Network (API key)**: the console's https address (e.g. https://192.168.1.1, or https://unifi.lan:8443 for a self-hosted Network server) or, from anywhere, **Ubiquiti Cloud Connector** with the console's id; and a vault credential whose password is the API key (UniFi Network → Settings → Control Plane → Integrations; Network 9.0 or later). **UniFi Site Manager** (api.ui.com) takes a key from unifi.ui.com → Settings → API Keys and shows every console, site and device your account has.
**Security:** https only; the key goes only in the X-API-KEY header; a console's self-signed certificate is pinned on first use and a changed one is refused. The key carries its creator's role: a read-only admin's key can look but not change. Changes need "Allow changes" on the provider.
**Seen:** sites, devices (state, firmware and updates, uptime, CPU / RAM, ports up, PoE ports, uplink), devices waiting for adoption, clients (wired / WiFi / guest), networks and VLANs, WiFi, WANs, VPN servers and site-to-site tunnels, firewall zones and policies, ACL rules, DNS policies, traffic lists, guest vouchers, LAGs and switch stacks. Site Manager adds site health counts, firmware status across consoles and ISP metrics (latency, loss, throughput).
**Changes** (each re-read until it is true): ↻ Restart device (verified by a new uptime), ⚡ PoE power-cycle a port, ＋ Adopt a waiting device, ✕ Remove (unadopt), ✓ Authorise / ⊘ Revoke guest access, 🎟 Generate vouchers (the codes are shown), ✕ Delete voucher, ▶ / ■ WiFi broadcasts, networks and your own firewall policies on or off. System-defined items and the default network are never switched.
**Tip:** in a Site Manager provider, right-click a console → "Add as a UniFi Network provider" to manage it through the Cloud Connector with the same key.

## DCX-CTL portal (dcx-ctl)
keywords: darkcipher portal, dcx-ctl, control api, enrolment, julia anne taylor, portal users, invites, website control
Infrastructure → ＋ Add ▾ → DCX-CTL (a DarkCipher Systems portal). Paste the enrolment string the portal's owner created (dcxctl1.…) and the server's SSH host key fingerprint (SHA256:…). The enrolment key is sealed in the vault; the host key is pinned in Known hosts.
**Security:** dcx-ctl/1 runs over SSH; every request and response is authenticated (X25519 key agreement, ChaCha20-Poly1305, per-message MACs), so a changed host key or a tampered reply is refused. Providers are PRODUCTION by default.

## Hetzner Cloud
keywords: hetzner, hcloud, cloud, project, api token, server, volume, floating ip, primary ip, load balancer, firewall, network, placement group, certificate, snapshot, backup, rescue, rebuild, resize, dns, zone, rate limit
**Connect a project:** in the Hetzner Console, open your project → Security → API tokens → Generate API token. Choose "Read" to only look, or "Read & Write" to make changes. Then Settings → Cloud providers → Hetzner Cloud projects → ＋ Add project, and paste the token. It is tested before it is saved, stored only in the encrypted settings, and never shown again. Add one entry per project. If you mark a token read-only, the app refuses every change itself, before sending anything.
**Screens** (Cloud providers → Hetzner Cloud):
- **Compute:** Servers (create, power, rescue, rebuild, change type, images, backups, ISO, networks, placement group, reverse DNS, protection, root password reset; Actions history per server), Images, Placement groups.
- **Storage:** Volumes.
- **Network:** Networks, Firewalls, Floating IPs, Primary IPs, Load Balancers.
- **Access:** SSH keys ("Upload a vault key" sends only the public half) and Certificates.
- **DNS:** zones and records.
- **Reference:** locations, server types, load balancer types, ISOs and pricing.
**How changes work:** every change in Hetzner is an action. The screen shows its progress and reports done only when Hetzner says it finished; a failed action shows Hetzner's reason. A new server without an SSH key gets a root password, which the app shows once and never stores. Deleting servers, volumes, networks, load balancers and DNS zones asks you to type the name. Protection (Protection…) stops deletion at Hetzner itself.
**Also (0.1.14):** 📈 Metrics (last hour) on servers (CPU, disk, network) and load balancers (connections, requests, bandwidth) — average, peak and latest; Alias IPs… on servers; Members on networks; Update service… and Reverse DNS… on load balancers; Add records…, Remove records… and Protection… on DNS records; Primary nameservers… on secondary zones.
**Limits:** 3600 API requests per hour per project; when Hetzner says to slow down, the app waits for its reset time (reads only — orders are never repeated). Not available: the web console (it needs a VNC client), and Hetzner's separate Robot (dedicated servers) and Storage Box APIs.

## Microsoft Azure
keywords: azure, microsoft azure, subscription, entra, app registration, certificate, pfx, resource manager, arm, aks, app service, azure sql, vm, deallocate, nsg, key vault, read-only
Settings → Cloud providers → Azure subscriptions → ＋ Add subscription.
**What you need:** an Entra app registration (single tenant) with a certificate — a .pfx with its private key; upload the public half (.cer) to the registration under Certificates & secrets. Give the registration a role on the subscription (Access control (IAM)): Reader to only look, Contributor (or narrower roles) to make changes. Client secrets aren't accepted.
**Adding it:** a name, the tenant id, the application (client) id, the subscription id, the .pfx and its password. The app signs in once to check before anything is saved. The certificate is stored only in the encrypted settings and never shown again; right-click a subscription to copy its id or the certificate's SHA-256 thumbprint (to compare with the portal).
**Read-only until you allow it:** a new subscription only reads. "Allow changes / read-only" switches it; even then every change asks first, and Azure checks the registration's role.
**Screens:** virtual machines (start, stop = deallocate so billing stops, power off — still billed, restart, redeploy, resize, instance view, extensions), VM scale sets (start / deallocate / restart all, scale), availability sets, AKS clusters (start / stop) and node pools, App Service (start / stop / restart), container instances (start / stop / restart), Azure SQL servers and databases, PostgreSQL and MySQL flexible servers (start / stop / restart), disks (snapshot) and snapshots, storage accounts (settings), virtual networks, network security groups and their rules, network interfaces, public IPs, load balancers, DNS zones and records, private DNS zones with records and virtual network links, key vaults (settings only), resource groups, all resources, Resource Health, Advisor recommendations, locations, and a read-only API explorer.
**Changes take a while in Azure:** the app follows each one until Azure reports it finished (or failed, with Azure's reason).
**Never read:** keys, secrets, connection strings, publishing credentials, boot-diagnostics links. Not available: the serial console, identity and role assignments.

## Google Cloud
keywords: google cloud, gcp, gce, compute engine, service account, key file, json key, gke, kubernetes, cloud run, cloud sql, bucket, cloud storage, vpc, firewall rules, cloud dns, project, read-only
Settings → Cloud providers → Google Cloud projects → ＋ Add project.
**What you need:** a service account in the project with roles for what you want (Viewer to only look) and a JSON key for it (IAM & Admin → Service accounts → Keys → Add key → JSON). The APIs you use must be enabled in the project.
**Adding it:** choose the key file, confirm the project id (the key's own project is suggested) and a name. The app signs in once to check before anything is saved. The key is stored only in the encrypted settings — delete the downloaded file afterwards. Key files that point anywhere but Google's own sign-in are refused.
**Read-only until you allow it:** a new project only reads, and while it does the app asks Google for read-only access too. "Allow changes / read-only" switches it; even then every change asks first, and Google checks the service account's roles.
**Screens:** VM instances in every zone (start, stop, hard reset, suspend / resume, machine type, serial output), managed instance groups (resize), instance templates, custom images and machine images, disks (snapshot) and snapshots, GKE clusters and node pools (set size), Cloud Run services and revisions, Cloud Functions, Artifact Registry repositories, Cloud SQL instances (start / stop / restart, back up now), databases and backups, Cloud Storage buckets (settings), VPC networks, subnets, firewall rules, routes, Cloud Routers and NAT, IP addresses, load balancer front ends, backend services, health checks, SSL certificates, VPN tunnels, Cloud DNS zones and records, Pub/Sub topics (create) and subscriptions, regions, and a read-only API explorer.
**Never read:** service-account keys, Secret Manager, Cloud SQL users and passwords, object contents. Not available: the serial console and IAM.

## Cloud providers workspace
keywords: cloud providers, remote api, vultr, contabo, hetzner, azure, google cloud, gcp, workspace, tree, navigation, sidebar, pin, pinned, favourite, switch account, search providers
Sidebar → SERVERS → ☁ Cloud providers opens one workspace for every cloud provider.
- **The tree on the left:** each provider (with its active account when that adds something, and how many accounts it has), and its screens grouped (Compute, Storage, Network, Access, DNS, Reference, Tools). Only what that provider's API offers is listed. The tree starts collapsed; what you expand stays expanded while the app runs.
- **Search** (Ctrl+F) filters providers and screens; Esc clears it, ↓ moves into the tree.
- **Opening a screen:** click it, or select it with the arrow keys and press Enter (moving with the arrow keys doesn't load each screen). Ctrl+Enter or a middle click opens it in its own tab.
- **★ Pinned:** right-click any screen → "Pin to top" to keep it in a Pinned branch at the top of the tree (up to 20, kept in the encrypted settings); right-click → "Unpin" removes it. Settings → Navigation & explorers can unpin them all.
- **Right-click a provider** to open its main screen, expand or collapse all of it, switch its account, or open its account settings.
- **Accounts:** right-click a provider in the tree → Switch account (or Settings → Cloud providers). The header no longer has an account picker (0.3.0).
- Before 0.1.11 every provider had its own sidebar section; links to those screens (from the dashboard, for example) still work and open here.

## AI Engineer: running commands in a terminal
keywords: ai engineer, terminal target, run command, review, risk, sudo, destructive, stop, ctrl+c, exit code, bridge, target terminal
module: ai
In the AI Engineer sidebar, TARGET TERMINAL is the one session that can receive commands. By default it follows the terminal you're working in (the one in front) as you switch tabs; pick a terminal yourself (or "none") to keep it, and "↻ Follow the terminal in front" goes back to the default. While the AI is working on a task the target stays put, and the AI can't switch it without asking you.
**When the AI suggests commands:** each code block in an answer gets "▶ Review & run in <session>". Nothing runs until you click it.
**The review** shows:
- the exact text;
- the target (session, server, user, directory, environment);
- a risk rating with its reasons: read-only, changes the system, sensitive (passwords, keys, sudo shells, piping downloads into a shell), or destructive (recursive deletes, disk writes, reboots, firewall flushes).
Sensitive commands ask you to type RUN. Destructive commands, and changes in a PRODUCTION terminal, ask you to type the session's name. Unknown programs count as changes.
**While it runs:** the command is typed into that terminal, so you see it happen. ■ Stop sends Ctrl+C. The exit status, working directory and output come back into the conversation.
**What the AI gets back:** the output, masked and shortened to the last 8,000 characters (Settings → AI permissions & safety can turn that off). There is a time limit, 5 minutes by default.
**Stale targets:** if the terminal disconnects, reconnects or closes, you must choose it again before anything else runs there.
**Which sessions work:** SSH to Linux/Unix, WSL and local PowerShell. cmd, Windows or network-device SSH, serial and Telnet sessions are listed as not usable, because their exit status can't be read reliably.
**The AI operator:** it has the same ability as the tool terminal_run. It only ever targets the session you chose, and goes through the same review.

## AI agents: steps, Stop, model per chat and speed
keywords: agent, agentic, steps, step limit, tools, stop, cancel, model per chat, streaming, slow ai, speed, warm, keep warm, tool calling, function calling
module: ai
**Agents:** the AI operator and the AI Engineer plan, call tools, look at the results and continue until they can answer. The step limit is 12 by default (Settings → AI speed, agents & memory). Independent read-only lookups run at the same time.
**STEPS** (AI Engineer, under the chat) lists every plan, tool call and result with its time; the operator shows them in the chat.
**■ Stop** cancels at any point, and sends Ctrl+C to a command the AI Engineer is running in your terminal.
**Agent** (next to the model box) on = tools; off = plain answers.
**Model per chat:** the model box picks another model of the same engine for this conversation only.
**Faster answers:** answers stream as they are written; the model loads while you type your first character and stays loaded for the keep-warm minutes (10 by default).
**Models without tool calling** get the tools described in the prompt instead; STEPS notes it. Small models (phi-4-mini, for one) sometimes write a tool call out as text instead of making it: when the reply is nothing but such calls, they run through the same approvals, and STEPS says so. When an answer writes a call into a sentence instead of making it, the AI Engineer tells the model nothing ran and asks for the call in a strict format (twice at most); if it still makes none, STEPS says "nothing ran", so a made-up result can't pass as real. A model that answers "As an AI, I can't run commands" before trying is told it can, and asked for the call the same way. For agent work on a target terminal, Qwen2.5 7B or 14B (Foundry Local's default on a GPU PC since 0.6.17) or a 7B–8B model in Ollama (qwen2.5, llama3.1) calls tools most reliably.

## The default AI model for your PC
keywords: default model, foundry local model, qwen2.5, qwen2.5-7b, qwen2.5-14b, phi-4-mini, npu, gpu, cpu, which model, best model
Foundry Local is the default engine, with the strongest tool-calling model your PC runs well (Foundry Local itself loads the GPU, NPU or CPU build your PC has):
- a GPU with 12 GB or more of video memory: **Qwen2.5 14B** (when your PC's catalogue offers it; otherwise 7B);
- a GPU with 6 GB or more: **Qwen2.5 7B**;
- an NPU (Copilot+ PCs): **phi-4-mini** on the NPU, at low power;
- a smaller GPU or most CPUs: **phi-4-mini**; a CPU with 32 GB of RAM and 12+ threads: Qwen2.5 7B (slower answers, reliable tool calls).
It is chosen once; a model or engine you picked yourself is never replaced. Settings → AI → Foundry Local shows the guide for every model your PC offers. The first download is about 2–9 GB depending on the model.

## Deploy a server with the AI (Vultr)
keywords: deploy server, create server, new vps, order server, vultr deploy, spin up, provision, cheapest plan, ai deploy
Ask the AI Engineer or the operator, e.g. "deploy a Vultr server called web-03 with Ubuntu 24.04, 2 vCPU and 4 GB RAM in London". It reads Vultr's own lists:
- **OS:** the image whose name has every word you gave (an LTS release first, then the newest; ARM only if you say so).
- **Region:** the id (lhr), city or country you gave; none given → the region most of your Vultr servers are in.
- **Plan:** an exact plan id if you name one; else the cheapest plan with exactly your vCPU and RAM (and at least your disk); if none matches, **the cheapest plan that runs that OS** (IPv6-only and GPU plans aren't used; Windows needs 2 GB). The approval says when your spec didn't match.
- **Approval:** you see the name, OS, region, plan, size and monthly price before anything is ordered. Decline and nothing happens.
- **After you approve:** it is ordered once (never retried), the AI waits until the server is running, saves it as a host (Remote Desktop for Windows), stores Vultr's root password in the vault (the AI never sees it) and checks that SSH (or Remote Desktop) answers.
- Needs a Vultr account in Settings → Vultr accounts. Settings → AI → "Let the AI deploy new cloud servers" (Group Policy `AiAllowServerDeploy`) turns it off.

## AI Engineer: working in your terminal session
keywords: ai engineer, auto-run, read-only, target session, read screen, send keys, ctrl+c, switch target, open session, command session
module: ai
With a TARGET TERMINAL chosen and Agent on, the AI Engineer runs commands in that session itself:
- **Review:** each command is reviewed by you, as in "AI Engineer: running commands in a terminal".
- **Auto-run read-only commands in the target session** (tick box under TARGET TERMINAL, off by default): commands that only read run without a review. Never auto-run:
  - changes, sensitive or destructive commands;
  - sudo;
  - interactive programs;
  - commands that don't end on their own (tail -f, ping without -c, watch);
  - anything in a PRODUCTION session.
- **It can also:**
  - read the screen (masked; asks unless auto-run is on);
  - send keys to prompts and pagers (always reviewed);
  - press Ctrl+C (its own command at once, anything else asks);
  - ask you to switch the target or open a new SSH session to a saved host. Only your yes changes the target.
- **Audit:** everything is in the audit log.

## AI memory guard
keywords: memory, ram, vram, gpu memory, freeze, slow pc, commit charge, foundry unload, ollama keep_alive, context window, out of memory
module: ai
Local models can fill GPU memory and spill into system RAM, which slows or freezes the whole PC. The guard (Settings → AI speed, agents & memory):
- **When memory is tight** (commit charge 85 % or more, or the Ollama model partly outside GPU memory): asks for a smaller context (8K; 4K at 93 %) and unloads the model right after the answer.
- **Foundry Local:** unloads its models when memory is tight and you use another engine, or after you stop using the AI.
- **Never** stops Ollama or Foundry Local.
- **Settings** shows the current reading and the guard's last action.

## Settings: tabs, search and reset
keywords: settings, tabs, categories, search settings, reset to defaults, reset section, scope, applies to, switches, captions, navigation, explorers, collapsed, remember expanded
Settings has tabs across the top: Security & privacy, Cloud providers, AI & models (while AI features are on), Terminals & connections, Appearance, Navigation, Data & backups, Integrations, Import & export, About.
- **Search** (Ctrl+F) above the tabs finds a setting by its title, label or caption in any tab and opens the tab of the first match; Esc clears it.
- **Cards:** each tab is one column of cards. A card's title says what it is and its caption what it applies to (the whole app, new terminal tabs, per account, per profile…). On/off settings are switches, and each setting has a short caption under it.
- **Reset:** cards that can go back to their defaults have a **Reset** button (Theme, Layout and text, Typography, Sidebar, Navigation & explorers, Terminal & connections, Terminal profiles). It asks first and changes nothing outside that card. Accounts, keys, the store and security settings are never reset this way.
- **Explain with AI:** right-click any option (while AI features are on).
**Moved in 0.2.1:** Appearance & navigation became two tabs, Appearance and Navigation; Updates & about became About; appearance import and export have their own tab.
**Moved in 0.1.11:**
- screenshots folder and crash recovery: Data & backups → Screenshots & recovery;
- "let the AI read without asking": AI & models → AI permissions & safety.

## Appearance: presets, accent, corners and animation
keywords: appearance, theme, theme pack, preset, darkcipher, graphite, teal, high contrast, neutral, slate, arctic, nord, windows 11, light, dark, accent, colour, color, swatch, contrast, status colours, density, zoom, text size, corners, rounded, sharp, animation, motion, reduced motion, house theme
Settings → Appearance. Everything applies immediately and is kept in the encrypted settings.
- **Theme:** Dark, Light or follow Windows, and a theme pack, picked from the preview tiles (each shows its dark and light look) or the dropdown. DarkCipher is the default; Graphite and Teal are dark DarkCipher variants (light mode uses DarkCipher Light); High Contrast is always dark. The everyday packs have their own dark and light palettes: **Neutral** (plain greys and blue, like Windows 11), **Slate** (blue-grey and indigo) and **Arctic** (polar greys and frost blue). Choosing a pack brings its own accent unless you picked a different one yourself. Environment labels look the same under every pack. With "Follow Windows high contrast" on, turning on high contrast in Windows switches the app to High Contrast until you turn it off.
- **Accent:** pick a swatch or type a colour (#RRGGBB). The contrast check shows how well it stands out against the background; a colour below 3:1 is refused.
- **Status colours:** subtle, standard or strong — how bright success, warning, error and the other states are.
- **Layout and text:** density (comfortable or compact), text size, zoom, corners (sharp, 2 px or rounded) and animation (follow Windows, standard or reduced; reduced also stops the header cursor blinking).
- **Typography:** fonts, see "Fonts, text size and typography".
Theme add-ons replace the colours they name and keep working. **Branding & colours** (0.4.0) lets you make the app your organisation's without an add-on: product name, tagline, a PNG logo (with an optional plate behind it), a UI font and every palette colour for dark and light mode — empty boxes keep the app's own values. "Fill with the colours in use" starts a palette from the current look; Import / Export theme.json exchange the same file a theme add-on carries; "Export as theme add-on" writes a .dcxaddon colleagues install from Add-ons (nothing in it runs). An organisation can push it by Group Policy: "Branding theme" (the theme.json text) and "Branding logo path" (a PNG on a share). A chosen theme add-on wins over the card.

## Import & export appearance
keywords: import, export, appearance, navigation, settings file, json, copy settings, another pc, share look, reset appearance
Settings → Import & export → **Export…** saves the appearance and navigation settings (theme, accent, status colours, density, text size, zoom, corners, animation, fonts, the sidebar, pinned pages and sections) as a small JSON file. It holds no accounts, keys, tokens, passwords, hosts, paths or notes, so it isn't encrypted.
**Import…** reads such a file (64 KB at most), checks every value, lists what would change and what it skipped, and changes nothing until you press Apply. Both are written to the audit log.
**Reset all appearance and navigation…** puts those settings back to the defaults; everything else stays as it is.
For everything else (hosts, credentials, settings) use an encrypted backup (Settings → Security & privacy → "Encrypted backup / export…", see "Backup, export and import").

## Updates
keywords: update, upgrade, new version, check for updates, download, install, signature, release key, github, automatic, daily, weekly, downgrade, backup before upgrade
Settings → Updates → Check for updates. By default the app looks at the latest release on GitHub (github.com/DarkCipher0x/ServerManagement; ⚠ internet) and sends nothing about you — a plain download of the update information and its signature. "Updates from" can point at an https address of your own (or the DarkCipher Systems website) instead. It shows the installed version, the latest version, release notes and the package (size, SHA-256). "You are running the latest available version" when there is nothing newer; older versions are never offered. Until a release is published on GitHub (the repository is public and publishing is on), a check says no release is published yet.
Trust: an update is offered only when its update information is signed by the DarkCipher release key built into the app. GitHub (or any server) only carries the files: a changed file, a different key or a redirect off GitHub is refused.
Download and verify: the installer goes to the app's private temp folder and must match the signed size and SHA-256; an MSI must also be this product at the advertised version. Anything else is refused and deleted — nothing is run. If you installed with the setup program, the update uses the new setup program; otherwise the MSI.
Install asks "Would you like to back up your configuration before upgrading?" (Back up and continue / Continue without backup / Cancel), then closes the app and starts the installer. At the next start the app checks that the new version is really running and tells you either way.
Automatic checks are off by default. Turn on "Check automatically" (an external connection) and choose once a day or once a week; it shows a status-bar message. "Also download and verify a new version in the background" fetches and checks it too, ready in Settings → Updates. Installing always asks you. Each of these is also a Group Policy.

## Auto-reconnect and long-running sessions
keywords: reconnect, auto reconnect, dropped, disconnect, network loss, backoff, hours, stable, keepalive
When a session drops unexpectedly (network loss, server reboot) the terminal reconnects automatically with exponential back-off (1, 2, 4, 8 … seconds, capped by Settings → Terminal & connections → Reconnect back-off cap, default 30).
Press Enter or ↻ Reconnect to retry immediately; ✕ Disconnect stops auto-reconnect.
Huge output (e.g. cat of a multi-GB log) is throttled: the oldest unrendered backlog is dropped with a notice instead of exhausting memory, so a session can't freeze the app.

## Terminal display recovery
keywords: renderer, crashed, display, blank terminal, webview2, restarted, recovery, msedgewebview2, frozen
A terminal tab draws its screen in a separate browser process (WebView2). If that process crashes or stops responding, the app builds a new display for the tab on its own. The connection itself (SSH, local shell, serial, Telnet) is never touched, so nothing is disconnected and nothing running on the server is interrupted.
The new display replays the most recent output (about the last 500,000 characters), and the status line says "display restarted after: …". SSH sessions also show a note in the terminal. A local console (PowerShell, cmd, WSL) redraws its own screen when the new display reports its size, so for those the status line is where you see it.
To avoid a crash loop it restarts at most 3 times in 10 minutes per tab. After that the status line says the display keeps failing and that the session is still connected: close and reopen the tab (or restart the app) when convenient.

## Running the app twice
keywords: second instance, already running, single instance, two windows, launch again, mutex
Only one copy of the app runs per data store, so two processes can never write the same encrypted files or share one browser profile. Starting it again (Start menu, shortcut, taskbar) simply brings the running window — or its unlock screen — to the front and exits.
Want more windows? Pop tabs out (right-click a tab → Pop out) instead of starting a second copy. A copy using a different data store (DCX_DATA_DIR) is a separate instance with its own browser profile.

## Crash recovery: recover previous work
keywords: crash, restore, workspace, tabs, reopen, unclean shutdown, recovery, unsaved, draft, lost edits, power cut, transfer resume, recover previous work
If the app didn't close cleanly (a crash, a power cut, Windows killed it), the next start shows **Recover previous work** with a box for everything it can bring back:
- **Open tabs:** terminals, files, Manage and Remote Desktop tabs reopen in their panes, pop-out windows and tab groups, with the same tab active. They reopen **disconnected**: press Enter in a terminal (or ↻) to connect. Turn this part off with Settings → Data & backups → Screenshots & recovery → "After a crash, offer to reopen the tabs that were open".
- **Unsaved edits:** text you were editing in a remote file (Files → Edit, or the file commander) reopens in the editor marked "recovered — not saved yet". Nothing is written until you press Save; if the file changed since you started, you're asked before it's overwritten.
- **Unfinished transfers:** copies and moves are queued again, and unfinished files resume from their ".dcxpart" part. A folder sync's deletions are never repeated after a crash — run the sync again to mirror.
**Recover selected** brings back what's ticked and discards the rest; **Later** keeps everything and asks at the next start; **Discard all** asks first when edits would be lost.
Unsaved edits and unfinished transfers are also kept when you simply exit, or Windows logs off while an editor has unsaved text.
How it works: the unsaved text and the transfer list are kept in an encrypted recovery file, written about 2 seconds after a change (nothing is written while nothing changes) and at once on a fatal error or when Windows shuts down. Notes and incidents save themselves about a second after you stop typing, and are saved at once on a fatal error too. A sudden power cut can lose only the last couple of seconds of typing.
Not recovered: AI conversations (kept in memory only, by design) and half-filled settings or host dialogs.

## Saved sessions and workspaces
keywords: save sessions, exit, reopen, restore tabs, workspace, layout, homelab, reconnect on start, remember tabs, session restore, tmux reattach, working folder
When you exit with tabs open, the app asks: **Save and exit** (they come back at the next start), **Exit without saving** or **Cancel**. Tick **Don't ask again** to make your choice the default; Settings → Terminals & connections → **Saved sessions** changes it (Ask, Always save, Never save) and has the switch for reconnecting.
What comes back, in the same panes, extra windows and tab groups, with the same tab in front:
- **Terminals** reconnect and go back to their working folder (typed for you as ` cd -- '/the/folder'`; the leading space keeps it out of your shell history). The folder is read from what the shell printed: OSC 7, a Debian / Ubuntu prompt (`user@host:/path$`) or a PowerShell prompt. A prompt that shows only the folder's name (Red Hat's `[user@host dir]$`) can't tell where it is, so that terminal reconnects in your home folder.
- **tmux:** a terminal you took into tmux with ⧉ tmux is re-attached to its session after every reconnect, so long-running work is still there. It stops once you detach or exit tmux. tmux is never installed during a reconnect.
- **Files** at their folder, **File commander** with both panes, **Remote Desktop**, **VNC**, **Manage** and saved **portal** tabs.
- **Quick-connect sessions** are kept with their settings (address, user, port, key or credential reference), never a password: one you typed is asked for again.
- **PRODUCTION** hosts reopen disconnected: press Enter or ↻. Locked hosts (and hosts whose credential or jump host is locked) never reopen by themselves.
**Named workspaces:** ▦ Workspaces in the title bar → **Save the open tabs as a workspace…** (Homelab, VPS…). Each one can be opened (reconnected, or disconnected), replaced with the open tabs, renamed or removed. Opening a workspace adds its tabs beside the ones already open; a tab of the same kind and host that's already open isn't opened twice. A removed workspace is archived in the encrypted store, not deleted.
Everything is stored encrypted. Group Policy can set both settings ("Terminals and connections").

## Tabs, split panes, tiling and pop-out windows
keywords: tabs, split, tile, tiling, pane, layout, resize, side by side, grid, pop out, window, new window, monitor, multi-monitor, second monitor, host pane, detach, sidebar, customise, customize
Every view opens as a tab. Right-click a tab (or use the shortcuts) to arrange your workspace:
- Split right (Ctrl+Alt+→) / Split down (Ctrl+Alt+↓): moves the tab into a new pane beside or below.
- Move to next pane, Tile all tabs (Ctrl+Alt+T) into a grid, Merge all panes (Ctrl+Alt+U).
- Pop out to a window (Ctrl+Alt+O): the tab becomes its own resizable window; when its last tab goes back, the window closes.
- **Extra windows for other monitors:** New window (Ctrl+Shift+N, a tab's menu, or the pane's ⊞ menu) opens a window with its own tabs, split panes and a **host pane** on the left (filter, Connect, ＋ Host, Properties, folders and hosts, Favourites). Connect from that pane, or open anything while the window is active, and it opens in that window. Move tabs with a tab's menu → Move to window ▸ (Main window, Window 2…, New window), drag them between windows, or drag a tab onto the desktop for a window of its own. The host pane is pinned, or 📍 auto-collapses to a slim "Hosts" tab at the window's edge that slides out when you point at it (Settings → Navigation → Host pane in extra windows). The windows come back on their monitors at the next start (Settings → Navigation → Reopen extra windows). Closing a window that still has tabs asks whether to move them to the main window or close them.
- Close, close others, close to the right, rename, duplicate a terminal.
Drag the splitters between panes to resize them. Ctrl+Tab / Ctrl+Shift+Tab switch tabs in the active pane; F6 moves to the next pane.
The sidebar can be resized by dragging its edge and hidden with Ctrl+B; its sections can be reordered and hidden (see "Customise the sidebar"). Sidebar items can be right-clicked to open in a new pane or a window.

## Right-click menus
keywords: right click, context menu, menu, actions, mouse, groups, submenu, organised, organized
Right-click works throughout: hosts, terminal, tabs, sidebar items, every table (services, processes, packages, containers, ports, users, credentials, tunnels, audit, cloud resources, files), command output text, AI chat messages, report findings and help results.
**Groups:** the everyday actions for what you clicked come first (Copy / Paste / Find in a terminal; Connect / Files / Manage on a host; Close / Rename on a tab). Everything else sits under a named group: in the terminal Selection & notes, Output & input, Capture & share, Session, Host and View; on a host Notes & incidents, Organise, Copy & export and Remote Desktop; on a tab Close tabs, Tab group, Layout, Session and Screenshot & help. A short menu shows its groups between separators; a menu that would be longer than 12 rows folds each group into a submenu (hover it or press →).
Table menus put the row's own actions first, then the view's toolbar actions under Screen actions, and Copy row, Copy cell, Copy all rows (TSV) and Export as CSV under Copy & export. Text menus offer copy, find, save and (while AI features are on) "Explain selection with AI".

## File transfer (SFTP)
keywords: files, sftp, upload, download, edit, chmod, rename, folder, drag and drop
⇅ Files opens an SFTP browser. Upload (or drag files from Explorer), Download, Edit text files in the built-in editor (Ctrl+S saves), new folder/file, Rename, chmod, copy path, open a terminal in the current folder, Delete. Show hidden files with the "hidden" checkbox.

## Manage server
keywords: manage, server view, overview, services, processes, packages, docker, network, firewall, users, cron, shell, systemd
▣ Manage opens the full server view: Overview (CPU, memory, swap, disk, load, uptime, failed units, reboot-required, auto refresh), Services (systemctl status/logs/start/restart/reload/enable/disable/stop), Processes (details, open files, kill), Logs, Packages (apt/dnf/yum/zypper/pacman/apk), Docker (containers, images, compose), Network (listening ports, connections, interfaces & routes, ping/trace/DNS from the server), Firewall (ufw/firewalld/nftables + fail2ban), Users (accounts, sudo, keys, sessions, sudoers), Cron (crontabs + systemd timers), Shell (one-off commands with sudo and snippets) and AI insights (reports).
Changes always ask for confirmation and are written to the audit log.

## Logs and log analysis
keywords: logs, journal, journalctl, auth.log, errors, digest, patterns, explain, analyse, analyze, grep, follow
Manage → Logs reads the systemd journal with filters (unit, priority, lines, since, grep). Follow (terminal) tails it live; auth.log opens the auth log in less.
"⌗ Digest" groups thousands of lines into ranked patterns (variable parts such as numbers, IPs and ids are folded) with counts, severity and first/last time — this runs on your PC with no AI at all.
"✦ Analyse with AI" sends the digest (not the raw log) to the AI engine for a structured explanation; "✦ Explain" sends the visible lines. Right-click any output text to explain just the selection.

## Reports and AI-generated reports
keywords: report, reports, health report, fleet, export, html, markdown, pdf, findings, insights, ai report, structured
Manage → AI insights (one server) or sidebar → Reports (many servers) builds a report:
1. Collects read-only evidence over SSH (overview, journal errors/warnings, failed units, SSH/sudo auth, kernel messages, disk & inodes, ports, pending updates, Docker, top processes, timers, extra log files you list).
2. Digests logs on your PC and runs built-in checks (full disks, failed units, OOM kills, I/O errors, brute-force SSH, pending security updates, reboot required…). This part needs no AI.
3. Optionally asks the AI engine for a structured analysis (health, summary, ranked findings with recommendations and commands, next steps). If the AI fails or is off, you still get the offline report.
Reports are kept in the encrypted store (history drop-down). Export to HTML (self-contained, light/dark) or Markdown, copy as Markdown, discuss with the AI operator, or open in Claude Code. Suggested commands only run when you press Run… and approve the exact command.
Print an HTML export from a browser to get a PDF.
**This PC (0.4.0):** tick **💻 This PC** in the fleet report to examine this Windows computer too — system, uptime and pending updates, disks, services (stopped automatic ones), Defender and the firewall, BitLocker, listening ports, top processes and scheduled tasks (WMI and the registry, read-only) — and the **Event Viewer**: the logs you name (System and Application by default; add e.g. `Microsoft-Windows-TaskScheduler/Operational`) digested like Linux logs (critical / error / warning, grouped by provider and id with counts and an example). Built-in rules add findings with **PowerShell** suggestions (Copy only — the app never runs them on this PC), and the AI analysis reads the Windows evidence as well. The Security log and BitLocker need the app to run as administrator.
**Event Viewer exports (0.4.0):** **⤓ Export events (.evtx)** writes one `.evtx` per log for the chosen window and levels (opens in Event Viewer; a plain file outside the store), **⤓ Events as CSV** the same events as one formula-safe CSV.

## AI operator
keywords: ai, assistant, operator, chat, ollama, tools, ask, diagnose, health summary
module: ai
Sidebar → AI operator is a chat with a model that can use tools: list hosts, host overview, read logs, list services, service actions, run commands, Vultr instances and any Vultr GET endpoint.
Read-only lookups run automatically (configurable); anything that changes state — running a command, restarting a service, powering a server — shows you the exact action first and only runs if you approve. Everything is in the audit log as actor "ai".
"✦ Diagnose with AI" on a server's overview and "✦ Ask AI" in a terminal start a conversation with context.

## AI engines: Foundry Local (default), Ollama, OpenAI-compatible, Anthropic
keywords: ai engine, provider, default, foundry local, npu, cpu, copilot+, ollama, gpu, lm studio, llama.cpp, vllm, openai, azure, anthropic, claude api, model, laptop, battery, off
module: ai
Settings → AI engine picks where the model runs. Every engine runs the same features (operator, log analysis, reports, help answers, "✦ Explain with AI").
- **Foundry Local · NPU or CPU (the default).** Microsoft Foundry Local runs the model on this PC — on the NPU of a Copilot+ PC (Intel AI Boost, AMD Ryzen AI / XDNA, Snapdragon X Hexagon), on a GPU, or on the CPU. Nothing leaves the PC. The app sets it up for you and keeps it working (see "Foundry Local: automatic set-up and repair").
- **Ollama · GPU** — Ollama on this PC, a LAN box, or a GPU server through an SSH tunnel to a saved host. The best choice for a strong graphics card (8 GB VRAM or more); never the default.
- **Ollama · CPU only** — forces CPU inference and a smaller context. Foundry Local's CPU models are usually the simpler choice.
- **OpenAI-compatible server** — LM Studio, llama.cpp server, vLLM, LocalAI, Jan, or OpenAI / Azure OpenAI / a company gateway with a key.
- **Anthropic Messages API** — Claude with your API key, or Ollama's Anthropic-compatible endpoint.
- **Off** — no model is ever called; digests, rule-based reports and help search still work.
**The default is chosen for your hardware** the first time the app starts (and once after an update that changes the rule), only when you haven't picked an engine or model yourself:
- PC with an NPU → phi-4-mini (its NPU build; light on RAM, the NPU does the work).
- No NPU, 16 GB RAM or more → phi-4-mini (3.8B; its GPU build when the PC has a capable graphics card, otherwise the CPU build, about 5 GB of RAM while answering).
- No NPU, 8–15 GB RAM → qwen2.5-1.5b (about 2 GB).
- No NPU, less than 8 GB → qwen2.5-0.5b, the lightweight model (about 1 GB).
These are model names (aliases), not builds: Foundry Local's own catalogue on each PC decides which build runs (NPU / GPU / CPU, x64 or ARM). So settings copied to another PC still work there.
Ollama is never chosen for you. "Detect hardware" shows RAM, GPU VRAM and NPU and recommends the same Foundry model; with a strong GPU it also mentions Ollama as an option. Each AI feature can be switched off separately, and "Pause on battery" stops automatic AI work when unplugged.

## Foundry Local: automatic set-up and repair
keywords: foundry local, setup, install, start, load model, repair, restart, stuck, exit 4, not answering, automatic, hands-off, npu, intel npu driver, winget, foundry server, not installed, not running, set up now
module: ai
With Foundry Local as the engine and **"Set up and repair automatically"** ticked (the default, Settings → AI engine), you don't need to do anything. A few seconds after the app starts, and whenever an AI request finds Foundry Local not answering, the app:
1. **Installs Foundry Local** if it's missing (`winget install Microsoft.FoundryLocal`, Microsoft's package; a few minutes).
2. **Starts the service** (`foundry server start --idle-timeout 0`) if it isn't running.
3. **Checks the service really answers.** A service that says "running" but doesn't respond — the "exit 4" problem — is restarted. If that isn't enough, it's stopped completely, the stuck Foundry process is ended, and it's started fresh.
4. **Picks the model build this PC can run.** If your model isn't offered here (for example, a model chosen on another PC), the same model's build for this PC is used. If the model isn't offered at all, the recommended one is used, and the status says so.
5. **Downloads it** the first time (the size is shown), then **loads it**. A load that fails is followed by one repair and one more try, then verified.
Each step shows in the status bar and under STATUS in Settings, and installs, downloads and repairs are in the audit log. **Offline mode** stops the install and downloads (starting and loading still work). **On battery,** the automatic run waits for mains power before downloading a model bigger than 1 GB; "Set up now" downloads anyway.
**The buttons** (to do it yourself, or with automatic set-up off):
- **Set up now:** all the steps above, now.
- **Restart service / Stop service:** stopping frees the memory the model uses.
- **Unload model.**
- **Show log:** Foundry Local's own messages.
- **Copy the commands:** the same steps for a terminal.
- **Test** (next to MODEL) asks the model to reply "OK".
**Errors** show Foundry Local's own message and code, not just its hint.
**Intel NPU (Core Ultra, "Intel AI Boost"):** install the Intel NPU driver for Windows (from intel.com) first, or Foundry Local uses the CPU instead. Task Manager → Performance → NPU shows it working while the model answers.
**Address:** leave FOUNDRY LOCAL ADDRESS empty and the app finds it itself; the port is chosen by Foundry Local each time it starts. A typed address (a fixed `--port`) skips the CLI and the automatic set-up.

## Which model should I use?
keywords: model, best model, vram, ram, qwen, llama, phi, gemma, size, 7b, 8b, 14b, 3b, 0.5b, 1.5b, recommend, lightweight
module: ai
**The default is phi-4-mini on Foundry Local** wherever it runs: on the NPU first (a Copilot+ PC's NPU is always preferred over its GPU or CPU), then a GPU with 4 GB+, then a CPU with 8 GB+ of RAM; smaller PCs get Qwen2.5 1.5B or 0.5B. A first run with AI features on picks and sets this up by itself. If a PC has an NPU the AI Engineer isn't using (you chose another engine), it asks once whether to set the NPU up, with a **"Remember my choice"** tick box (Group Policy: `AiNpuSetupChoice` = ask / always / never). Not every PC has an NPU; then nothing is asked.
Tool calling matters for the AI operator. Good picks:
- **Foundry Local (default):** Settings → AI engine → **"Show the models for this PC"** lists exactly what Foundry Local offers on this machine, grouped NPU / GPU / CPU. Each model shows its size, whether it's downloaded, whether it can use tools (the AI operator needs this) and what it's good for, with the recommendation for this PC on top. "Use" switches to it and loads it.
  - **NPU:** phi-4-mini, low power.
  - **Graphics card with 12 GB or more:** qwen2.5-7b (best quality that stays quick).
  - **6–12 GB:** phi-4-mini on the GPU.
  - **CPU only:** phi-4-mini with 16 GB RAM, qwen2.5-1.5b with 8 GB, qwen2.5-0.5b otherwise.
  - Reasoning models (deepseek-r1, phi-4-mini-reasoning) think out loud and can't drive the operator.
- **Ollama with a GPU:** 16 GB VRAM: qwen3:14b or qwen2.5:14b (best quality that fits), qwen3:8b for speed. 8–12 GB VRAM: qwen3:8b / qwen2.5:7b / llama3.1:8b. 4–6 GB VRAM: qwen2.5:3b or llama3.2:3b. Install with "ollama pull <model>".
- **Images** (screenshots, scanned PDFs) need a vision model in Ollama (qwen2.5vl, llama3.2-vision, gemma3); Foundry Local models here are text-only.

## Privacy and what is sent to the AI
keywords: privacy, secrets, redact, mask, cloud, local, data leaves, api key, safe
module: ai
By default only local or LAN endpoints are allowed (localhost, private ranges, Tailscale/CGNAT, .local/.lan names, SSH tunnels). Internet providers need "Allow internet AI providers" in Settings → AI engine.
"Mask secrets" (on by default) replaces private keys, passwords, tokens, API keys, JWTs and credentials in URLs with [REDACTED] before anything is sent. Logs are digested first, so far less raw data leaves the process.
The AI never types into your terminals. It analyses automatically; anything it wants to execute is previewed and needs your approval.

## Claude Code with a local model
keywords: claude, claude code, local claude, anthropic, ollama launch, agent, cli
module: ai
If Claude Code is installed and Ollama 0.14+ is running, Settings → AI engine → "Open Claude Code (local)" (or "⌁ Open in Claude Code" on a report) starts Claude Code in a console window, pointed at Ollama (ANTHROPIC_BASE_URL) and using your local model, in a workspace folder (Documents\DarkCipher\claude-workspace by default).
Claude's own models don't run locally — this is the Claude Code agent driven by your open-weight model, so nothing leaves your machines. Non-essential traffic and telemetry are disabled for that session.
Install Claude Code from PowerShell: irm https://claude.ai/install.ps1 | iex. Pick a model with a large context and good tool use (for example qwen3-coder or gpt-oss:20b) in "Claude Code model".

## Help and FAQ assistant
keywords: help, faq, f1, how do i, search help, assistant help
F1 or sidebar → Help & FAQ. Type a question: matching articles are found instantly on your PC (no AI needed). With the AI engine on, "Ask" also writes a short answer grounded only in these articles and tells you where to click. Right-click a result to copy it.

## Snippets and multi-host runs
keywords: snippets, saved commands, run on many hosts, bulk, parallel, multi-run, batch
Sidebar → Snippets & multi-run: save commands (optionally with sudo), pick hosts (All / None / Group…) and "▶ Run on selected hosts" — results per host appear in the table below. Snippets are also available in Manage → Shell.
**Variables:** `{host}` (the host's name), `{address}`, `{user}`, `{port}`; `{ask:Label}` or `{ask:Label=default}` asks when you send it; `{pick:Label|command}` runs the command on the host and lets you choose from its output lines (for example `{pick:Service|systemctl list-units --type=service --plain --no-legend | cut -d' ' -f1}`); `{{` and `}}` are literal braces. Every value is quoted for the shell it lands in (POSIX or PowerShell) and line breaks are dropped, so an answer can never run a second command. Sent to several terminals, the questions are asked once and each terminal gets its own host's values.
**Terminal button bar:** tick "Show on the terminal button bar" and the snippet becomes a button above each terminal its **scope** fits: `host:<name>`, `folder:<path>` (with its sub-folders), `tag:<tag>`, `env:PRODUCTION`, `shell:posix` / `shell:powershell` or `local`, separated by commas (any one matches; empty = every terminal). Click to send it to that terminal; right-click to send it to the tab group, the pane or every open terminal. A risky command, a PRODUCTION terminal or more than one terminal is shown first. Settings → Terminals & connections → Highlighting & button bar can hide the bar.

## Tunnels and SOCKS proxies
keywords: tunnel, port forward, forwarding, socks, proxy, local forward, remote forward, dynamic
Sidebar → Tunnels: local (-L), remote (-R) and dynamic SOCKS (-D) forwards over a saved host. Start/Stop, copy the local address, and "Start automatically when the app opens". The AI engine can also reach Ollama on a GPU server through a tunnel (Settings → AI engine).

## VPN tunnels (WireGuard, OpenVPN)
keywords: vpn, wireguard, openvpn, tunnel, route, routes, split tunnel, allowedips, redirect-gateway, connect through, .conf, .ovpn, tap, wintun, uac
Sidebar → Tunnels → VPN tunnels. Import a WireGuard .conf or an OpenVPN .ovpn: the private key (and WireGuard pre-shared keys) go to the vault as credentials, the configuration is stored without them, and lines that would run programs are removed (WireGuard PostUp / PreUp / PostDown / PreDown) or refused (OpenVPN up / down / plugin / script-security…). OpenVPN files must have their certificates and key inline (<ca>, <cert>, <key>); auth-user-pass takes a vault password credential, sent over OpenVPN's local management channel — never written to a file.
**Routes ("route traffic down it for certain routes"):** Routes & options… lists the networks that go through the tunnel (WireGuard AllowedIPs / OpenVPN route lines with route-nopull); split tunnel by default; "Route everything" is an explicit choice that warns. DNS servers for the tunnel are optional. ⓘ Status shows the engine's state, endpoint, handshake and bytes, and the routes actually installed through the tunnel's interface (read back from Windows).
**Connect through:** in a host's editor pick the VPN under "VPN (brought up before connecting)": every SSH, SFTP, commander and Remote Desktop connection to that host brings the tunnel up first. "Bring down after N minutes without use" and "Bring it up when the app starts" are per profile.
**Engines:** WireGuard for Windows (wireguard.com) — the app installs the tunnel as WireGuard's own service and asks Windows for administrator approval (UAC) each time it goes up or down; while up, the rendered configuration (with the key) sits in a folder only you, SYSTEM and administrators can read (%LOCALAPPDATA%\DarkCipher Systems\ServerManagement\vpn) and is deleted when the tunnel goes down. OpenVPN Community (openvpn.net) — openvpn.exe runs elevated with the rendered configuration and a management channel on 127.0.0.1; the same folder rule applies. The endpoints go through offline mode like any destination; every up / down and failure is in the audit log.
Honest note: there is no kill switch — if the tunnel drops, traffic for its routes stops (it is not sent in the clear) but nothing blocks other traffic. A viewer (read-only) profile can't bring tunnels up.

## LAN discovery
keywords: lan, scan, discover, network scan, find servers, subnet
Sidebar → LAN discovery scans a subnet of your own network for SSH servers and lets you add the ones you select as hosts.

## Ansible
keywords: ansible, playbook, ad-hoc, inventory, ansible-vault, vault password, ansible-lint, galaxy, requirements, control node, wsl, become, check mode, diff, limit, tags, extra vars, ansible-doc, ansible-config
Sidebar → Ansible. Ansible itself doesn't run on Windows, so pick a **control node**: a WSL distribution on this PC (install one with `wsl --install`, then `pip install ansible` or your distribution's package) or a saved Linux host with Ansible on it. **Version** checks what is installed there.
**Inventory:** generated from the saved hosts you tick — `ansible_host`, `ansible_port` (when not 22) and `ansible_user` per host, groups per folder level (`folder_production_eu`), per environment (`env_production`) and per platform. Preview it, or see it with **Graph**. It holds no secrets: the control node's own SSH keys or agent authenticate to the hosts. It is written to a private temp folder on the node (mode 700) for one run and removed afterwards.
**Commands:** Ping and Facts on a pattern (`all`, a group, `web*:!db`); an ad-hoc module with arguments (tick **become** for sudo); a playbook with `--check` and `--diff` (both on by default — untick check to apply), `--limit`, `--tags`, `--skip-tags`, `--start-at-task`, `--forks`, `--extra-vars`, verbosity and the **vault password**; **Syntax check**, **List tasks** and **List hosts** of a playbook before running it (0.4.0); Lint; Vault (view, encrypt, decrypt, rekey, or "edit-check" to test a password); Galaxy `install -r requirements.yml` and **Install collection** (`ansible-galaxy collection install <name>`), **Installed** (collections and roles on the node); Doc for a module; Config (`--only-changed`). Output streams in and is masked for secrets; **Copy**, **Save…** (a plain file, asked first) and **Add to incident** keep it.
**Security:** nothing of a run is on the command line — the node runs a fixed bootstrap that reads the job from stdin; the vault and become passwords are asked for each run and sent as stdin lines only (never a file, argument, setting or audit line). Patterns, tags and module names are validated; paths and arguments are quoted. Any run that can change servers asks first, and a PRODUCTION host in the inventory means typing PRODUCTION; Galaxy and vault writes on a saved control node ask too. Every run is in the audit log (what ran, where, exit code).

## Network & mail toolbox
keywords: toolbox, telnet, tcp port, banner, traceroute, mtr, path mtu, dns lookup, propagation, http check, redirect, security headers, tls certificate, starttls, spf, dmarc, dkim, mta-sts, tls-rpt, bimi, blacklist, dnsbl, rbl, email headers, header analyzer, whois, asn, mxtoolbox
Sidebar → Network & mail toolbox. Every tab's checks run from this PC or, where it says "from <server>", from a saved server over SSH with that server's tools (nc, mtr / traceroute / tracepath, dig / host, curl, openssl). Each run is kept in **History** (encrypted on this PC, never exported) with its full report.
**Network:** TCP port (the "telnet test": open / closed / filtered, the time, and the banner the service sends first), Traceroute, a live **mtr** (loss % and last / average / best / worst per hop, refreshed every round) and Path MTU (the largest unfragmented packet). ICMP needs no administrator rights.
**DNS:** any record type (A, AAAA, CNAME, MX, TXT, NS, SOA, SRV, PTR, CAA) through your resolver, and **Propagation**: the same question sent directly to Cloudflare, Google, Quad9, OpenDNS, your system resolvers and any resolver IPs you add; answers that differ from the majority are marked ≠ (still caching the old record until its TTL ends, or a split view).
**Web & TLS:** an HTTP check that follows each redirect by hand (status and time per hop), lists the final headers, which security headers are missing (HSTS, CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy) and the certificate; a TLS certificate check of any port — with STARTTLS for SMTP, IMAP, POP3 or FTP — showing names, days left, chain, protocol and cipher, and whether Windows trusts it (an untrusted one is still shown, marked so). Only a GET or a handshake is sent; no cookies or logins.
**Mail domain:** one run does MX (with reverse DNS of each MX address), SPF as an include tree with the 10-lookup and void-lookup limits and — given a sending IP — the result a receiver would reach, DMARC (policy, pct, reports, alignment), DKIM (a selector you name, or a probe of the selectors common providers use; the key's real strength is read from the key), MTA-STS (record and policy file over https, compared with the real MX hosts), TLS-RPT and BIMI. Each finding says what to do. "Blacklists for the MX" jumps to the next tab.
**Blacklists:** a public IP against 18 DNSBLs (Spamhaus ZEN, SpamCop, Barracuda, SORBS…) or a domain against 5 domain lists. "Refused" means the list won't answer public resolvers (Spamhaus does this) — it is never shown as clean; ask again from your own resolver.
**Headers:** paste a message's full headers (Outlook: File → Properties → Internet headers; Gmail: Show original). You get the route oldest-first with the delay at each hop (a long one shows where it queued), the SPF / DKIM / DMARC results the receiver recorded, DKIM d=/s= and whether they align with the visible From, Reply-To elsewhere, missing Message-ID and spam-filter scores. **Nothing is sent or looked up** and the text isn't stored unless you tick "Keep the result in history".
**WHOIS & ASN:** WHOIS over port 43 starting at IANA and following at most two referrals, with a summary (registrar, dates, name servers, status; or netname, organisation, country and abuse contact for an IP block); ASN through Team Cymru's DNS (AS number, name, prefix, registry). Private addresses are refused.
**Security:** every target is validated before anything is built from it (no shell or URL characters reach a command); remote commands use quoted values; all destinations go through offline mode; certificates are validated by Windows except in the labelled inspection-only certificate check, which exchanges no data after the handshake; answers from the network are made printable and masked for secrets before they are shown.

## Credentials and SSH keys
keywords: credentials, password, ssh key, private key, public key, vault, generate key, ed25519, export key, upload key
Sidebar → Credentials holds passwords and SSH keys. Add/Edit/Delete, Copy public key, Upload the public key to Vultr, Contabo or Hetzner, Install public key on hosts…, Export private key…, and "★ Use for quick connect". Hosts → Install key… copies a key into a server's authorized_keys.
**Folders and types (0.4.0):** every credential has a **folder** (any name; nest with `/`, e.g. `Customers/Contoso`) and a **type** — password, SSH key, API token, certificate, cloud, database, other. The screen shows a folder list with counts on the left, a type filter and a search box above the list; **Move to folder…**, **Set type…**, **New folder…**, **Rename folder…** and **Remove folder** (its credentials move to the parent) act on the selection. Shared-library credentials keep their own folders. Folders and types are kept in the encrypted store and in backups.
**🔑 New SSH key…** (Credentials, and Shared library): Ed25519 (recommended), RSA 3072/4096 or ECDSA P-256/384/521, generated on this PC with no temporary files, saved in your vault or straight into a shared library collection you can edit. Then copy the public key, install it on the saved hosts you tick (PRODUCTION hosts are confirmed), or export it.
**Export private key…** asks for a passphrase (recommended): the file is written in OpenSSH's format, readable by ssh, PuTTYgen and this app, and only your Windows account can open it. The passphrase goes to Windows' ssh-keygen through the app's own helper over a one-shot private pipe, never on a command line. Without a passphrase you confirm that anyone with the file can use the key.

## Shared library (team hosts and credentials)
keywords: shared library, team, team hosts, team credentials, share, shared credentials, shared hosts, vault sharing, members, fingerprint, rotate, collection, sync, conflict
Sidebar → TEAM → Shared library shares hosts and credentials with your team through a folder or file share (SMB, NAS or a synced OneDrive / Syncthing folder). Only ciphertext, signatures and public keys are written there. Each item is encrypted under a collection key that is wrapped for each member's own key, and every change is signed by its author.
- **Built in (0.3.0):** team features need no add-on. Create a library… makes you its owner and shows your owner fingerprint. Each colleague uses Join a library… → From its folder…, picks the share and types the owner fingerprint you read to them over a call or chat you trust; then an admin starts the check, both screens show the same 6-digit code, and the admin approves with Editor, Reader or No access per collection. (Team add-ons from older versions still work: Export team add-on… makes one.)
- **The TEAM section** of the sidebar is always there (Settings → Security & privacy → Team features switches it off; admin-protected and a Group Policy): Shared library (members, groups, requests, collections, sync), Team hosts (Hosts, opened on the library's node) and Team credentials (Credentials with "⇄ Shared only" ticked).
- **Groups (👥 Groups…):** a named set of members with its own per-collection access, on top of each member's own. Put people in "Ops" once and give Ops the folders. Enforced by the encryption exactly like a direct grant: the collection keys are wrapped for the group's members; leaving a group re-keys what they lose and lists the credentials to rotate. Windows Hello confirms reveals, exports and backups when "Confirm sensitive actions" is on.
- **Where shared items show:** in Hosts under "Shared · <library>" and under All hosts, and in Credentials (Source column) and every credential picker, always marked ⇄. They never replace or change your own hosts and credentials. Copy to my hosts / Copy to my vault makes your own copy. Publish to a shared library copies one of yours into a collection after showing exactly what goes.
- **Editing:** only editors can change a collection. If two people save the same item at once, both versions are kept as a CONFLICT; pick one in Shared library (right-click the item).
- **Offline:** when the share can't be reached (or Offline mode blocks it), the copy cached on this PC is used and labelled, and your edits wait until the next sync.
- **Removing someone:** right-click them under Members. Every collection they could read gets a new key and is re-encrypted, and the credentials they could see go on the ROTATION CHECKLIST: change those on their targets, then mark them rotated.
- **Audit:** admins can open Shared audit… (signed, hash-chained lines from every member). Each use of a shared credential is also in your own Audit log.
- **Back up your keys:** ⚿ Back up my keys… saves a password-sealed .dcxkey file (use a long password). On a new PC: install the library add-on, then Restore my keys…. The owner should always keep one, because the owner key exists only on their PC.
- **Hand over ownership:** the owner makes the new owner an admin, then uses Hand over ownership…. Nothing changes until that admin presses Accept ownership… on their PC; the previous owner stays an admin.

## Known hosts and host keys
keywords: known hosts, host key, fingerprint, trust, mitm, changed key
The first connection to a server shows its host key fingerprint (trust on first use). If a known server's key changes, the connection is refused with a warning. Sidebar → Known hosts lists and removes trusted keys.

## Vultr cloud
keywords: vultr, cloud, instances, api key, dns, firewall, snapshots, block storage, kubernetes, database, console, reboot, power, billing
Settings → Vultr accounts: add a Personal Access Token (enable the API at my.vultr.com → Account → API and allow this PC's IP). Several accounts are supported; switch with Cloud providers → right-click Vultr → Switch account, or in Settings.
Cloud providers → Vultr covers instances, bare metal, DNS, firewalls, networks/VPC, block and object storage, snapshots/backups, load balancers, Kubernetes, databases, SSH keys, startup scripts, ISOs, reserved IPs, billing and more — list, create, edit, delete and actions, all from the Vultr API.
New in 0.1.14: IPv6 reverse DNS (set, list, delete), reset an IPv4 reverse DNS to the default, remove an extra IPv4 and ISO status on instances; IPv6, upgrades and VPC attach / detach on bare metal; Block snapshots; File System (VFS) with attachments; Sub-accounts.
Hosts linked to a Vultr instance get "🖥 Vultr console" (out-of-band web console) and "⚡ Vultr power cycle" in Manage. "{ } API explorer" calls any endpoint; "⧉ Vultr portal" opens my.vultr.com in an app window.
Accounts can be disabled (kept, but never used) with Disable / enable in Settings → Vultr accounts.
**Hosts follow their servers (0.6.24, every cloud provider).** Whenever a provider's instance list loads (its Instances screen, or the dashboard's Cloud servers card), saved hosts linked to its servers are brought up to date: a changed public IPv4 is copied to the host, and if you destroyed a server and built a new one **with the same name**, the host moves to the new server and its new address (only when exactly one new, unlinked server has that name). Hosts whose server no longer exists are listed, and you're asked once whether to remove them (credentials in the vault are kept; Cancel keeps them). Padlocked and shared-library hosts are never changed. Each change is in the audit log. Turn it off in Settings → Terminal ("Keep hosts in step with their cloud servers") or by Group Policy (CloudSyncHosts). The new server has a new host key, so the first connection asks you to trust it.

## Contabo cloud
keywords: contabo, vps, vds, cloud, client id, client secret, api user, api password, instances, rescue, reinstall, snapshot, rollback, cancel, secrets, private network, firewall, dns, images
**Connect an account:** Settings → Contabo accounts → ＋ Add account. Contabo's API needs four values, all from the Contabo Customer Control Panel → API:
- the **Client ID**;
- the **Client Secret**;
- your **API user** (your Contabo login e-mail);
- an **API password**, which you set on that page; it is not your login password.

Test checks the sign-in and reports how many instances the account has. The values are stored only in the encrypted settings and are never shown again. Several accounts are supported: while a Contabo screen is open, the header shows CONTABO ACCOUNT, where you switch. Accounts can be renamed, given new credentials, disabled or removed.
**The CONTABO sidebar section:**
- **Instances:** list, ▶ Start, ⏻ Shut down (graceful), ↻ Restart, ■ Stop (hard), ⌨ SSH, ＋ Add to hosts, Rescue…, Reset password…, Reinstall…, Snapshots ›, Upgrade options, Cancel contract…, Rename (✎ Edit), ＋ Create (order).
  - **State-aware buttons:** a button is enabled only when it can work (e.g. Start only on a stopped server); hover a greyed one for the reason.
  - **After an action:** the screen watches the server until it reaches the new state ("waiting for running…"), for at most a few minutes, and only while the tab is open.
- **Snapshots** (from an instance): create, rename, delete, ↺ Roll back (type the snapshot's name).
- **Images:** standard and custom images. A custom image is downloaded by Contabo from a URL you give (.qcow2 or .iso; needs the Custom Images add-on). Rename; delete custom images.
- **Secrets:** the SSH public keys and passwords that orders, reinstalls, rescue and password resets use. "⇧ Upload a vault key…" (or Credentials → More → Upload public key to Contabo) adds the public half of a key from this app's vault; the private key never leaves it.
- **Private Networks:** create, rename, delete; add or remove an instance.
- **Firewalls:** create, edit, rules as JSON; assign or unassign an instance; preset rules.
- **DNS Zones:** zones and their records (A, AAAA, CAA, CNAME, MX, SRV, TXT).
- **Reference:** data centers, standard images, and Contabo's action history (who started, stopped or restarted what).
- **New in 0.1.14:**
  - **Object Storage:** order (billed monthly), rename, Usage, Resize… (grow only, billed), Cancel contract… (type the name; there is no delete).
  - **VIPs:** additional and floating IPs; assign to an instance, unassign.
  - **Reverse DNS (PTR):** create, change, delete PTR records.
  - **Tags:** create, recolour, delete, tag an instance, and see / remove a tag's assignments.
  - **Instances:** Add Private Networking… and Add Automated Backup… (billed add-on orders).
**What Contabo's API can't do, so the app doesn't offer it:**
- **No delete:** a server ends by **Cancel contract…**; Contabo removes it on the cancellation date.
- **No resize:** "Upgrade options" shows what is possible, but changing the product isn't available through the API (add-ons can be ordered, see above). Moving a server to another region is left to the customer panel.
- **No web console:** use SSH, or the rescue system.
- **Ordering is billed:** ＋ Create is a paid order for the chosen period (1, 12 or 24 months). The app asks first, and an order is never re-sent automatically. If the answer is slow, you're told to refresh before trying again. Product ids such as V153 come from Contabo's product list, because the API has no catalogue.
**Hosts and safeguards:**
- Hosts → right-click → ⇩ Import from Contabo adds servers as hosts (user: the server's default user), updates the address of hosts already linked (and moves a host to a server rebuilt under the same name), and lists linked hosts whose server no longer exists (you can remove the link; hosts are never deleted without asking).
- Manage on a linked host offers "⚡ Contabo power cycle".
- Actions on a server whose host is PRODUCTION ask you to type its name.
**Offline mode** blocks api.contabo.com and auth.contabo.com, and the Privacy view lists both.
**Troubleshooting:**
- **"the client id or client secret was refused":** copy both again from the Customer Control Panel → API.
- **"the API user or API password was refused":** the API password is set separately on that API page (resetting it there and replacing the credentials here usually fixes it).
- **"This Contabo API user isn't allowed to do this":** the user's role lacks that permission.
- **"limiting requests":** wait a minute and refresh.
- Every error from Contabo shows a request id. Give it to Contabo support if you contact them.

## Audit log
keywords: audit, history, log of changes, who did what, export audit
Sidebar → Audit log records every change you or the AI made (commands, service actions, power, Vultr changes, reports, exports), encrypted line by line. Filter and Export CSV.

## Security and encryption
keywords: security, encryption, aes, master password, dpapi, backup, restore, encrypted, store
All hosts, credentials, settings, API keys, known hosts, snippets, tunnels, reports and the audit log are encrypted with AES-256-GCM under one random data key. The key is protected by your Windows account (DPAPI) and/or a master password (PBKDF2-SHA256, 600,000 iterations). With "require on start" only the password can open the store.
Settings → Security: set/remove the master password, Encrypted backup… (own password, restorable on another PC) and Restore backup….
**The whole application is encrypted:** every file the app writes — data, settings, notes, incidents, reports, session logs, the audit log, error logs, caches, snapshots, add-on data, recovery drafts, AI chats and memory — is sealed under the data key; the key ring holds only wrapped copies of that key. With a master password that is required at start (or Windows Hello), the Windows-account copy of the key is removed, so nothing is readable without the password or Hello — not by an administrator, not from a copy of the disk. "✓ Verify everything is encrypted" walks the store and the logs and reports any plain file; the self-test does the same on every build. Plain by design: keyring.json (wrapped keys), location.json and profiles.json (paths and names), add-on program files.
Exports you choose (CSV, Markdown, HTML, saved text) are plain files — the app says so each time.

## Profiles and organisation sign-in
keywords: profile, profiles, switch profile, multiple users, active directory, ad, domain, ldap, ldaps, starttls, sasl, kerberos, negotiate, entra, azure ad, microsoft entra, oidc, pkce, sso, single sign-on, group, role, viewer, read-only, required group, offline grace, join domain
Settings → Security & privacy → Profiles (and the status-bar lock menu → Switch profile). Several profiles in one installation, each its own encrypted store — own data key, hosts, vault, settings, notes, team membership; nothing is shared between them. The store you already had is the first ("default") profile; nothing moves. With more than one profile the app asks which to open at start (or opens the last one without asking, if you tick that).
- **Local profiles** are always available (unless your organisation forbids them by policy): New local profile…, then Switch to… restarts the app into it.
- **Sign in with your organisation:** New profile with organisation sign-in… — the profile then opens only after that sign-in succeeds:
  - **Windows / Active Directory:** the Windows session itself is the proof (Kerberos / Negotiate at logon; nothing typed). The profile is bound to the domain account's SID; another account can't open it. Groups come from the Windows token.
  - **LDAP over TLS:** LDAPS (636) or StartTLS (389 → TLS) — never plain LDAP. The server certificate must validate through Windows and match the host name, or match a SHA-256 pin you set; there is no "accept any". Bind with the Windows session (SASL / Negotiate, Kerberos) or a simple bind inside TLS with a password asked each time and never stored. The account must exist and be enabled; groups from memberOf.
  - **Microsoft Entra ID:** OAuth 2.0 authorization code with PKCE through your browser, so MFA and Conditional Access apply. Needs an app registration (public client, redirect http://127.0.0.1) — its client id and the tenant. The id_token is verified against the tenant's keys (RS256 by kid; issuer, audience, expiry, nonce, tenant); only its claims are used and no token is stored.
- **Groups → role:** map group names to admin / operator / viewer (first match wins; no match = operator). A **viewer** profile is read-only: nothing can be saved. A **required group** keeps everyone else out.
- **Offline:** a successful sign-in leaves a sealed, tamper-evident proof in the profile's store; while the directory can't be reached, a proof younger than the grace (default 7 days, 0 = never) opens the profile. Forget cached sign-in proof removes it.
- **For companies (Group Policy):** RequireDirectorySignIn = 1 forbids local profiles on the PC. Every sign-in, refusal and cached opening is in the audit log.
Honest note: the directory sign-in gates opening the profile and sets the role; the files are protected by the profile's own data key and master password / Hello as always.

## First-run setup
keywords: setup, first run, wizard, welcome, getting started again, onboarding, recommended settings
A new store opens with a short guided setup — every step optional: protect the app (master password required at start, Windows Hello, auto-lock), personal settings (theme, default user, confirmations), AI features (off, or local only with Foundry Local + phi-4-mini), and your team (create or join a shared library). Run it again from Settings → About.

## App lock, 2FA and sandbox mode
keywords: lock, app lock, auto lock, idle, 2fa, two factor, totp, authenticator, recovery codes, qr code, sandbox, password on start, lock screen
Settings → Security: Auto-lock after N minutes idle, lock when Windows locks, or Ctrl+Shift+L / the lock icon in the status bar. Locked, every window is hidden while sessions keep running; unlock with the master password and/or a code.
2FA: "Set up two-factor" shows a QR code for Microsoft/Google Authenticator, Authy, 1Password or Bitwarden (TOTP, RFC 6238), asks for a code to confirm, then shows 10 one-time recovery codes — save them. Turning 2FA off needs the password and a current code.
Honest note: 2FA gates opening the app. The files themselves are protected by the data key and your master password; a code doesn't make the encryption stronger against someone with a copy of the files.
Sandbox mode: master password required at start, temp and share files wiped on exit, auto-lock set to 15 minutes. (Error logs are encrypted in every mode.)

## Admin protection: security settings need administrator approval (UAC)
keywords: admin, administrator, uac, elevation, protected settings, protect, security settings, policy, hklm, approve, standard user, run as administrator
Settings → Security & privacy → Admin protection. Security settings are kept where only administrators can change them, so nothing running as you (a script, a malicious add-on, someone at your unlocked PC) can switch them off: AI features, internet AI providers, masking secrets and AI auto-run; offline mode, its approved list and add-ons while offline; the local API and its port; Telnet; lock when Windows locks, auto-lock, sandbox mode; Windows Hello sign-in and confirmations. Settings marks them with 🛡.
- **Protect them once:** "Protect these settings now…" (one UAC prompt). Until then they work as before, unprotected.
- **Changing one:** Windows asks for administrator approval (UAC). Then a second window, from the elevated app, lists exactly what changes, for which Windows user and why; nothing is written unless you press Apply (Cancel is the default). On an administrator account UAC is a click; on a standard account it asks for an administrator's password.
- **Stricter at once:** turning offline mode on, Telnet off, shortening the approved list and similar changes apply straight away for this session; the approval makes them last after a restart.
- **Where:** HKLM\SOFTWARE\DarkCipher Systems\ServerManagement\Protected, one entry per Windows user: administrators can write, everyone can read. No password or key is ever stored there. If anyone other than administrators could change that key, the app ignores it and says so on the card.
- **Only the installed app** (Program Files) can ask for approval. A portable or development copy says it can't protect settings, and won't change them once an administrator has protected them.
- The card shows what is protected now and the last changes (when, and which administrator approved them). Each change is in the audit log.

## Managed by your organisation (Group Policy)
keywords: group policy, gpo, policy, managed, organisation, organization, intune, admx, registry, administrator, greyed out, can't change, change not kept
Settings → Security & privacy → Managed by your organisation shows the settings your administrator sets for this PC or for you, with their values. They can't be changed in the app: a change is put back, and the status bar says it is set by policy. Values the app couldn't use (wrong type, out of range, or a setting that can't be a policy, like an API key) are listed there too and ignored.
Administrators: almost every setting is a policy under HKLM or HKCU\SOFTWARE\Policies\DarkCipher Systems\ServerManagement, named like the setting (OfflineMode, AccentColor, LocalApiPort…). ADMX templates ship with every release (the policy folder); the administrator guide and POLICIES.md list them all. The computer policy wins over the user's, and both win over admin protection.

## Uninstalling: what is removed
keywords: uninstall, remove, delete data, clean up, leftovers, leave nothing, silent uninstall, msiexec, remove everything, library, registry
The program, shortcuts, the app's registry keys and its temp files always go. On "Remove your data too?" you choose what else: the encrypted store (with your Windows Hello key), your shared library membership (local copies and member keys; the team's share is never touched), the cache and logs, settings enforced on this PC (admin-protected settings and policy values), and whether to do this for every Windows user on the PC. Nothing of yours is deleted unless ticked. Backups and keys you saved elsewhere, %USERPROFILE%\.ssh and your servers are never touched.
Silent: msiexec /x <the MSI or its product code> /qn REMOVE_DATA=1 REMOVE_LIBRARY=1 REMOVE_CACHE=1 REMOVE_SETTINGS=1 REMOVE_ALLUSERS=1, or REMOVE_EVERYTHING=1.

## Windows Hello: sign in, confirm, lock
keywords: windows hello, hello, face, fingerprint, pin, biometric, sign in, unlock, confirm, tpm, passwordless, prompt behind, prompt in front
Settings → Security & privacy → Security & encryption → WINDOWS HELLO (both switches are admin-protected).
- **Sign in with Windows Hello:** the app opens with your face, fingerprint or PIN. A Hello key in the PC's TPM signs a fixed challenge, and that signature unlocks the data key (since 0.6.12 together with a secret only your Windows account can open, so it is post-quantum: still one Hello prompt, and an older setup upgrades itself at the next unlock); the Windows-account copy of the key is removed, so nothing opens without Hello or the master password. A master password must exist first: it is the fallback (another PC, a changed reader, a reset PIN). The lock screen takes Hello too. With 2FA on, type the code first, then use Hello.
- **Confirm sensitive actions:** Hello before opening a saved credential (the editor shows private keys), exporting a private key, and every export or backup; and before any protected setting change. When Hello isn't available (not set up, a Remote Desktop session) the master password is asked instead.
- **AI features lock with Windows Hello:** Settings → AI features → Lock → "Windows Hello": no password to remember; Hello confirms turning AI on or off, internet AI providers and removing the lock.
- **The prompt in front:** the app tells Windows which window is asking, so the Hello prompt opens on top of it (Windows 11). If it still opens behind, click it on the taskbar; the app keeps waiting.
- Set up Hello first in Windows Settings → Accounts → Sign-in options.

## AI features: on, off and the lock
keywords: ai features, turn off ai, disable ai, no ai, ai off, remove ai, lock, ai lock, password, policy, administrator, group policy, aifeatures, msi, restart
Settings → Security & privacy → AI features decides whether the app has AI at all. Off means no AI screens, sidebar items, menu items, settings, help articles, background work or connections. Offline features (log digests, rule-based reports, help search) work as before, and your AI settings and chats stay in the encrypted store, so turning AI back on restores them. A change applies after a restart: the card offers "Restart now".
New installs start with AI off. Upgrading from 0.1.x keeps AI on if it was in use (enabled, with an engine).
**The lock:** "Lock with a password…" (at least 12 characters) protects three things: turning AI features on or off, "Allow internet AI providers", and changing or removing the lock. The password is asked in the app each time one of them changes. After three wrong passwords the next try waits 30 seconds, doubling up to 10 minutes, and restarting the app doesn't reset that. Every attempt is in the audit log (never what was typed). There is no recovery: a forgotten password can only be cleared by deleting ai-lock.dcx from the data folder while the app is closed, and the audit log records that at the next start.
**Administrators:** the machine policy HKLM\SOFTWARE\Policies\DarkCipher Systems\ServerManagement, DWORD AiFeatures, forces it (0 = off, 1 = on) for every user; the card then shows "set by your administrator". The MSI writes the 0 with: msiexec /i DarkCipherServerManagement-<version>-x64.msi AIFEATURES=0 (pass it on every upgrade, or use Group Policy). For one run, the environment variable DCX_AI_FEATURES=0 or 1 overrides the setting — except while the lock is set.
**Add-ons:** with AI off, the six terminal capabilities that act on the AI Engineer's target session (terminal_run, terminal_read_screen, terminal_send_keys, terminal_interrupt, terminal_switch_target, terminal_open_session) are not listed, and calling one returns "capability unavailable (AI off)". Everything else is unchanged.

## File commander (two-pane SFTP / SCP file manager)
keywords: commander, total commander, two pane, dual pane, sftp, scp, copy files, move files, upload, download, transfer, queue, f5, f6, winscp, file manager
Sidebar → File commander, right-click a host → "File commander", or Files → More → "Open in File commander". Each pane is This PC or any saved SSH host, so this PC ⇄ server and server ⇄ server both work.
Keys: Tab switches pane · Enter opens · Backspace goes up · Alt+←/→ back/forward · F2 rename · F3 view (PDFs open in the document viewer) · F4 edit · F5 copy → · F6 move → · F7 new folder · F8/Del delete · Ctrl+R refresh · Ctrl+H hidden files · Ctrl+U swap panes. Typing jumps to the filter box (* and ? work). Click a column header to sort.
Drag between panes (hold Shift to move) or drop files from Windows Explorer. Transfers run in the background queue at the bottom (two at a time, own connections, progress, cancel from right-click); a move deletes the originals only after everything arrived. Name clashes ask once: overwrite, skip existing, or keep both.
Safe transfers: each file is written as "name.dcxpart" and only renamed to its real name when complete and its SHA-256 matches the source (the hash runs on the server with sha256sum when it can), so a half-copied file never looks finished. A dropped connection retries the file up to 3 times on a fresh connection and continues where it stopped. Right-click a transfer → ⏸ Pause / ▶ Resume, or ↻ Retry a failed/cancelled one (it resumes the unfinished files). Modified times are kept (and Unix permissions between two servers). More → Transfer options switches verify / keep times / retries; More → Transfer history lists the last 300 transfers (stored encrypted).
Sync: the "⇄ Sync ▾" button updates or mirrors one pane's folder to the other in one step, or opens the compare window and saved jobs — see "Folder sync" below.
SCP: servers without SFTP work too — the app switches to SCP automatically (the pane title shows "· SCP"), or choose per host under More → "Files for <host> over…". With SCP an interrupted file restarts instead of resuming.
Local deletes go to the Recycle Bin; remote deletes are permanent and ask first (typed confirmation for folders). Right-click: copy path/name, scp command, terminal here, open in File Explorer, properties, chmod, ✦ Explain with AI.

## Folder sync (compare, sync jobs, versions, schedules)
keywords: folder sync, sync, synchronise, synchronize, freefilesync, two-way, mirror, update, compare folders, sync job, versioning, versions, recycle bin, filter, exclude, include, schedule, real-time, backup folder, deletion guard
**Quick:** File commander's "⇄ Sync ▾" button syncs the two panes' folders in one step — Update → / ← Update (copy new and changed files, never delete), Mirror → / ← Mirror (make one side a copy of the other) or Two-way. It compares, shows one confirmation with what will be copied and deleted and where deleted files go, then runs in the transfer queue. The same menu opens the compare window and your saved jobs.
**The sync window:** "⇄ Sync ▾ → Compare / sync folders…" (or More, or Ctrl+X D), Sync jobs…, or Settings → Data & backups → Folder sync. It compares straight away. Pick what to do with one click (⇄ Two-way, Update →, Mirror →, ← Update, ← Mirror, Custom); the line under the buttons says what will happen and the preview updates. Everything else is under "More options". Each side is this PC or any saved SSH host. A job holds one or more **folder pairs**; "+ Add folder pair" adds one, ⇆ swaps its sides.
**Compare** (F5) lists every difference, nothing changes yet. Compare by time + size (with a tolerance in seconds, and optionally ignoring exact 1–2 hour shifts from daylight-saving or FAT disks), by content (SHA-256, on the server when it can) or by size only.
**Synchronise variants:** Two-way — remembers the last sync, so a file deleted on one side is deleted on the other, a changed file is copied over, and a file changed on both sides is a conflict (left alone, or newer / left / right wins, as you choose); a file deleted on one side but changed on the other is copied back, never lost. Mirror → / ← — makes the target identical (deleting extras can be switched off). Update → / ← — copies new and changed files one way, never deletes, leaves a newer target alone. Custom — pick the action for each kind of difference (only left, only right, left newer, right newer, different).
**The preview:** every row has a tick box and an action; right-click to copy either way, delete on either side, leave alone, go back to the planned action, or exclude it from now on (added to the filter). The buttons above the list show or hide copies, deletions, conflicts and rows left alone. A folder that exists on one side only is one row when everything in it goes the same way. Nothing runs before "Synchronise…", which says where deleted and overwritten files go; deleting asks you to type the count, and a PRODUCTION server asks as usual.
**Filter:** include and exclude lines, one per line: * and ? match, a leading / anchors at the sync folder, a trailing / means a folder (*.tmp, /build/, node_modules/). Optional minimum and maximum size (10 MB, 1.5 GB) and maximum age. A folder holding excluded files is never deleted as a whole. The versions folder and .dcxpart files are never synced.
**Deleted and overwritten files:** Recycle Bin (this PC; a server has none, so it uses versions), Versioning — moved into a versions folder (".dcx-versions" in the sync folder, or a folder you choose per side), named with a time stamp per file, one time-stamped folder per run, or replaced (only the last version) — or Permanent, which on a server is allowed only when an administrator allows it (Settings → Folder sync, UAC). Versions older than the days you keep (default 30), or beyond a count, are pruned after each run.
**Settings per pair:** the "Settings for" list edits the whole job or one pair; tick "This pair has its own settings" to give a pair its own compare, variant, filter and deletion settings.
**Sync jobs:** "Save job" keeps the job (encrypted, like the Two-way memory of the last sync). The Sync jobs window opens, runs now, duplicates or removes jobs; "Forget the last sync" makes Two-way start afresh. **Schedule:** manual, every N minutes, daily at a time, on chosen weekdays, or real-time (this PC's side is watched and synced a few seconds after a change; a server side is checked every N minutes). Scheduled runs need the app running and the data store open, never run in a viewer profile, leave conflicts alone, and stop a pair when a folder is missing (an unplugged disk) or when it would delete more than the guard's share of a side's files (default 50 %, when more than 10). Results show on the job, in the status bar and in the audit log; progress is in the Transfers queue. Settings → Folder sync switches all scheduled runs off and holds the defaults; every option is also a Group Policy.

## File commander: Midnight Commander keys and tools
keywords: midnight commander, mc, mcedit, f9, menu bar, insert, mark, tag, select group, invert, hotlist, history, find file, panelize, quick view, ctrl+x, symlink, hard link, chown, archive, zip, tar, tgz, user menu, command line, ctrl+o, directory size, ctrl+space
**Menu bar:** F9 (or F10) opens Left / File / Command / Options / Right; F1 is this help.
**Marking:** Insert or Ctrl+T marks the item under the cursor and moves down; while items are marked the arrow keys move the cursor without unmarking. + marks by shell pattern (*.log or *.conf;*.cfg — end it with / for folders), − or \ unmarks, * inverts the marks on files. Copy, move, delete, chmod and chown act on the marks; view, edit and rename act on the cursor.
**Keys:** Shift+F4 new file · Shift+F6 rename · Ctrl+F4 hex editor · Ctrl+Space folder sizes (on “..”: every folder) · Ctrl+\ directory hotlist · Alt+H directory history · Ctrl+Shift+F or Alt+? Find file · Ctrl+Q quick view in the other pane · Ctrl+S filter · Alt+I same folder in the other pane · Alt+. hidden files · Ctrl+O terminal here · Ctrl+F2 user menu · Ctrl+PgUp / Ctrl+PgDn up / into. Ctrl+X then S symbolic link · L hard link · O chown · C chmod · D compare folders · I info · Q quick view.
**Find file:** a name pattern and optional text; the matches become the pane's listing (Enter goes to a match, “..” returns to the folder). On a server it runs find and grep -F over SSH with every value quoted.
**Archives:** Enter on a .zip (.jar .war .nupkg .whl), .tar, .tar.gz or .tgz browses inside it like a folder, read-only (up to 512 MB, read into memory). F3 views, Ctrl+F4 shows bytes, F5 copies files out to the other pane. Entries whose names climb out (../) or are absolute are marked “⚠ unsafe name” and never extracted; links inside archives aren't extracted.
**Links and owners:** links are made in the other pane's folder on the same machine (on Windows a symbolic link needs Developer Mode or an administrator). chown takes user, user:group or :group, can be recursive, and offers sudo when the server refuses.
**Command line** (under the panes): Enter runs the command in a terminal tab in the pane's folder — over SSH for a server, PowerShell for this PC; “cd folder” just moves the pane. Ctrl+Enter adds the current name (Ctrl+Shift+Enter the full path); ↑/↓ recall earlier commands.
**User menu** (Ctrl+F2, Command → User menu): your own commands for servers, with %f (current file), %F (other pane's file), %d / %D (folders), %s (marks or current), %t (marks only) and %% — every value is shell-quoted. Each runs in a visible terminal tab after you confirm it. Command → Edit the user menu opens it as text; the menu and the hotlist are kept in the encrypted data store.

## Text editor, Vim mode and the hex editor
keywords: editor, edit file, vim, vi, vimrc, profile, keybindings, mapping, hex, hex editor, binary, bytes, find, replace, regex, line endings, crlf, lf
F4 in File commander (or Files → Edit) opens a text file — local or on a server — in the editor. Ctrl+S saves (a server file is checked first: if it changed since you opened it you're asked before overwriting), Ctrl+F finds, Ctrl+H replaces (plain or regex, one undo step), Ctrl+G goes to a line, F3 finds the next match. The status bar shows Ln/Col and the line endings; a Linux (LF) file stays LF and a Windows (CRLF) file stays CRLF.
Vim mode: tick "Vim" in the editor or press Ctrl+Alt+V (Settings → Terminals & connections → Text editor makes it the default). Normal, insert, visual (v / V), counts, motions (h j k l w b e 0 ^ $ gg G f F t T + -), operators (d c y > < with any motion, dd cc yy), x X D C J ~ r p P u Ctrl+R, "." repeat, / ? n N search, :s and :%s substitute, and :w :wq :x :q :q! :e!.
Vim profiles (Settings → Text editor): each is a small vimrc — set tabstop / shiftwidth / expandtab / ignorecase / smartcase / wrap, let mapleader, and nnoremap / inoremap / vnoremap mappings (e.g. inoremap jk <Esc>). Choose the profile in the editor's drop-down. Profiles only change keys and options: lines such as source, autocmd or ! commands are listed as unsupported and ignored, never run. Profiles are stored with your encrypted settings.
Hex editor: Ctrl+F4 in File commander, or any binary file opened with F4 (it asks first). Type hex digits in the hex column or text in the text column (Tab switches column); Insert switches overwrite / insert; Del deletes in insert mode; Ctrl+Z / Ctrl+Y undo and redo; Ctrl+F finds hex bytes ("7F 45 4C 46") or text; Ctrl+S saves. The status bar shows the offset, the value at the cursor (u8 / i8 / u16 / u32) and, for a selection, its CRC-32 and SHA-256. Files up to 64 MB.

## Document viewer, Vultr invoices and billing
keywords: invoice, invoices, billing, pdf, document, print, statement, pending charges, billing history, cost, viewer, save as pdf, portal
Vultr → Invoices: double-click (or "📄 Open invoice") opens a printable invoice built from the API's line items — zoom, print, Save as PDF, save a copy, open externally, and "Official PDF (Vultr portal)". Vultr's API has no PDF download, so the official PDF is in the portal; every number in the app's copy comes from the API.
Every Vultr list has "📄 Docs & ✦ AI ▾": view the list as a document (print / PDF), review the list with AI, explain the selected item. Invoices also have "✦ Review invoice with AI" and "✦ Compare with the previous invoice".
Documents are held in memory and served to the viewer with no-store; a plain copy exists on disk only when you Save or Open externally (that temp copy is deleted when the app exits).

## AI across the app (and what it can see)
keywords: ai, explain with ai, review with ai, privacy, what does the ai see, local ai, cloud ai, npu, ollama, file tools, ai files, ai copy files
module: ai
"✦ … with AI" actions appear on files (Commander), documents and invoices, Vultr resources, logs, reports, terminals and help. Nothing is sent until you click; an internet provider asks "send this?" first and is blocked unless allowed in Settings → AI engine; keys, passwords and tokens are masked (including JSON fields like "default_password"). Each area can be switched off under Settings → AI engine → features ("Files & documents" covers files, documents and Vultr explanations).
The AI operator can list and read files on hosts (SFTP) and on this PC, and copy/move/create/rename/delete — every change, and anything on this PC, needs your approval; key and credential files (.ssh, *.pem, *.key, .env, shadow, .aws…) are refused for reading. Its transfers appear in the Commander's queue and in the audit log as "ai".

## Explain an option, an error or a screen with AI
keywords: explain this option, explain setting, explain this error, error dialog, what does this setting do, ask ai about this screen, screen help, ai help, unhandled error
module: ai
Settings: right-click any option (a labelled field, a checkbox or a button) → "✦ Explain this option with AI", or anywhere on a card → "✦ Explain the … settings with AI". The AI gets the card name and description, the option's label and tooltip, and — only for on/off switches and list choices — its current value. Text fields (keys, URLs, user names) and passwords are never sent.
Error messages: error dialogs have "✦ Explain this error with AI" next to OK, including the "Something went wrong, but the app kept running" dialog. The message, exception type and stack frames are sent with source paths, your Windows profile folder, your user name and any keys/passwords/tokens removed. The button only appears when an AI engine is on and "Files & documents" is enabled.
Any tab: right-click the tab header → "✦ Ask AI about this screen…", type a question; the answer is grounded in the matching help articles plus the screen's button labels (no list contents; host tabs are described as "Terminal", "SFTP file browser" etc., never by host name). Uses the "Help" AI feature switch.
Nothing is ever sent automatically: each of these runs only when you click, and internet providers ask "send this?" first.

## AI Engineer sidebar (dock, detach, context list)
keywords: ai sidebar, ai engineer, ai panel, dock, docked, detach, reattach, pop out ai, second monitor, collapse, hide, context, context list, what does the ai see, blocked, credentials blocked, new conversation, clear conversation, ctrl+shift+a
module: ai
The AI Engineer sidebar sits on the right of the main window. It holds one conversation at a time (＋ starts a new one; the drop-down switches between the last dozen, kept in memory only until the app closes) and a CONTEXT list showing exactly what the model may see.
Every "✦ … with AI" action (files, documents, Vultr resources, hosts, snippets, tunnels, audit log, settings options, error dialogs, "Ask AI about this screen") adds what you clicked as a context item and answers in the sidebar — no separate answer windows. Internet providers still ask "send this?" first.
Context items: ✓ included (sent with your next question), ○ available (click ✓/○ to switch), × removes it. "Vault credentials — BLOCKED" is always listed: passwords, private keys, passphrases and API keys are never sent, and anything you share is masked when it is added. Items are snapshots taken when you clicked; terminals, Remote Desktop and notes are never streamed to the AI.
Buttons: ＋ new conversation · ⌫ clear (messages and shared items) · ⧉ detach into its own window (move it to another monitor, maximise it) and ⇲ reattach — the same conversation moves, nothing restarts · ▸ collapse to a thin strip (click it to open again) · ✕ hide. Closing the detached window puts the sidebar back in the dock, collapsed, with the conversation intact.
Drag the left edge to resize (double-click it to collapse). Ctrl+Shift+A hides / shows it, or use the sidebar item AI → AI Engineer sidebar (right-click it for dock / detach / collapse / hide). Width and docked / collapsed / hidden / detached state are saved in the encrypted settings; a detached sidebar reopens detached. It follows Settings → Appearance → Density and the UI font and text size.

## Local-only storage, OneDrive and synchronised folders
keywords: onedrive, dropbox, google drive, icloud, sync, synchronised, synchronized, cloud, local only, storage location, data location, network share, nas, privacy, where is my data
Nothing is uploaded or synchronised by the app. Settings → Data & storage shows where every kind of data lives and whether that folder is local, inside a sync client (OneDrive, Dropbox, Google Drive, iCloud, Box…) or on a network share, with how it was detected.
Detection uses the Windows sync-root registry, OneDrive's account registry, Dropbox's info.json, Cloud Files placeholder attributes and the drive type, and follows junctions/symlinks — it does not just look for "OneDrive" in the path.
If the encrypted store is in a synchronised folder, a warning appears at start: Change data location…, Review storage, Continue anyway, or Don't show this again (remembered for that folder; moving the store elsewhere re-arms it; "Show the sync warning again" in Data & storage turns it back on).
Plain files you write (CSV, saved text, screenshots, exported private keys) and encrypted backups are checked too: if the destination is synchronised you're told before writing, and can go ahead. Screenshots and the Claude Code workspace default to Pictures / Documents unless those are synchronised (OneDrive folder backup), then to a local app folder.
Error logs are sealed with the data key (Data & storage → View error log decrypts them). Temporary share, .rdp and key-generation files are deleted on exit and at the next start.

## Customise the sidebar: style, pinned pages, sections, density
keywords: sidebar, shown pages, hide page, show page, icons only, compact sidebar, rail, sidebar width, pin, pinned pages, rearrange, reorder pages, drag page, move page, landing page, start page, open at start, reorder sections, move section, hide section, show section, drag header, navigation, density, compact, comfortable, row spacing, padding, layout, customise, customize
Settings → Navigation → Sidebar:
- **Sidebar:** Expanded (icons and names), Compact (tighter rows) or Icons only (a narrow strip; hover an icon for its name, right-click it for the same menu). Dragging the Icons only strip wider switches back to Expanded at that width.
- **Sidebar width** of the expanded sidebar (you can also drag its edge).
- **Open on start:** the page the app opens on (the dashboard by default).
- **Pinned pages:** up to 6 pages in a PINNED section at the top of the sidebar. Point at a page and click its 📌 (a pinned page shows its pin in the accent colour; click it to unpin), drag a page onto PINNED (or onto a pinned page to put it there; a PINNED drop zone appears while you drag if nothing is pinned yet), switch them on here, or right-click a sidebar item → Pin to the top of the sidebar. Drag pinned pages to reorder them, or right-click → Move up / Move down; drag one out of PINNED to unpin it.
- **Rearranging pages:** drag a page up or down within its section (or right-click → Move up / Move down). A page stays in its own section. Right-click a section header → Reset sidebar layout puts the default order back.
**Shown pages** (Settings → Navigation → Navigation & explorers): switch off any page you never use, or right-click it in the sidebar → Hide. A hidden page leaves the sidebar and its pin but still opens from links, menus and quick connect. Settings is always shown, so you can bring pages back.
Drag a sidebar section header (OVERVIEW, SERVERS, TOOLS, DOCUMENTS, AUTOMATION, LOCAL, VAULT, TEAM, AI, LOGS, SYSTEM) onto another header to move it there, or right-click a header → Move section up / Move section down. Right-click → Hide hides a section; "Show hidden section ▸" or Settings → Navigation → Navigation & explorers brings it back. "Reset sidebar layout" restores the default order and shows everything. At least one section always stays visible, and screens of hidden sections still open from links, menus and quick connect.
Settings → Appearance → Layout and text → Density: Comfortable (default) or Compact — tighter rows in every list, table, tree and the sidebar. Applies immediately.
Every section starts collapsed, so the app opens clean; click a header to expand it (right-click → Expand all sections). What you expand stays expanded while the app runs. Tick "Remember which sidebar sections I expanded" (Navigation & explorers) to keep it across restarts. Order, hidden sections, density and (only with that option) expanded sections are saved in the encrypted settings. The Hosts folder tree and the Cloud providers tree also start collapsed.

## Host folders and the inventory explorer
keywords: folders, tree, group, organise, organize, drag, favourites, inventory, explorer, move host
Hosts has a folder tree on the left (All hosts, your folders, ★ Favourites). Folders nest with "/" (e.g. Clients/Acme/Prod). Drag hosts onto a folder, or right-click → Move to folder. Right-click a folder: new sub-folder, rename/move, delete (keeps the hosts), add host here, open terminals for every host and tile them, check reachability, health report, export the folder (encrypted) or import into it.
**Folder defaults:** right-click a folder → ⚙ Folder defaults… sets a user name, credential, jump host, environment, terminal profile, VPN or tmux for the hosts in it and its sub-folders (the nearest folder wins). Saving asks whether every host in it follows now, or only new hosts and those already following. A host follows a field while it keeps the folder's value: change it in the host editor and it's the host's own from then on (the editor shows "from <folder>" for what it inherits). Fields that don't fit a host are skipped: a jump host and tmux only for SSH, and a key never goes to Remote Desktop or VNC. Moving hosts into a folder offers its defaults; renaming a folder takes its defaults along. Shared library hosts are never changed.

## Command palette (Ctrl+Shift+P)
keywords: command palette, ctrl+shift+p, search everything, jump to, quick open, go to, run command, find host, find setting
Ctrl+Shift+P (also from inside a terminal) opens one search box for everything: hosts (Enter connects; Shift+Enter or right-click for Files, Manage, a new terminal, copy address), open tabs, named workspaces, snippets (sent to the active terminal), every page, every settings card, and actions such as Lock, Tile, New window, Screenshot and Save workspace. Type parts of a name, an address, a folder or a tag: every word must match. What you open most comes first (Settings → Navigation → Navigation & explorers can switch this off, which forgets the list; it is kept encrypted). The palette opens what you pick the normal way, so locks, restrictions and confirmations still apply.

## Backup, export and import (encrypted .dcxbackup)
keywords: backup, export, import, restore, transfer, another pc, migrate, dcxbackup, merge, replace, tick, choose, selective, secrets, api keys
Settings → Data & backups → Backup & restore → "⤓ Back up now…" (also Import & export → Export data…) shows everything as a **tick tree**: hosts by folder, credentials, cloud accounts one by one (Vultr, Contabo, Hetzner, Azure, Google Cloud), infrastructure providers, portals, tunnels, serial consoles, snippets, notes, incidents, trusted host keys and web certificates, app settings and saved reports. Tick what goes into the file; the line underneath counts the items and how many passwords/keys and cloud API keys you are exporting. A host's jump hosts come along automatically, and a credential you leave out is removed from the hosts that use it (the summary names them). One .dcxbackup file, protected by a password you choose (PBKDF2 600k + AES-256-GCM), read back to verify it before the dialog closes. The audit log records counts only. Hosts → right-click → Export exports just those hosts plus the credentials, jump hosts, tunnels and host keys they need.
Import / restore shows the file's contents in the same tree, all ticked: untick what you don't want, then Merge (skip, replace or keep both on conflicts, optionally into a folder) or Replace everything (you type REPLACE). App settings in a file are applied without touching admin-protected or Group Policy settings, the AI lock or this PC's cloud accounts.

## Remote Desktop (RDP) hosts
keywords: rdp, remote desktop, mstsc, windows server, keepalive, reconnect, gateway, full screen
A host can be SSH or RDP (the selector at the top of the host editor). RDP hosts open INSIDE the app in a tab, using Windows' own Remote Desktop control: pop it out to its own window or dock it back without dropping the session, ⛶ Full screen (the connection bar at the top brings you back), Ctrl+Alt+Del (or Ctrl+Alt+End inside the session), 📷 screenshot, ↻ Reconnect, and a display choice — Resize to window (the remote resolution follows the pane), Scale to window, or Fixed (the host's resolution). The password is given to the control in memory only; nothing is written to disk (no .rdp file, no bitmap cache). Device redirection follows the host's settings: clipboard on, drives/printers/microphone off by default; smart cards, ports and cameras are never redirected embedded. Windows-key combinations go to the remote computer in full screen.
Right-click → "Open in mstsc" (or Settings → Terminal & connections → untick "Open Remote Desktop inside the app") uses Windows' Remote Desktop Connection window instead; hosts with an RD Gateway always open there. In mstsc the .rdp file is generated: resolution or full screen, multi-monitor, clipboard/drive/printer/audio redirection, admin session, RD Gateway, and auto-reconnect with retries (the client-side keepalive). A vault password is handed to Windows only for that sign-in and removed again; it never goes into a file or on a command line.
**Through SSH:** host editor → Remote Desktop → CONNECT THROUGH → Through SSH (the same address, or a saved SSH host such as a bastion). Each session opens its own tunnel from 127.0.0.1 on a free port to the target (the host's address as the SSH host sees it, or the target you enter) and closes it when the tab or the mstsc window closes; nothing listens beyond this PC. Kerberos single sign-on and Remote Credential Guard need the real server name, so they don't work through the tunnel: a password from the vault does. An RD Gateway, when set, is used instead of the tunnel.
Server-side keepalive is a Windows policy on the server: right-click the host → "RDP server keepalive commands…" for the exact reg add lines. Right-click → Screenshot Remote Desktop window captures the open session.

## Moving the data store
keywords: move data, data folder, another drive, d drive, onedrive, synced folder, location, relocate, store location
Settings → Data → "Move data store…": pick an EMPTY folder. A pre-flight checklist runs first (absolute path, empty target, not nested in the current store, drive ready, free space ×3, writable, warnings for network/removable drives). The move copies every file, compares SHA-256 hashes, decrypts every file and audit line at the new place with the in-memory key, and only then switches a small pointer file in %APPDATA%. The old copy is kept until you click "Remove old copy…". "Move back to the default folder" reverses it. Uninstall with "delete my data" also removes the store files at the moved location.

## Share with Windows (Share sheet)
keywords: share, windows share, nearby sharing, teams, mail, outlook, send, send file
Share… buttons and menu items open the Windows Share sheet (Mail, Teams, Outlook, Nearby Sharing, OneDrive…): reports (HTML), terminal output, command output text, table exports (CSV), snippets (.sh), multi-host run results, the AI conversation, SFTP files (up to 200 MB in total) and screenshots. Shared files are plain copies written to a private temp folder that is emptied at start and exit.

## Screenshots of terminals, Remote Desktop and tabs
keywords: screenshot, screen shot, capture, image, png, picture, snapshot, rdp screen, terminal screen, clipboard, share image
Right-click a terminal → 📷 Screenshot, or right-click any tab → 📷 Screenshot this tab: Save to screenshots folder, Save as…, Copy image (clipboard), Share… (Windows Share sheet). Ctrl+Shift+S captures the active tab and, by default, both saves it and copies it (change in Settings → Terminal & connections).
Remote Desktop: right-click an RDP host in Hosts → 📷 Screenshot Remote Desktop window. The mstsc window must be open and not minimised; it is captured even when another window covers it (full-screen sessions give the whole remote screen).
Terminals are captured exactly as drawn (colours, cursor). To capture the text instead, use the Scrollback menu (copy / save / share output).
Screenshots are plain PNG files (default folder Pictures\DarkCipher ServerManagement) — they are not encrypted and may contain whatever was on screen, including secrets. The audit log records each one.

## Keyboard shortcuts
keywords: shortcuts, keys, hotkeys, keyboard
Outside a terminal: F1 help · Ctrl+K quick connect · Ctrl+W close tab · Ctrl+B toggle sidebar · F6 next pane.
Inside a terminal plain Ctrl+letter and F-keys go to the remote program (Ctrl+W deletes a word, Ctrl+B is the tmux prefix, F1 is htop's help), so use: Ctrl+F1 help · Ctrl+Shift+K quick connect · Ctrl+Shift+W close tab · Ctrl+Shift+B sidebar · Ctrl+F6 next pane.
Everywhere: Ctrl+Tab / Ctrl+Shift+Tab next/previous tab · Ctrl+Shift+A AI Engineer sidebar · Ctrl+Shift+L lock the app · Ctrl+Shift+S screenshot the active tab · Ctrl+Alt+→ split right · Ctrl+Alt+↓ split down · Ctrl+Alt+T tile all · Ctrl+Alt+U merge panes · Ctrl+Alt+O pop out tab · Ctrl+Shift+C / Ctrl+Shift+V copy/paste in a terminal · Ctrl+F find in output text · Enter in a disconnected terminal reconnects.

## Incidents and work sessions
keywords: incident, outage, work session, timeline, postmortem, post-incident, root cause, severity, sev1, evidence, add to incident, active incident
Sidebar → DOCUMENTS → Incidents. An incident (or any work session) has a title, status (Open, Monitoring, Resolved, Closed), severity (SEV1–SEV4), environment, linked hosts, a summary and a timeline.
The ACTIVE incident (⚑) receives captures with one click: select text in a terminal → ⚑ Add selection to incident; any 📷 Screenshot menu → ⚑ Add to incident; under an AI answer → ⚑ Add to incident (or ✎ Save as note); Reports → ⚑ Add report to incident; a note's right-click → Link to incident. With no active incident you are asked which one (or to start one). Right-click a host → ⚑ New incident for this host. While an incident is active, opening a terminal to one of its hosts is recorded on the timeline.
In the incident: ＋ Event, ＋ Log / output, ＋ Attach file (up to 20 MB), ＋ Link note, ＋ Report. Status and severity changes are recorded automatically. Double-click an entry to open it; select several (Ctrl/Shift+click) to summarise or remove just those.
✦ Summarise with AI and ✦ Root-cause hypotheses first show exactly what will be sent (secrets masked, destination shown). Screenshots and files are only named, never sent. Export Markdown writes a plain (not encrypted) file you choose.
Everything is encrypted in the store: screenshots added to an incident are NOT written as PNG files. Incidents are in snapshots, store moves and encrypted backups.

## Local API
keywords: api, local api, rest, token, script, automation, monitoring, curl, powershell, invoke-restmethod, integration
Settings → Local API & broadcast. Off by default. When on, the app listens on 127.0.0.1 only (this PC; never the network — use an SSH tunnel if another machine needs it). Programs need a token: ＋ New token… → name, scopes (status.read, hosts.read, incidents.read, incidents.write, notes.write) and an expiry. The token is shown ONCE; only a hash is kept. Revoke one or all at any time; Settings shows when each was last used.
Endpoints: GET /api/v1/status · GET /api/v1/hosts (never credentials) · GET /api/v1/incidents[?status=open] · GET /api/v1/incidents/{id} · POST /api/v1/incidents/{id|active}/events {"title","text","kind":"event|log"} · POST /api/v1/notes {"title","body","host","append"}.
Example: Invoke-RestMethod -Headers @{Authorization="Bearer dcx_…"} http://127.0.0.1:47321/api/v1/status
Browsers are refused (any Origin header), the Host header must be 127.0.0.1/localhost, bodies are limited to 64 KB, 10 requests/s per token, and 20 failed tokens in a minute lock the API for 60 s. Every write is in the Audit log. Nothing answers while the app is locked. There is no way to read credentials, keys, settings, terminals or files through it.

## Broadcast to OBS
keywords: obs, stream, streaming, broadcast, twitch, browser source, overlay, share screen, presentation
Right-click a terminal → 📡 Broadcast to OBS… (or an incident → 📡 Broadcast status card…). Choose theme (dark / light / transparent), text size, crop to the last N rows, how long it runs, and what to mask. In OBS add a Browser source with the URL shown (127.0.0.1, only on this PC).
The terminal is published as TEXT, not pixels, so masking is reliable: passwords, tokens, keys (even half a private key on screen) and long key-like strings are always masked; IP addresses, e-mail addresses and your own patterns optionally. While a password / passphrase / [sudo] / verification-code prompt is on screen the stream freezes on the last safe frame with "input hidden". Colours are not kept.
A red 📡 BROADCASTING strip on the terminal and a badge in the status bar stay visible; click either to stop. Broadcasts also stop when they expire, when the tab closes, when the app locks or exits. An incident status card shows title, status, severity, duration and the latest entry titles — never their contents.

## Add-ons
keywords: add-on, addon, plugin, extension, extend, script, sdk, dcxaddon, sandbox, permissions
Sidebar → SYSTEM → Add-ons → ＋ Install add-on… and pick a .dcxaddon file. Before anything is installed you see the name, publisher (as the package states it), version, the package's SHA-256, whether its program is signed, its commands, and every permission it asks for in plain words with a risk level. Only what you tick is granted; high-risk ones (credentials.request) start unticked.
An add-on is a program, run only when you click one of its commands (Add-ons screen, a host's right-click → ◇ Add-ons, or a terminal's right-click → ◇ Add-ons). It runs sandboxed: Low integrity (it can't change your files, the app or other add-ons), memory and process limits, killed at its timeout, and a clean environment. It can still read many files and use the network on its own — the permissions control what THIS app gives it — so install only add-ons you trust.
Permissions: hosts.read (never passwords/keys), incidents.read/write, notes.read/write, terminal.read (the screen of the terminal you ran it from, secrets masked), filesystem.read (only a file you pick each time), credentials.request (a dialog each time; you pick one credential or deny).

Full control (0.1.18): an add-on can list everything the app can do (app.capabilities) and call any of it (app.call), including server status, logs, services, files, SSH commands, transfers, cloud and infrastructure actions, terminal sessions, notes, incidents, portals and libraries. It can also add, change or remove saved hosts, and open screens and hosts. The permissions:
- app.read: reads.
- app.private: notes, incidents and documents; asks each time.
- app.control (HIGH): any change. You approve the exact action each time, and PRODUCTION asks for its name.
- hosts.write: you approve the exact fields of each change.
- ui.control: open screens and hosts.
Every call is in the audit log as addon:<id>.
Untick a permission to revoke it (next call fails), Disable/Enable, Remove. Updates keep what you granted; any NEW permission stays off until you approve it; older versions are refused. Every file is checked before each run — a modified add-on is disabled. The run log shows each run's result; everything is in the Audit log. Writing your own: docs/ADDONS.md and samples/addons in the source.
Encryption (0.1.17):
- **Messages:** everything the app and an add-on send each other is encrypted (AES-256-GCM, with new keys for every run). Add-ons made for the old unencrypted bridge (API 1) are refused, and blocked if already installed; ask their author for an updated version. Since 0.1.18 every add-on must be built for API 2 to install, themes and other data-only add-ons too; data-only ones installed earlier keep working.
- **The running add-on:** no other program, including other add-ons, can open it to read its memory or take over its connection.
- **Files on disk:** themes, the portal browser and shared library add-ons are stored encrypted on disk, and are checked every time they're read.

## Theme add-ons (sample theme, your organisation's theme)
keywords: theme, colours, colors, skin, look, branding, rebrand, logo, product name, sample theme, ocean, palette, accent, dark mode, light mode, default theme, font, roboto, theme add-on export
A theme add-on replaces the app's colours, and can replace the header logo and product name and the UI font, without running anything: it is a .dcxaddon with a theme file and no program, so it asks for no permissions and gets no access to your data.
- **Install:** Sidebar → SYSTEM → Add-ons → ＋ Install add-on… → pick the theme's .dcxaddon (for example com.darkcipher.theme.sample-2.0.0.dcxaddon). The approval screen says "This is a theme". Install applies it at once. A newer version installs over an older one with Update from file….
- **The sample theme (Ocean):** the header and window title read "Ocean ServerManagement" with a sample logo. Light mode is a white header and pages with grey panels and blue accents; dark mode is a deep navy with the logo on a white pill. Copy `samples/addons/sample-theme`, change the values and pack it — or build yours in Settings → Appearance → Branding & colours and press **Export as theme add-on**.
- **Enabled = in use:** while a theme add-on is enabled it overrides the app's own light and dark themes; the header's selector (System / Dark / Light) switches between the theme's light and dark colours.
- **Font:** the sample theme names Roboto. It is used only if Roboto is installed on this PC (Windows Settings → Fonts) and you haven't chosen your own UI font in Settings → Typography; otherwise the app's own font stays.
- **Back to the default:** Add-ons → select the theme → Back to the default theme, Disable or Remove. The DarkCipher palette, logo and name come back. Use this theme (or Enable) turns it on again; only one theme is in use at a time.
- **What a theme can't change:** environment label colours (PRODUCTION fuchsia, TEST sky blue, DEVELOPMENT violet, LAB slate — never the warning or error colours — stay the same under every theme), terminal colours (Settings → Terminal profiles), remote desktops, dialog titles, the installer, the Start menu entry and the taskbar icon.
- **Safety:** the theme file may contain only #RRGGBB colours for known keys, a font name, plain-text names and a PNG logo of at most 1 MB; anything else is refused when the package is inspected. Its files are checked every time it is applied: a changed theme is not applied, the add-on is disabled and the default comes back.
Writing your own theme: docs/ADDONS.md → Theme add-ons, and samples/addons/sample-theme in the source.

## Microsoft 365 admin (Intune, Defender, Entra)
keywords: microsoft 365, m365, intune, endpoint manager, defender, security, incidents, alerts, entra, azure ad, users, groups, sign-ins, risky users, compliance, admin centre, admin center
SERVERS → Microsoft 365 admin shows, read-only and signed in as you: **Intune** managed devices (compliance, OS, user, last check-in, model, encryption); **Defender** incidents and alerts (severity, status, classification); **Entra** users, groups, devices, sign-ins (Entra ID P1) and risky users (Entra ID P2). Pick a list, ↻ Load (the first time per service, the browser asks you to sign in), type in the filter, and double-click an item (or Open in portal) to open it in the real admin centre in a tab. Portals ▾ opens the Intune admin centre, the Defender portal and the Entra admin centre; Copy the list copies what is shown.
Set up once (Settings → Integrations → Microsoft 365 admin): your tenant and an app registration (a public client with the redirect http://127.0.0.1) with the read-only delegated Microsoft Graph permissions listed there; an administrator grants consent, and changing it in the app needs admin approval. Your account also needs a role that can read each list. Nothing read is stored; each list you load is in the audit log. Actions (sync, wipe, resolve incidents, disable users) come in a later release.

## Cloud PCs (Windows 365 and Azure Virtual Desktop)
keywords: cloud pc, cloud pcs, windows 365, w365, azure virtual desktop, avd, windows app, remote desktop, virtual desktop, connect cloud pc
SERVERS → Cloud PCs. Microsoft's Windows App can't be embedded in another program, so the page works around it:
- **▶ Open the Windows App** starts the installed Windows App (the full experience: multi-monitor, devices). If it isn't installed, the Microsoft Store is offered.
- **🌐 Windows App in a tab** opens Microsoft's Windows App web client in a tab here: every Cloud PC and desktop assigned to you. Sign in once; it keeps its own browser profile ("Cloud PCs"), with no saved passwords or autofill. While the app's browser is off (Settings → Integrations → Portal browser), it opens in your default browser.
- **Your Cloud PCs listed here (optional):** in Settings → Integrations → Cloud PCs, add your Entra tenant and an app registration (a public client with the redirect http://127.0.0.1 and the delegated Microsoft Graph permission CloudPC.Read.All; an administrator grants consent; changing it needs admin approval). Then ↻ List my Cloud PCs signs you in through the browser (once) and shows each Cloud PC with its plan and image; double-click or Connect in a tab opens it straight away, or open it in the Windows App. Sign out forgets the Microsoft Graph sign-in.
- **Actions (optional):** with "Allow restart and rename…" switched on (Settings → Integrations → Cloud PCs, admin approval) each listed Cloud PC has ⟳ Restart and ✎ Rename…, and Admin ▾ for Windows 365 / Intune administrators: Troubleshoot (Microsoft's health check), and for Windows 365 Frontline Cloud PCs Power on / Power off (power off deallocates it and revokes its licence at once, so you type its name to confirm). The app registration needs CloudPC.ReadWrite.All (admin consent); it is asked for in a separate sign-in the first time you use an action. Every action asks first and goes into the audit log.
Nothing is contacted until you click. The page can be hidden (Settings → Integrations → Cloud PCs, Group Policy CloudPcEnabled).

## Portal browser (web UIs in tabs)
keywords: browser, portal, web ui, webui, web interface, router, nas, synology, proxmox, unraid, idrac, ilo, unifi, pfsense, opnsense, bookmark, open web ui, self-signed, certificate, pin, https, download, pop-up, popup, cookies, profile, sign in, login
The portal browser opens web admin pages — routers, switches, NAS, the Proxmox / Unraid UI, out-of-band controllers, provider portals — as tabs inside the app. Since 0.1.17 it is part of the app: Portals is in the sidebar under SERVERS, and Settings → Integrations → Portal browser switches it off and on. No add-on is needed; the portal browser add-on (com.darkcipher.portal-browser) still works if you have it, and its starter bookmarks are still offered.
- **Bookmarks:** Portals → ＋ Add portal… (title, address, folder, environment, optional named profile and sign-in credential). For a host's own web UI: right-click the host → 🌐 Open web UI → ＋ Add web UI… (address pre-filled with https://<host>/); the host's web UIs are listed in that menu afterwards. Bookmarks live in your encrypted store like every host.
- **Tabs or windows:** Portals → Open portals in: a tab in the main window (default) or a separate window. Any portal tab can pop out or dock back (tab menu, Ctrl+Alt+O). Pop-ups a page opens (sign-in windows, consoles) become tabs next to it, in the same profile; when the page closes its pop-up, the tab closes.
- **Profiles (cookies):** each environment has its own browser profile — PRODUCTION, TEST, LAB, DEVELOPMENT, LOCAL and "general" (no environment) — so signing in to a production portal never shares cookies with the lab. A portal takes its own environment, else its host's. A named profile (for example "home lab") gives a group of portals their own cookie jar. The profile shows in the tab's toolbar; ⋯ → Clear cookies and site data signs every portal in that profile out.
- **The address bar:** the chip on the left says 🔒 host (HTTPS that Windows trusts), 🔒 host · pinned (a self-signed certificate you trusted), ⚠ Not secure (plain HTTP: passwords cross the network unencrypted), ⛔ Untrusted certificate, or ⛔ CERTIFICATE CHANGED. Query values that look like secrets (token, ticket, password, vncticket…) are shown as "…", so screen shares and screenshots don't leak console tokens. Type an address and press Enter to go there (Ctrl+L focuses it; Esc restores it).
- **Self-signed certificates:** a certificate Windows doesn't trust is refused, and the banner shows its SHA-256. Compare it with the device (its certificate page, or `openssl x509 -noout -fingerprint -sha256 -in cert.pem`), then click **Trust this certificate…**: it is pinned for that host:port only (trust on first use, like Infrastructure providers). Afterwards any other untrusted certificate on that host:port is refused as CERTIFICATE CHANGED — possible interception; replacing the pin asks you to type the host:port. Portals → Trusted certificates lists the pins; Forget makes the next visit ask again.
- **Downloads:** every download asks first, showing the file name, size, type and the folder; files that can run code (.exe, .msi, .ps1, .iso…) carry a warning. They go to the folder chosen on the Portals screen (asked at the first download). Names are cleaned and never overwrite an existing file.
- **Passwords:** the browser never saves passwords or form data. To sign in, set a vault password on the portal (or use its host's) and click **⚿ Fill sign-in**: it fills the page's user name and password fields only on the portal's own address (scheme, host and port), never over a refused certificate, and asks first on plain HTTP. Every fill is in the Audit log.
- **What it won't open:** only http(s) addresses. file: links (this PC's disks), and links that would start another program (ms-settings:, mailto:, custom handlers), are refused.
- **Privacy:** no telemetry; SmartScreen look-ups (which send addresses to Microsoft) are off. Offline mode applies to every page and every request a page makes. Cookies and site storage are kept by the WebView2 runtime in the app's local data folder, protected by Windows (DPAPI) — not by the app's own encrypted store; the Privacy & network screen says so. The pages you open can of course load whatever they load.
- **Turning it off:** Add-ons → Portal browser → Disable or Remove. Portals and Open web UI disappear; your bookmarks and pins stay in the store.

## Snapshots and recovery
keywords: snapshot, backup, restore, rollback, undo, damaged, corrupt, recovery, migration, data format, upgrade data, restore drill
Settings → Snapshots keeps rolling encrypted copies of your whole store: automatically (daily by default, newest 10 kept) and before every restore, import, data upgrade and app update. Everything stays on this PC. A snapshot only counts once it was read back and every file checked; old ones are removed only after a newer one verified. If the disk is low, nothing is written or removed and you are told.
Snapshots use the store's own key, so they are as private as the store and only work with it. For another PC, a new Windows account or a forgotten password, keep a password-protected export too (Settings → Security → Encrypted backup).
Verify (restore drill) decrypts and checks every file without changing anything. Restore shows what will be replaced, added back and removed, takes a snapshot of your current data first (so a restore can be undone), then restarts the app. Your master password / 2FA and the audit log are never rolled back.
At start the app finishes any restore or upgrade a crash interrupted, and checks every data file. A damaged file is offered back from the newest snapshot with a good copy; the damaged file is kept next to it (".damaged-…"), never deleted. Data written by a NEWER app version is not opened (no downgrades). Format upgrades run only after a verified snapshot, are checked afterwards, and are put back automatically if anything is wrong.
Snapshots live in "snapshots" inside the store folder by default and move with it; "Choose folder…" puts them on another drive.

## Install, update and uninstall
keywords: install, msi, update, upgrade, uninstall, remove, data, where is my data
Install with the MSI (per-machine). Each build has its own version number (0.1.1, 0.1.2, …), shown at the top left of the window and in Settings → Updates. Each version ships with release notes.
- **Upgrading:** install the newer MSI over the top. It upgrades in place and keeps your data. In-app updates are turned off until signed releases exist.
- **Going back:** older installers are kept, so you can return to an earlier version by uninstalling (keep your data) and installing the older MSI. If the newer version changed the data format, the older one says so when it starts. In that case, restore a snapshot taken before the upgrade (Settings → Snapshots). Uninstall asks whether to also delete your encrypted data (%APPDATA%\DarkCipher Systems\ServerManagement) and the cache (%LOCALAPPDATA%\DarkCipher Systems\ServerManagement). Settings → Data shows both folders. (Until 0.3.1 both sat under a `DarkCipher` folder; 0.4.0 moves them to `DarkCipher Systems` on its first start, files untouched.)

## Troubleshooting connections
keywords: troubleshoot, cannot connect, timeout, permission denied, auth failed, refused, host key, sudo password
- Timeout: check the address/port, firewalls and "◉ Check reachability"; raise the connect timeout in Settings.
- Permission denied: check the user and credential; for keys, make sure the public key is in authorized_keys (Hosts → Install key…).
- Host key changed: verify with the provider console before removing the old key in Known hosts.
- sudo asks for a password: give the host a password credential or a sudo password in its settings.
- AI "not reachable": start Ollama (ollama serve), check Settings → AI engine, or switch to CPU-only / Off on a laptop.

## Backup & restore (Settings → Data & backups)
keywords: backup, back up, restore, import, export, scheduled backup, backup folder, backup password, tick, pick what, dcxbackup, data and backups, nas, policy
Settings → Data & backups → **Backup & restore** is the first card. "⤓ Back up now…" shows everything as a tick tree (hosts by folder, credentials, cloud API keys, notes, incidents, reports, settings…) and writes one password-protected .dcxbackup (PBKDF2-SHA256 600,000 rounds + AES-256-GCM), read back before it counts. "⤒ Restore or import…" opens a .dcxbackup, shows its contents as ticks, then merges (keep mine / take theirs / keep both) or replaces everything; a snapshot is taken first.
Scheduled backups: choose how often (daily to monthly), a folder (another disk or a NAS share is best; a OneDrive-style folder is allowed after a warning, the files are encrypted), how many to keep, and set a backup password (kept only in the encrypted store — you need it to restore on another PC). "Run a scheduled backup now" tests it. Snapshots below stay as the automatic local safety net.
Rules an administrator can set (admin-protected and Group Policy): allow exports at all, allow imports, allow credentials / keys / cloud API keys in exports (off: they are stripped from every file, including the app's own tokens), whether scheduled backups include secrets, and the shortest file password (12 by default).

## Import connections from another app (mRemoteNG, SecureCRT, Royal TS…)
keywords: import, migrate, mremoteng, confcons, securecrt, royal ts, royalts, rtsz, mobaxterm, mxtsessions, putty, kitty, winscp, rdcman, rdg, rdp file, devolutions, remote desktop manager, master password
Settings → Data & backups → "⇩ Import from another app…" (also Hosts → ⋯ → Import from another app). Pick the app, then its file (or "Read from this PC" for PuTTY, KiTTY and WinSCP), and you see every connection as a tick under its folder. Choose the folder they go into and whether saved passwords come along; a snapshot is taken first, hosts already here are skipped.
- mRemoteNG: confCons.xml; passwords are decrypted (the default key, or the master password you set in mRemoteNG — asked for, never stored); whole-file encryption and pre-1.76 files work; inherited users and passwords are resolved.
- SecureCRT (XML export or session .ini), Royal TS (.rtsz document or a CSV export — passwords only from a CSV that includes them), MobaXterm (.mxtsessions), PuTTY / KiTTY, WinSCP (registry or WinSCP.ini), RDCMan (.rdg), .rdp files (several at once), Devolutions RDM (.rdm export).
- Passwords those apps encrypt with their own keys (SecureCRT, Royal TS documents, RDCMan, WinSCP, MobaXterm) aren't guessed: you're told which hosts had one. Key files (.ppk / OpenSSH) are read into the vault when they exist.
Every file is size-capped, XML is read with DTDs refused (no XXE), host names, ports and folders are checked. Imported passwords go straight into the encrypted vault; consider deleting or protecting the source file afterwards.

## Identity & directory (Settings tab)
keywords: identity, directory, active directory, ad, ldaps, entra, azure ad, kerberos, windows account, domain, hybrid joined, prt, profile, bind, sign in, sso
Settings → **Identity & directory** shows who you are signed in to Windows as (DOMAIN\name, UPN, SID), how the PC is joined (Active Directory, Microsoft Entra ID, hybrid, the Entra SSO token) and your Kerberos tickets (realm and renewal time only). "Bind a profile to this account" creates a profile that opens with your Windows sign-in. **Directory connections** lists every profile that signs in with the organisation (Windows session / Kerberos, LDAP over TLS, Microsoft Entra ID): View configuration, Edit (asks you to confirm with Hello or your password first), and **Test sign-in**, which signs in for real and shows the role and groups. Profiles moved to this tab.

## Automatic sign-in with your Windows account and Windows Hello
keywords: automatic sign in, auto login, windows hello, domain account, profile picker, single sign-on, kerberos, user aware
When a profile is bound to the account you're signed in to Windows with, the app opens that profile without the picker, signs in with your Windows session (Kerberos — nothing typed) and Windows Hello opens its encrypted store (face, fingerprint or PIN; the master password stays as the fallback). Switch it in Settings → Identity & directory → Automatic sign-in; the computer policy AutoProfileForWindowsAccount can force it on or off. Two profiles bound to the same account show the picker.

## Single sign-on to SSH (Kerberos) and Remote Desktop
keywords: sso, single sign-on, kerberos, gssapi, ssh kerberos, rdp single sign-on, remote credential guard, restricted admin, credssp, delegate, ticket
SSH: host editor → "Single sign-on with my Windows account (Kerberos)". The host opens in a tab running Windows' OpenSSH with GSSAPI — no password or key; the server must accept it (domain-joined Linux: sssd / realmd with a host keytab). Forwarding your ticket to the server stays off unless an administrator allows it.
Remote Desktop: host editor → SIGN IN: "Single sign-on with my Windows account" (CredSSP with your Windows credentials; needs the "Allow delegating default credentials" policy for TERMSRV/<server>, otherwise Windows asks once), "Remote Credential Guard" (Kerberos, nothing left on the server) or "Restricted Admin" (network logon; the account must be an admin there).
An administrator can switch each of these off (Settings → Identity & directory → Single sign-on to sessions; Group Policy).

## Automation: Ansible and Puppet
keywords: automation, puppet, ansible, configuration management, sidebar, section
The sidebar's **AUTOMATION** section holds Ansible and Puppet. Puppet is read-only until an administrator allows changes (Settings → Integrations → Automation).

## Puppet (certificates, agents, code, PuppetDB, Bolt)
keywords: puppet, puppetserver, puppet agent, noop, dry run, certificate, ca, sign, revoke, clean, r10k, code manager, deploy, hiera, lookup, puppetdb, pql, facts, reports, bolt, task, plan, lint, validate
AUTOMATION → Puppet. Pick your Puppet server and PuppetDB host from the saved hosts.
- Certificates: list (requested first, with SHA-256 fingerprints), sign, revoke, clean — compare the fingerprint with the node's own first.
- Agents: select hosts; "Run Puppet" is a dry run (--noop) unless you untick it; Last run (changed / failed / out of sync, disabled reason), Facts, Config, Disable with a reason, Enable. Exit codes are explained (2 = changes applied, 4 = failures).
- Code & Hiera: environments, Deploy (Code Manager on Puppet Enterprise, else r10k; production asks you to type it), Hiera lookup with --explain, parser validate, puppet-lint.
- PuppetDB: nodes, failed last run, a node's facts / reports / latest events — queried on the PuppetDB host with its own certificate, so no Puppet key is copied to this PC.
- Bolt: command / task / plan on the selected hosts from a control node (WSL or a Linux host).
Sudo passwords come from the vault on stdin; every value is checked before it reaches a command; changes ask first (type PRODUCTION for production hosts) and go to the audit log.

## WSL: distributions, config files and WSL Containers
keywords: wsl, windows subsystem for linux, ubuntu, distribution, distro, wsl.conf, wslconfig, .wslconfig, mirrored networking, systemd, export, import, unregister, wslc, containers, images
LOCAL → WSL (shown when WSL is installed). Distributions with state, version, default and disk size: open a terminal or the files (File commander on \\wsl.localhost\name), open in your IDE, Start, Terminate, Set default, Convert v1↔v2, Export (.tar or .vhdx — a plain copy, keep it safe), Import, Unregister (type the name), Shut down WSL, Update, Install from the online list.
Config & network files: /etc/wsl.conf, /etc/hosts and resolv.conf of a distribution, edited as root with the old one kept as .dcx-bak; the distribution's addresses and listening ports. .wslconfig: memory, processors, swap, networking mode (NAT / mirrored), DNS tunneling, firewall, proxy, localhost forwarding, GUI apps, nested virtualisation — every other line is kept and a dated copy is made; shut WSL down to apply.
Containers (WSL Containers, wslc — WSL 2.9.3 or later): list containers / images / volumes / networks, start, stop, remove, logs, inspect, a shell in a tab, pull an image.

## Open in VS Code (and other IDEs)
keywords: vscode, visual studio code, cursor, vscodium, windsurf, jetbrains gateway, ide, remote-ssh, remote ssh, dcx_config, ssh-agent, open folder
Hosts → right-click → "⧉ Open in Visual Studio Code" (or Cursor, VSCodium, Windsurf, JetBrains Gateway — whichever is installed; choose in Settings → Integrations → IDE integration). File commander's menu opens a folder (local, or on the host through Remote-SSH); LOCAL → WSL opens a distribution. Remote-SSH reads ~/.ssh/config: the app keeps its own ~/.ssh/dcx_config (address, user, port — no password or key) and asks once before adding "Include dcx_config" to ~/.ssh/config. The host's vault key goes into the Windows ssh-agent for a while (60 minutes by default) and never onto disk; the agent service must be running. "Remove dcx_config and the Include line" undoes it.

## Share to Microsoft Teams
keywords: teams, microsoft teams, share, screenshot, channel, chat, webhook, workflows, graph
Not to be confused with the sidebar's TEAM section (the app's own shared libraries). 📷 → "Share to Microsoft Teams…", a terminal's right-click → "Share selection to Microsoft Teams…", Incidents → "Share to Teams…". You see exactly what is posted (secrets already masked; edit anything else out). Channels and chats go through Microsoft Graph signed in as you (screenshots included, up to about 4 MB); a Teams Workflows webhook carries text only. Set up in Settings → Integrations → Microsoft Teams: switching sharing on needs administrator approval; an app registration (public client, delegated ChannelMessage.Send, ChatMessage.Send, Team.ReadBasic.All, Channel.ReadBasic.All, Chat.ReadBasic) and your tenant; then add channels, chats or webhooks.

## Pingdom checks on the dashboard
keywords: pingdom, uptime, monitoring, pinned checks, dashboard, api token
Settings → Integrations → Pingdom: paste a read-only API token (Pingdom → Settings → Pingdom API), Load checks, select and "Pin selected to the dashboard". Pinned Pingdom checks show Pingdom's state (up / down; paused isn't judged) and response time beside the app's own checks, refreshed on the dashboard's schedule. Only api.pingdom.com is contacted, read-only.

## BeyondTrust (Privileged Remote Access and Password Safe)
keywords: beyondtrust, pra, privileged remote access, remote support, bomgar, jump item, jump group, jumpoint, access console, vault, password safe, beyondinsight, check out, checkout, check in, managed account
Settings → Integrations → **BeyondTrust** page (switching it on needs admin approval; every setting can be set by Group Policy, secrets stay encrypted here): Connection and Password Safe (each with Test), Checkout policy (ask for a reason), Sessions (access console or web console; Jump Group and Jumpoint filters), TLS (a self-signed appliance certificate is refused until you trust its fingerprint), Scopes and Audit. SERVERS → BeyondTrust:
- Jump Items (shell jump, RDP, VNC, tunnels, web, Jump Clients) with their Jump Groups: open one in your access console (double-click), or add shell-jump / RDP items as saved hosts. Sessions: the access-session report for the last days. Jumpoints and Vault account names (never vault secrets). Read-only.
- Password Safe: list the managed accounts you may request. "Check out & connect" opens a saved host once with that account; "Link to a saved host" makes every connection to that host check the password out. The password goes straight into the SSH / Remote Desktop session in memory — never saved, logged or shown — and is checked back in when the tab closes, when the checkout runs out, when you press Check in, or when the app exits. (A host that opens in Remote Desktop Connection instead, such as an RD Gateway host, gets it through the Windows credential store for its sign-in only, removed after about 25 seconds, and the checkout goes back two minutes later.)

## Bitwarden: sign hosts in from your vault
keywords: bitwarden, vaultwarden, password manager, vault, master password, api key, secrets manager, bws, bw, machine account, access token, ssh key, copy password
Settings → Integrations → **Bitwarden** page (switching it on needs admin approval; every setting can be set by Group Policy). Install the vault CLI with `winget install Bitwarden.CLI` (and `bws.exe` for Secrets Manager); the CLI card shows whether each is signed by Bitwarden. Choose the server (bitwarden.com, bitwarden.eu or your own), enter your personal API key (web vault → Settings → Security → Keys → View API key) and, for Secrets Manager, a machine account's access token. Both are stored encrypted; your master password is asked when the vault is locked, and kept (encrypted) only if you tick Remember on this PC.
- Not working yet? VAULT → **Bitwarden** starts with a set-up checklist: "⬇ Install Bitwarden CLI" installs Bitwarden's command-line tool (bw.exe) for you with winget (the Bitwarden desktop app doesn't include it); then add your personal API key; then 🔓 Unlock.
- VAULT → **Bitwarden**: 🔓 Unlock, then search the vault. Lists show names, user names and addresses only. "Link to a saved host" makes every connection to that host read the item's password or SSH key into memory (never saved); "Connect a host with it" does it once. A host's right-click menu → 🔑 Sign in with Bitwarden… opens the screen ready to link that host.
- **Secrets Manager** tab: projects and secrets (keys and notes, never values); link a secret to a host (service accounts) or copy it.
- **Copy password / key / value**: cleared from the clipboard after 30 s (Settings → Bitwarden → Use) and kept out of Windows clipboard history and cloud sync.
- The vault locks after 30 minutes unused, when the app locks and when it closes. Offline mode allows an unlocked vault's cached items, but not signing in, syncing or Secrets Manager. Every unlock, use and copy is in the audit log; secrets never are.

## Bitwarden and Password Safe credentials everywhere
keywords: vault credential, external credential, bitwarden credential, password safe credential, add as a vault credential, infrastructure credential, portal sign-in, sudo, vpn key, api key from bitwarden, ⛓, find credential, search bitwarden, credential picker
**🔍 Find…** beside every credential box (a host's sign-in and sudo password, an infrastructure provider, a portal's sign-in, a VPN) searches this vault, your Bitwarden vault, Secrets Manager and Password Safe in one window. Type any part of a name, user name or address (several words in any order); the list filters as you type, even across thousands of items, and Enter or a double-click uses the selected one. Choose a source at the top, or All sources; ↻ reads a source again (Bitwarden syncs with its server first). Bitwarden's list is read once each time the vault unlocks, in the background, and then searched in memory, so searches are instant; other lists are kept for 5 minutes. Names only, and forgotten when the vault or the app locks. In a host, picking one also fills in the item's user name, and replaces a direct Bitwarden / Password Safe link if the host had one.
VAULT → Credentials → **⛓ From Bitwarden / Password Safe…**, or VAULT → **Bitwarden** / **BeyondTrust** → **＋ Add as a vault credential**, add one without editing anything. Either way it is listed in VAULT → Credentials, marked ⛓ with where it lives, and picking it again reuses it. Nothing secret is copied: the credential keeps the item's id and a label.
- Pick it anywhere a credential goes: a host's sign-in (also hosts from your cloud providers), its sudo password, an infrastructure provider (Proxmox, vCenter, Hyper-V, UniFi, Unraid, Synology, Satellite, the DCX portal), a portal's sign-in, a VPN key, the quick-connect default key, or an add-on's credential request you approve.
- Each use reads the password or key into memory only. Bitwarden asks for your master password when the vault is locked; tick **Remember on this PC** and it won't ask again (see "Remember the Bitwarden master password"). Password Safe checks the account out once and every use shares that checkout until it runs out; then it is checked back in (also when the app locks or closes, or from the Password Safe screen).
- What was read is reused for up to two minutes, so a busy screen doesn't start Bitwarden again for every request, and forgotten when the vault or the app locks.
- **Dashboard and scheduled checks never ask.** If the vault is locked, or no Password Safe checkout is open, those checks fail with that reason until you use the credential once (or press Check now, which may ask).
- A host linked straight to a Bitwarden item or a Password Safe account (right-click → 🔑 Sign in with Bitwarden…, or Link to a saved host) uses it for Files, Manage, tunnels, sudo and checks too, not just its terminal tabs.
- Double-click such a credential to rename it; its password or key is changed in Bitwarden or Password Safe. A key kept in Bitwarden isn't exported from here: export it from Bitwarden.
- Shared libraries never carry these: publishing a host leaves them (and its direct links) behind, and nothing from a library can point at your vault.

## Remember the Bitwarden master password
keywords: remember master password, save master password, bitwarden unlock, auto unlock, unlock at start, forget master password, bitwarden slow
When Bitwarden asks for your master password, tick **Remember on this PC**. It is kept encrypted with the app's other secrets (never in the registry or a backup), and from then on Bitwarden unlocks without asking: when you use a vault credential, search, and in the background when the app starts or you unlock the app, so the first search is instant. It is tied to your Bitwarden server. If you change your master password, Bitwarden refuses the old one: it is forgotten and you are asked again. Settings → Integrations → Bitwarden → Use shows whether it is remembered and has **Forget the remembered master password**; signing out or changing the server forgets it too. Anyone who can open the app can then use your vault without typing the password, so keep the app's own lock on (Settings → Security). Your organisation can turn remembering off (Group Policy BitwardenAllowRemember).

## Fill sign-in, copy and TOTP codes in the portal browser
keywords: fill sign-in, autofill, login page, two step login, synology login, dsm, copy password, copy username, totp, 2fa, mfa, one-time code, authenticator, verification code
**⚿ Fill sign-in** fills the page's sign-in form from the credential set on the portal (or its host), on that portal's own address only. It finds forms inside page components and frames from the same site too. A sign-in in steps (Synology DSM, Microsoft, Google) gets the user name first; press Next on the page and the password goes in when its box appears; if the credential has a TOTP secret, the code goes in when the page asks for it (for 90 seconds, on the same site only).
When a page can't be filled (no form, or a sign-in frame from another site), the banner offers **⧉ User name**, **⧉ Password** and **⧉ TOTP code**; **⧉ Copy ▾** beside Fill sign-in offers them at any time (in a narrow tab both buttons shrink to their icons, ⚿ and ⧉▾). Copied passwords and codes are kept out of clipboard history and cleared after the seconds set in Settings → Integrations → Bitwarden. TOTP codes come from the credential's **TOTP secret** (VAULT → Credentials → edit: paste the Base32 secret under the site's QR code, or its otpauth:// link) or from the Bitwarden item's own authenticator key.

## Settings locked by your organisation (🔒) or needing an administrator (🛡)
keywords: locked, greyed out, disabled setting, group policy, gpo, managed, admin approval, uac, shield
A setting set by Group Policy is greyed out with "🔒 Set by your organisation" on its card and in its tooltip (who set it and the value). A 🛡 setting can be changed only with admin approval (UAC): the card lists them under "🛡 Needs admin approval", and its button reads "Apply (admin approval)…". Every setting in the app can be set by Group Policy (templates in each release's policy folder; docs/POLICIES.md); computer-only policies include RequireDirectorySignIn and AutoProfileForWindowsAccount.

## Drag and drop (hosts, folders, dashboard cards, sidebar sections)
keywords: drag, drop, move, reorder, folder, dashboard, unlock layout, sidebar sections
Hosts: drag hosts from the list onto a folder in the tree, or drag a whole folder onto another (it moves with its sub-folders; a closed folder opens when you hover over it). Dashboard: Customise ▾ → Unlock layout, then drag a card onto another — a picture of the card follows the cursor, the target is outlined and the cards slide into place. Sidebar section headers can be dragged too. Animation follows Settings → Appearance → Animation.

## Remote Desktop: disconnected tab
keywords: rdp disconnected, white screen, reconnect, rdp tab
When a Remote Desktop connection ends, the tab shows why in the app's colours with ↻ Reconnect and "Open in mstsc" (the Windows control would otherwise stay white).

## VNC: Mac and Linux desktops
keywords: vnc, mac, macos, screen sharing, apple remote desktop, ard, remote login, linux desktop, tigervnc, x11vnc, vencrypt, rfb, 5900, desktop
Add a host of type **VNC (Mac / Linux desktop)**, or right-click an SSH host → More tools → **Add its desktop (VNC)…**. A Mac: turn on Screen Sharing and Remote Login (System Settings → General → Sharing), choose "Mac" and give the host its macOS user name and password — it signs in with Apple Remote Desktop. Linux: the VNC password (TigerVNC, x11vnc, GNOME / KDE remote desktop). By default the session goes **through SSH** to the machine (127.0.0.1:5900 on it), so it is encrypted whatever the VNC server offers; "Direct" connects to the VNC port and prefers VeNCrypt TLS (the certificate is pinned the first time you trust it). In the tab: Scale, View only, Keys ▾ (Ctrl+Alt+Del, ⌘ / the Windows key, ⌘ Space, ⌘ Tab), 📋 Send clipboard, 📷 screenshot, ↻ Reconnect. The editor's Test sign-in runs the real handshake. Dragging and resizing windows on the remote desktop stays inside the session (0.6.25): only the tab's header drags the tab itself.

## Find hosts on the network (host import)
keywords: find hosts, discover, network scan, import hosts, scan subnet, lan scan, ssh scan, rdp scan, vnc scan, identify, ai names
Hosts → ⋯ → **Find hosts on the network…**: scans your subnet (private addresses only) for SSH (22, 2222), Remote Desktop (3389) and VNC (5900–5903), reads what answers (SSH banners, Apple's Screen Sharing) and offers each machine as hosts. Edit names, folders, users and platforms in the grid, untick what you don't want, then **Import ticked**. With AI on, **✦ Identify with AI** suggests names, platforms and folders from the names, ports and banners only. Addresses you already have are shown but not ticked. Your organisation can turn scanning off (Group Policy AllowNetworkScan).

## Locks: lock a category or a single item
keywords: lock, padlock, unlock, uac, password lock, lock credentials, lock host, lock folder, lock note, lock everything, audit log locked, lock category
Right-click a host, folder, credential, infrastructure provider, cloud account, portal or note → 🔒 Lock, or a sidebar section's screen → 🔒 Lock, or Settings → Security & privacy → **Locks & restrictions**. A lock opens with Windows admin approval (UAC), Windows sign-in (Hello or your password) or a password of its own; it stays open for 15 minutes (Settings) and closes when the app or Windows locks, the PC sleeps, or with **Lock everything now**. A locked thing shows 🔒 and opens nothing until unlocked: its connections, tunnels and AI tools are refused, its details and search hits are hidden, the dashboard shows only its name. Add-ons and the local API never get a locked item. The **audit log is locked by default** (LOGS → Audit log asks first). A lock set by your organisation (Group Policy LockedCategories) can be opened but not removed.

## Turned off by your organisation (adding hosts, credentials, scanning…)
keywords: not allowed, turned off, policy, allow host add, allowhostadd, restrictions, can't add host, can't delete, integrations greyed
Group Policy can turn off adding, changing or deleting hosts, folders, credentials, cloud accounts, infrastructure providers and portals, adding tunnels, snippets and shared libraries, changing integrations, installing add-ons and scanning the network. The screen then says why ("… is turned off by Group Policy (AllowHostAdd)"); Settings → Security & privacy → Locks & restrictions lists everything that is off.

## Add-ons: installing needs admin approval
keywords: add-on install uac, addon approval, blocked add-on, allowed add-ons, signed add-on, addonsneedapproval
Installing, updating, enabling, granting a permission to and removing an add-on ask for admin approval (UAC) while AddonsNeedApproval is on (the default). Disabling and revoking never ask. Your organisation can block add-on ids, allow only listed ones, or allow only add-ons whose program is signed; a blocked add-on isn't run (it stays installed and comes back when the policy changes).

## Dashboard: edit, add cards, reset to default
keywords: edit dashboard, add card, dashboard card, host card, folder card, grafana card, sentinel card, pingdom card, beyondtrust card, reset dashboard, card catalogue
Dashboard → **✎ Edit dashboard**: show or hide the built-in cards, add cards for a host, a folder, an infrastructure or cloud provider, Grafana, Microsoft Sentinel, Pingdom, BeyondTrust, Puppet or Entra ID, and pin checks (ping, TCP, HTTP(S), certificate, DNS, SPF / DMARC). A card's ⋯ menu renames it, makes it full width or removes it. **Reset to default** (asks first) puts the original dashboard back. Cards contact nothing by themselves. Your organisation can lock the dashboard (DashboardLocked) or set its cards (DashboardCards).

## Hyper-V: the PowerShell module on Windows Server and Pro
keywords: hyper-v module, install hyper-v powershell, windows server hyper-v, windows 11 pro hyper-v, hyper-v administrators, rsat hyper-v
Infrastructure → a Hyper-V provider without the module shows the fix for this Windows edition and **⚙ Install the Hyper-V module (UAC)**: Windows Server → Install-WindowsFeature Hyper-V-PowerShell; Pro / Enterprise / Education → the optional feature Microsoft-Hyper-V-Management-PowerShell. Windows Home has no Hyper-V. The provider also says whether you are in "Hyper-V Administrators" (needed for the cmdlets unless the app runs elevated); add yourself, then sign out and in.

## Quick Start (first-run set-up)
keywords: quick start, setup, first run, wizard, welcome, getting started, set up everything
The first start of a new profile opens Quick Start (again any time: Settings → About): protection, category locks, personal settings, AI with the one-click Foundry Local set-up, organisation sign-in, BeyondTrust (tested before it is kept), cloud accounts, infrastructure, importing connections, Teams and Pingdom, log sinks, backup and your team. Every page can be skipped. Admin-protected choices are asked for once, at Finish; "Take me there when I finish" opens the right Settings page.

## Foundry Local in one click (phi-4-mini)
keywords: foundry local, set up foundry, phi-4-mini, ai engine install, npu, local ai, one click ai
module: ai
With AI on and Foundry Local not ready, the AI panel shows **⚙ Set up Foundry Local** (also in Quick Start and Settings → AI & models → Foundry Local → Set up now): it installs Foundry Local from Microsoft (winget), starts it, downloads phi-4-mini and checks that it answers. phi-4-mini is the default model on every PC; the model guide suggests a lighter one for small PCs. Your organisation can keep the engine fixed (AiAllowEngineChange) or stop model downloads (AiAllowModelDownload).

## Docs: Markdown documents and runbooks
keywords: docs, documents, markdown, md, runbook, wiki, readme, editor, preview, folders, pin, pinned, cluster, proxmox, knowledge base, export, import, outline, task list
DOCUMENTS → 📄 Docs (separate from Notes). Markdown documents in folders and subfolders, each its own encrypted file.
- **Folders:** ＋ Folder (use / for subfolders, e.g. Runbooks/Linux); right-click a folder to add a document there, rename or move it, or remove it (its documents move up; nothing is deleted).
- **Pins:** 📌 Pin to… a host, a host folder (every host in it) or infrastructure (a Proxmox cluster, vCenter, Hyper-V, Docker host…). Right-click a host or an infrastructure provider → 📄 Docs to open what's pinned to it (or start one).
- **Editor:** Split (editor beside the live preview), Edit or Preview. Toolbar for bold, italic, strike, code, headings, lists, tasks, quotes, links, code blocks, tables and rules. Shortcuts: Ctrl+B, Ctrl+I, Ctrl+K (link), Ctrl+S (save a version), Ctrl+Enter (tick a task), Tab / Shift+Tab (indent). Enter on a list line continues the list; on an empty item it ends it. The outline jumps to a heading.
- **Saving:** autosave 1 s after you stop typing and whenever you leave the document; a version is kept every 10 minutes and on Ctrl+S (History… restores one). Trash and restore; Empty trash deletes for good.
- **Import / export:** Import .md… (one or more files, into the selected folder); Export .md… per document. Backups include your documents with your notes.
- **Safe preview:** only https: and mailto: links work, after asking (offline mode applies); images are never downloaded (their description shows); HTML is shown as text.

## Post-quantum security
keywords: post-quantum, quantum, pqc, ml-kem, ml-dsa, kyber, dilithium, harvest now decrypt later, hybrid, sntrup, mlkem768x25519, quantum-safe
Settings → Security & privacy → Security & encryption → POST-QUANTUM shows a live check and what is covered.
- **What it protects against:** data recorded or copied today (network traffic, a disk image, a file share, a backup) being opened later by a quantum computer, and forged updates.
- **Everything saved** is AES-256-GCM, which quantum computers don't break. The data key's protectors are quantum-safe: your Windows account (DPAPI), the master password, and Windows Hello (a hybrid since 0.6.12: the Hello signature plus a secret only your Windows account can open; still one prompt).
- **SSH** offers the hybrid post-quantum key exchanges first (mlkem768x25519-sha256, OpenSSH 10's default, and sntrup761x25519-sha512 from OpenSSH 9.0). The session strip shows what was negotiated. **Require post-quantum key exchange for SSH** (admin-protected; Group Policy PqSshRequired) refuses servers without one: update their OpenSSH or turn it off.
- **Shared libraries:** new libraries and members use hybrid keys (P-256 + ML-KEM-768 to share keys, P-256 + ML-DSA-65 to sign). Members who joined before 0.6.12 keep working with their classical keys; join again to get hybrid ones.
- **Updates** are signed with two release keys (ECDSA P-256 and ML-DSA-65) and offered only when both signatures verify.
- **Not covered (outside the app's control):** HTTPS to cloud and Microsoft services (Windows' own TLS), Remote Desktop, SSH host and user keys (no post-quantum SSH signatures exist yet), Entra sign-in tokens. The add-on bridge stays classical: it never leaves your PC.
