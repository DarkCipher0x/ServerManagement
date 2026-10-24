# DarkCipher ServerManagement vs SecureCRT — gap analysis & roadmap

Goal: a rock-solid, long-running terminal + remote-administration suite for an IT engineer, matching SecureCRT's terminal/session strengths (minus RDP), and going beyond it with Vultr management, an encrypted vault, a local-AI operator, and a network-forensics/diagnostics stack — all for servers and networks the operator administers.

Scope note: everything here is for the operator's **own** infrastructure. The "forensics/diagnostics" stack means reading a host's own connections, sockets, routes and (where the operator installs the tooling) its own packet captures for troubleshooting — the same ground `ss`, `tcpdump`, `journalctl` and Wireshark cover. No scanning, capture or access of third-party systems.

Date: 2026-10-18. Sources: VanDyke SecureCRT capabilities & key-features pages, SecureCRT Session Options docs.

## Legend
✅ have · 🟡 partial · ❌ missing · ➕ beyond SecureCRT

---

## 1. Connection protocols

| Capability | SecureCRT | Us today | Target |
|---|---|---|---|
| SSH2 (password, key, keyboard-interactive) | ✅ | ✅ | keep |
| SSH agent / FIDO / X11 fwd | ✅ | 🟡 via external OpenSSH | native agent later |
| Jump hosts / proxy | ✅ | ✅ (chained) | keep |
| Telnet | ✅ | ❌ | **Phase 1** |
| Telnet/TLS | ✅ | ❌ | Phase 3 |
| Raw TCP socket | ✅ | ❌ | **Phase 1** |
| Serial (COM) | ✅ | ❌ | **Phase 1** (USB-serial adapters appear as COM ports) |
| Local shell (ConPTY) | ✅ | ❌ | Phase 2 |
| RDP | ✅ (Windows) | ❌ | **thin launch** via built-in `mstsc.exe` + generated `.rdp` (no embedded ActiveX) |

## 2. Terminal emulation & session options

| Capability | SecureCRT | Us today | Target |
|---|---|---|---|
| xterm-256color, alt-screen, mouse | ✅ | ✅ (xterm.js) | keep |
| Per-session options (font, colours, scrollback, encoding) | ✅ | 🟡 global only | **Phase 1** per-session |
| Named colour schemes | ✅ | 🟡 one theme | Phase 2 |
| Keymaps / mapped keys | ✅ | ❌ | Phase 4 |
| Login/expect automation | ✅ | 🟡 one initial command | Phase 4 |
| ANSI colour / line-wrap / logical rows | ✅ | ✅ (xterm.js) | keep |

## 3. Productivity

| Capability | SecureCRT | Us today | Target |
|---|---|---|---|
| Tabbed sessions | ✅ | ✅ | keep |
| Tiled/split panes | ✅ | ✅ split/tile/merge/pop-out (session 3) | keep |
| Session manager / folders | ✅ | ✅ hosts + nested folder tree, encrypted export/import | keep |
| **Right-click context menus everywhere** | ✅ | ✅ every list, text box, tab, terminal, tree (session 4) | keep |
| Screenshots of sessions (terminal, RDP window, any tab) | 🟡 | ✅ Ctrl+Shift+S, save/copy/share (session 4) | keep |
| Button bar (one-click commands per session) | ✅ | 🟡 snippets | Phase 3 |
| Command/chat window → send to all tabs | ✅ | 🟡 multi-run (non-interactive) | **Phase 2** interactive broadcast |
| Session logging to file (+ timestamps, raw/clean) | ✅ | ❌ | **Phase 1** |
| Scrollback find / highlight keywords | ✅ | ❌ | Phase 2 |
| Auto-reconnect / keep-alive | ✅ | 🟡 keep-alive, manual reconnect | **Phase 1** auto-reconnect + backoff |
| Scripting (Python/VBScript) | ✅ | ❌ | Phase 4 (consider embedded scripting or AI-driven) |

## 4. File transfer

| Capability | SecureCRT (+SecureFX) | Us today | Target |
|---|---|---|---|
| SFTP browser | ✅ | ✅ | keep |
| Drag/drop up/down, recursive | ✅ | ✅ | keep |
| SCP / Xmodem/Zmodem | ✅ | ❌ | Phase 4 (Zmodem over serial/SSH) |
| In-place remote edit | partial | ✅ | keep ➕ |

## 5. Beyond SecureCRT (our differentiators)

