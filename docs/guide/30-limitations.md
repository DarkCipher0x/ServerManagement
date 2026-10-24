# Known limitations

## Not yet verified against real systems

Everything below passes the self-test against fakes, loopback servers or real local processes, but has not been tried against the real thing:

- SSH against real servers, including jump hosts, keyboard-interactive and sudo;
- embedded Remote Desktop sign-in (only a refused connection is tested);
- the Vultr, Contabo, Hetzner, Microsoft Azure and Google Cloud APIs with real accounts (Contabo, Hetzner, Azure and
  Google Cloud were built from their official API documentation and tested against fakes only);
- Docker, Proxmox VE and Unraid on real hosts (Unraid field names are unverified against 7.x);
- UniFi (0.6.16) against a live console, the Cloud Connector and Site Manager (built from Ubiquiti's published API
  references; the Integration API needs UniFi Network 9.0 or later and doesn't cover everything the UniFi web UI does,
  e.g. firmware upgrades, port profiles or traffic statistics history);
- Bitwarden (0.6.14) with a real `bw` / `bws` and a live account (cloud, self-hosted or Vaultwarden); vault items are
  read-only (nothing is created or changed in Bitwarden), and the CLIs must be installed separately;
- vault credentials kept in Bitwarden or Password Safe (0.6.17) with a live vault and a live Password Safe (the fakes
  cover the protocol, the leases and the unattended rules);
- Fill sign-in (0.6.20) on real sign-in pages: tested on loopback pages (one step, steps with a code, a shadow root, no
  form); a sign-in inside a frame from another site can't be reached (copy the user name, password or TOTP code instead);
- real AI and vision model calls (Ollama, NPU, OpenAI-compatible, Anthropic);
- OBS as a broadcast consumer;
- SFTP resume and SCP on a real server;
- WSL and PowerShell 7 shells (not installed on the build PC), serial hardware;
- `.exe` and signed add-ons;
- snapshot restore → restart by hand;
- multi-day uptime (the 8–24 h soak test is deferred);
- **0.5.0:** real files from every connection manager the importer reads; automatic sign-in, Kerberos SSH and RDP single sign-on on a domain; a Puppet server with PuppetDB and Bolt; WSL Containers (wslc's commands are read from its own help); VS Code Remote-SSH with ssh-agent; Microsoft Teams with a real app registration; Pingdom; BeyondTrust PRA and Password Safe;
- **0.4.0:** real Elasticsearch / OpenSearch, Grafana Loki, Azure Monitor / Sentinel, Splunk and Graylog sinks and a real
  Grafana server (TLS fakes on 127.0.0.1 and an in-process Grafana in the self-test); the Event Viewer exports on a
  locked-down PC; the Azure and Google Cloud screens added in 0.4.0 against real subscriptions and projects; the data
  folder migration on an installation whose store was moved; Group Policy branding (`BrandingTheme` / `BrandingLogoPath`)
  pushed from a real domain controller.

## Limited by design, or for now

- **Updates are off** until the releases are code-signed. Install newer MSIs by hand.
- **BeyondTrust Privilege Management (EPM)** isn't integrated; PRA / Remote Support is read-only (Jump Items open in your access console).
- **Settings locks:** about half of the policies have a control that greys out on its card; the rest are enforced but not greyed out yet.
- **RD Gateway** hosts open in mstsc, not in the embedded client.
- **No OCR** for scanned PDFs or images.
- **Web pages** (consoles, the document viewer) render at 100 % and don't follow the UI text size.
- **Error-log lines written before unlock** are plain text (exception text only); everything after unlock is sealed.
- **Terminal broadcasts** to OBS are text without colour.
- **SCP can't resume** a file; it restarts that file.
- **Offline mode** doesn't cover the WebView2 runtime's own update / SmartScreen traffic, Windows services, or external programs after launch.
- **Add-ons** run at Low integrity but are not network-isolated (AppContainer is planned).
- **Display recovery** covers terminal tabs only; web console windows and the document viewer show a message and need reopening.
- **Tab groups** and per-session terminal profiles last for the session only.
- **Crash restore** reopens saved-host and local-shell tabs, the pane layout, unsaved remote-file edits and unfinished transfers (ADR-022); tabs come back disconnected and nothing reconnects or writes to a server by itself. Ad-hoc connections aren't restored.
- **The AI conversation** is kept in memory only (by decision).
- **Azure and Google Cloud:** no serial console (Google VMs offer the read-only serial output); Azure VM rows show no IP
  address (it lives on the network interface), so SSH and "add to hosts" ask for it; identity and role assignments stay
  in the clouds' own portals.
- **Not in 0.2.0:** Windows management over WinRM / CIM, mail server utilities and a built-in MaskedMail (later
  releases or add-ons).
- **Log sinks (0.4.0):** no OpenTelemetry / OTLP exporter yet (the SIEM-native APIs are used directly); the sink list as
  a Group Policy is limited to 2,048 characters of JSON (REG_SZ); sinks connect directly, not through a proxy.
- **Reports for this PC (0.4.0):** the Security event log and BitLocker status need the app to run as administrator;
  Windows event messages come rendered by `wevtutil` (the provider's message DLL must be installed for full text).

## 0.5.1 features

- **VNC:** Apple's own Screen Sharing extensions ("High Performance" and Apple's security types 33 / 35 / 36) are
  undocumented and not supported; a Mac is reached with Apple Remote Desktop sign-in (type 30) or a VNC password, and its
  picture is sent in the standard encodings (Raw, CopyRect, RRE, Hextile, ZRLE — no Tight or H.264), so a busy 4K screen
  is slower than in Apple's own app. No audio, file transfer or multiple monitors. The clipboard carries Latin-1 text
  only (RFB's limit). The Windows key reaches the remote side as Super / ⌘ while the tab has the focus, but Windows still
  opens the Start menu for it on its own (use Keys ▾ → ⌘ for a clean press).
- **Host import** finds machines that answer on the scanned ports; a host whose firewall drops them stays invisible. The
  platform guess comes from banners and is a guess: check it in the grid.
- **Locks** are an access control inside the app for the signed-in Windows user. They don't protect against malware
  running as that user (see SECURITY.md); the store's encryption, the master password and Windows Hello do the at-rest
  part.
- **Hyper-V:** the module install runs Windows' own command; on a Server without internet access the feature source
  must be available (installation media or WSUS).

## Intermittent self-test observations

Two checks have each failed once under heavy load and passed on every rerun: the add-on "flood" case (a timeout won the race) and a closed-view leak check that once kept the assistant screen. Both are tracked; neither has been seen in normal use.
