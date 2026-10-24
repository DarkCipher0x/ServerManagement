# About this guide

This is the complete guide to **DarkCipher ServerManagement {VERSION}**: what it does, how to use every part of it, how to run and administer it, how it is secured, how it is built and tested, and what changed in each release. It is generated from the project's own documentation for this exact version, so it matches the installed build.

## Who each part is for

| Part | For | What it covers |
|---|---|---|
| 1. Overview | everyone | what the workstation is, the feature set, cloud providers, encryption at a glance |
| 2. User guide | daily users | every screen and feature, in the same words as the in-app help (F1) |
| 3. Administration | whoever installs and looks after it | install / upgrade / uninstall, where data lives, keys and access, backup and recovery, network control, the local API, add-ons, logs, troubleshooting; **for the organisation:** identity (Active Directory, LDAPS, Entra), Group Policy with a baseline, admin protection, syslog and log sinks for a SIEM, observability, team features, branding by policy, offboarding |
| 4. Security | administrators, reviewers | the security model control by control, how each is verified, and the security report with the penetration tests run against this version |
| 5. Developer reference | anyone changing or extending it | repository layout and coding rules, the project tree and how it is built out, the source code file by file with its main flows, the complete build and release procedure, the architecture decisions (ADR-001 to ADR-058), provider API coverage, add-ons and their SDK (API 2), the self-test |
| 6. Release notes | everyone | 0.1.0 to {VERSION} |
| 7. Known limitations | everyone | what is not done, not verified against real systems, or limited by design, and the hand-test checklist |

## The state of {VERSION} in one paragraph

A release is only built when the full automated self-test passes with no failures and without ever showing a window (over 400 checks since 0.3.0: crypto, every screen, real local shells, loopback servers and fake provider APIs, the real add-on sandbox over the encrypted bridge, fuzzing of every untrusted-input parser, the penetration-test suite, endurance and leak loops, a real second launch, a real crash and a real renderer kill), the AI-off suite passes, and the published exe passes it too. Every release ships with this documentation set as PDFs, a security report, the project structure and its sanitised source. Most features have **not** yet been verified against real external systems: SSH servers, Remote Desktop sign-in, Vultr / Contabo / Hetzner / Azure / Google Cloud / Proxmox / Docker / Unraid / vCenter / Hyper-V accounts, AI models with tools, serial hardware, a real file share for the shared library, real SIEM sinks and a real Grafana. The MSI is **not code-signed**, so Windows SmartScreen shows "unknown publisher" and in-app updates are switched off. Treat it as a test build for your own use until the hand tests in Part 7 pass.

## Conventions

- **Menu paths** are written *Settings → Security → Encrypted backup*.
- **Keys** are written Ctrl+Shift+F.
- **Example addresses** use the documentation ranges 192.0.2.x, 198.51.100.x and 203.0.113.x; they are not real hosts.
- **⚠** marks an external (internet) destination, exactly as the app does.
