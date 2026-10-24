# Security policy

## Reporting a vulnerability

Please report security problems **privately** through GitHub: the repository's **Security** tab → **Report a
vulnerability**. Don't open a public issue for anything that could put users at risk.

Include the version (Settings → About, or the MSI file name), what you did, what happened and what you expected. Leave out
real passwords, keys, tokens, IP addresses and host names — use made-up or TEST-NET values (192.0.2.x, 198.51.100.x,
203.0.113.x).

## Supported versions

Only the latest release receives fixes. Each release is in its own `dist\<version>\` folder with SHA-256 checksums, an
SBOM and a security report.

## What is in scope

- The application (`src\`), the installer (`installer\`), the build and packaging scripts (`build.ps1`, `tools\`), and the
  add-on bridge and SDK (`samples\addons\`).
- How the app stores data (the encrypted store, keyring, audit log, snapshots, backups) and what it sends on the network.

How the app is built to resist attack, and what each release was tested against, is described in
[docs/SECURITY.md](../docs/SECURITY.md) and [docs/SECURITY-REPORT.md](../docs/SECURITY-REPORT.md).