- ➕ **Vultr cloud management** (full API v2, console, create/power/DNS/firewalls/DBs…).
- ➕ **Encrypted vault** (AES-256-GCM) for every credential, key and setting.
- ➕ **Per-server management dashboards** (services, processes, logs, packages, Docker, firewall, users, cron).
- ➕ **Local-AI operator** (Ollama, approval-gated) — to be expanded into an analysis/organisation assistant across sessions, logs, captures and inventory.
- ➕ **Network diagnostics/forensics** for the operator's hosts (below).

## 5a. Cloud providers (multi-provider, beyond SecureCRT)

Our Vultr screens are generated from a data-driven catalog (`VultrCatalog.cs`), so provider support generalises into a **provider layer**: each provider supplies auth, a resource catalog and endpoint mappings; the generic list/create/action/console UI stays the same.

- ✅ **Vultr** (API v2) — full.
- ➕ **Akamai Connected Cloud / Linode** (Linode API v4) — instances, volumes, NodeBalancers, domains, firewalls, object storage, LKE; token auth + optional OAuth.
- ➕ **Others** as catalogs land (e.g. Hetzner, DigitalOcean, AWS-lite) — each is a catalog + auth adapter, not a rewrite.
- Hosts imported from any provider keep a `provider:instanceId` link so power/console actions route to the right API.

## 5b. Extensibility — add-ons & SDK (beyond SecureCRT)

A first-class add-on contract (shipped as `DcxManager.Addons.dll` + an SDK with a sample, mirroring WhisperDesk's model), so the suite is extended without rebuilding it. Add-on kinds:

- **Tool providers** — network diagnostics and packet/PCAP analysers (the plug-in layer from §6); an add-on can register a new analysis tool, a capture backend (tshark/Npcap/other) or a report renderer.
- **Cloud provider catalogs** — a provider adapter (auth + resource catalog) plugs into the provider layer (§5a) to add Linode/Akamai, Hetzner, etc.
- **Session/terminal actions** — button-bar commands, context-menu items, output transformers.
- **AI tasks** — named analysis tasks with their own prompt + output schema for the AI Engineer panel.

Add-ons run isolated (a faulting add-on can never crash the app or a session, per §8.1), are listed/toggled in an Add-ons screen, and are signed/trusted before load. SDK ships the contract DLL, XML docs, and a buildable sample.

## 5c. Reporting

- **Exports** — any analysis, capture summary, diagnostics run, inventory or session log exports to **PDF / HTML / Markdown** (PDF via the MigraDoc approach already used in WhisperDesk).
- **AI-generated reports** — the AI Engineer turns a session/logs/PCAP-summary into a structured report (summary · severity · observations · errors · warnings · causes · evidence · suggested investigation · next steps), rendered to the same export formats. Incident notes and config-comparison reports are first-class outputs. Everything is generated locally via Ollama; nothing leaves the machine.

## 6. Network forensics / diagnostics stack (new; beyond SecureCRT)

For hosts the operator administers. Degrades gracefully when a tool isn't installed on the target.

- **Connections & sockets:** live `ss`/`netstat` table (proto, state, pid, process), refresh/sort/filter, "who owns this port".
- **Interfaces & routing:** addresses, routes, ARP/neighbours, DNS config, link stats/errors.
- **Flow view:** top talkers / per-connection byte counts (from `ss -i`, `/proc/net/*`, nftables counters).
- **Live capture (operator-initiated, on the operator's host):** run `tcpdump`/`tshark` on the remote with a BPF filter, stream a summary back, and optionally pull the `.pcap` for local analysis.
- **Local capture analysis:** open a `.pcap`/`.pcapng` with **SharpPcap/PacketDotNet**, show a packet list, protocol breakdown, conversations, and a hex/detail pane (Wireshark-lite). Live local capture only if **Npcap** is installed.
- **Latency/path:** ping, tracepath/mtr-style table, TCP connect timing, TLS handshake/cert inspector, port reachability for the operator's own endpoints.
- **Firewall forensics:** nftables/iptables ruleset view, fail2ban jails, recent drops from the journal.
- **Log forensics:** auth.log/journal analysis — failed logins, new sessions, sudo events — with AI summarisation.

## 7. Remote-administration toolset additions

- Service/unit management ✅ (have) → add masking, edit unit/override, dependency tree.
- Process tools ✅ → add `lsof`/open files ✅, strace-on-demand, renice, cgroup/systemd-cgls view.
- Package mgmt ✅ → add history/rollback where supported, held packages, reboot-required banner ✅.
- Storage: `lsblk`/`df` ✅ → add SMART (`smartctl`), mounts/fstab editor, LVM, iostat.
- Users ✅ → add SSH authorized_keys manager per user, password/expiry (`chage`), group editor.
- Scheduled tasks: cron/timers ✅ → add systemd timer editor, at-jobs.
- Containers: Docker ✅ → add Podman, compose up/pull, image prune ✅, stats ✅.
- Hypervisor hooks: detect Proxmox/libvirt, quick `qm`/`virsh` status (operator's own nodes).
- Config: sysctl viewer/editor, kernel modules, dmesg, hardware (`lshw`/`lscpu`) ✅-ish.

## 8. Stability (the "rock solid, runs for hours" requirement)

Root principles, enforced in Phase 1:
1. **Total session isolation** — a fault in one session (SSH drop, serial unplug, decode error, backend throw) can never crash the app or another tab. Every backend callback is wrapped; the UI exception handler already keeps the app alive, but we add per-session guards and a visible per-session error state.
2. **Bounded memory for long runs** — xterm scrollback is capped; the output pipeline uses pooled buffers and a coalescing flush; session logs stream to disk, never held in RAM.
3. **Auto-reconnect with backoff** — keep-alive detects dead links; optional auto-reconnect re-establishes the shell and (optionally) replays a login macro.
4. **No blocking the UI thread** — all I/O is async/offloaded; the render bridge batches at frame cadence (already) and is backpressure-aware.
5. **Deterministic teardown** — disposing a tab always tears down the backend, its threads, timers, port handles and temp files; verified by the self-test and a soak test.
6. **Crash forensics** — richer logging: first-chance for backend threads, a crash marker written before any hard exit, and a "report last error" action.

## 9. Architecture (target)

Redefine the product as an **IT engineering workstation**, not a terminal client. Session model:

```
Server ─ Connection ─ Credential ─ Terminal ─ Logs ─ Files ─ Notes ─ Metrics ─ AI
```

so a host presents as `PROD-WEB-04 [SSH | SFTP | Logs | Notes | Metrics | AI]`, not "SSH tab #17".

Process/ownership layering (fault isolation is the point):

```
UI
 │
Session Manager ── SSH worker · Serial worker · Local-shell worker · Transfer worker
Network Services ── Capture · Diagnostics · Analysis
AI Service ── Ollama (structured JSON + tool-calling)
Persistence ── config/session DB · logs · crash-recovery/workspace
```

**AI never owns the terminal.** Analysis is automatic; execution is approval-gated:

```
Terminal selection/output → AI Context Builder → Ollama → structured response → AI panel
AI-suggested action → command preview → user approval → terminal
```

**AI provider is pluggable.** Default is **fully local** — Ollama, or any OpenAI-compatible local server (LM Studio, llama.cpp). Optionally the operator can configure **other secure API agents** (an OpenAI-compatible or Anthropic endpoint, Azure OpenAI, a self-hosted gateway); each provider carries a trust/privacy flag, the default local one never leaves the machine, and a cloud provider is used only for the data the operator explicitly sends. All providers honour the same structured-output + tool-calling contract, so the AI Engineer panel is identical regardless of backend. (AI providers are also an add-on kind, §5b.)

Ollama returns **schema-constrained structured objects** (Summary · Severity · Observations · Errors · Warnings · Possible causes · Evidence · Suggested investigation), not a wall of prose. Tool-calling lets the model *propose* actions; the app controls execution.

## 10. SecureCRT acceptance checklist (the benchmark) — reviewed for 0.3.0 (2026-10-20)

| Area | Target | Status |
|---|---|---|
| SSH / session management | SecureCRT-class | ✅ hosts + folders, jump chains, keepalive, auto-reconnect with back-off, per-host terminal profiles, quick connect with protocols, encrypted export / import, shared team libraries |
| Terminal emulation | SecureCRT-class | ✅ xterm.js, colour schemes, encodings, key maps per profile |
| Profiles (per-host) | SecureCRT-class | ✅ terminal profiles (colours, cursor, encoding, keys, logging), a host picks its own |
| Tabs | SecureCRT-class or better | ✅ tabs, groups with colours |
| Split sessions | required | ✅ split / tile / merge / pop-out |
| Serial | first-class | ✅ COM and USB-serial, BREAK, reconnect on replug |
| USB serial | first-class | ✅ |
| Logging | better | ✅ per-session encrypted, timestamped logs + Session logs screen |
| Search (scrollback) | better | ✅ find in scrollback |
| Automation | comparable/better | 🟡 snippets + multi-run, user menu, Ansible (inventory, ad-hoc, playbooks), approval-gated AI operator, add-ons; no VBScript/Python session scripting (by design: scripts run on servers, not in the terminal) |
| Button bar | one-click commands | 🟡 snippets (pinned quick targets) and the commander's user menu; no per-session button strip |
| Keyword highlighting in the terminal | nice to have | ❌ (search covers the need today) |
| Zmodem / Xmodem | serial file transfer | ❌ (SFTP / SCP everywhere else) |
| SSH agent / FIDO / X11 | native | 🟡 via external OpenSSH (Windows agent); keys in the vault |
| Context menus | significantly better | ✅ every list, box, tab, tree; grouped submenus |
| Network diagnostics | integrated | ✅ LAN discovery (ping sweep, SSH, ports, connection tool, monitoring), the Network & mail toolbox (TCP / traceroute / mtr / DNS + propagation / HTTP / TLS / MTU, SPF / DMARC / DKIM / blacklists / MTA-STS / headers / WHOIS / ASN), dashboard checks |
| Packet analysis | integrated tooling | ❌ (deferred: tshark / pcap add-on) |
| AI | native local AI | ✅ Foundry Local / Ollama / optional providers, approval-gated terminal actions, reports |
| Server organisation | native | ✅ folders, environments, team libraries with groups |
| Session screenshots (terminal / RDP / tab) | native | ✅ |
| Windows Share (reports, output, CSV, scripts, files, screenshots) | native | ✅ |
| RDP | thin launch (mstsc) | ✅ embedded (ActiveX) or mstsc, RD Gateway |
| VPN | — (SecureCRT has none) | ➕ WireGuard / OpenVPN profiles, split-tunnel routes, "connect through" per host |
| Security of stored data | — | ➕ whole application encrypted (AES-256-GCM, master password / Windows Hello), admin-protected settings, Group Policy, offline mode, directory sign-in (AD / LDAPS / Entra) |

Remaining gaps after 0.3.0, honestly: terminal keyword highlighting, Zmodem, a native SSH agent / FIDO, packet capture
analysis, and session scripting — none of them blocks replacing SecureCRT for day-to-day server work; they stay on the
list for 0.4.

## 11. Phased plan (ordered per engineering review)

Stability before surface features. Nothing bolted on until the core is solid.

- **Phase 1 — Stabilisation** ← *in progress.* Crash capture + recovery, session lifecycle & isolation, clean deterministic shutdown, reconnect + auto-reconnect with back-off, bounded memory/output for long runs, resource-leak elimination, workspace recovery, and a stress/soak harness (the Phase-6 torture tests, started early as regression gates).
- **Phase 2 — Terminal UX.** Right-click context menus everywhere; tabs/splits; per-host & global profiles; session manager; automatic timestamped logging; searchable scrollback + keyword highlight; keyboard customisation; broadcast input.
- **Phase 3 — Connectivity.** Serial / USB-serial (baud, data/stop bits, parity, flow control; device appear/disappear handling), Telnet for legacy kit, local shells (ConPTY), raw TCP, richer SSH (agent, env vars), forwarding, SCP/Zmodem.
- **Phase 4 — Engineering toolkit.** Network Toolbox as a separate surface (ping, traceroute/mtr, DNS, ARP/neighbours, interfaces/routes, sockets, HTTP/TLS inspection, subnet calculators) + packet capture/PCAP via a **tool-provider plug-in layer** (tshark/Wireshark where installed; SharpPcap/PacketDotNet for local .pcap analysis) + system/admin tooling (SMART, LVM, sysctl, etc.).
- **Phase 5 — Ollama AI.** Dockable AI Engineer panel; context-aware log/session/PCAP analysis with structured results; notes/incident write-ups; config comparisons; controlled, approval-gated administrative assistance.
- **Phase 6 — Torture testing.** 8h and 24h sessions; dozens of simultaneous terminals; reconnect storms; serial device removal/reinsertion; sleep/resume & NIC changes; extreme output; huge scrollback; rapid tab create/destroy; Ollama unavailable mid-request.

Each phase ships built, self-tested, and soak-tested before the next. "Done" = *I can replace SecureCRT with this on my engineering machine.*
