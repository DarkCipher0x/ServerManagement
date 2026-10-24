# Source map

One solution (`DarkCipher.ServerManagement.sln`), one exe (`DcxServerManager.exe`), ten projects. References point down
only; the self-test checks it on the built assemblies ("projects"), and with AI features off the AI assembly is never
loaded (checked at the end of the AI-off run).

```
DcxManager (the app: screens, main window, composition root, self-test)
  ├── Dcx.Ai            the AI module (loaded only when AI features are on)
  ├── Dcx.Browser       portal browser engine
  ├── Dcx.Library       shared library (team hosts and credentials)
  ├── Dcx.Addons        add-on packages, sandbox, bridge, API
  ├── Dcx.Cloud         cloud layer + Vultr, Hetzner Cloud, Contabo
  ├── Dcx.Infra         Docker, Proxmox, Unraid, vCenter, Hyper-V
  ├── Dcx.Ui            the WPF kit + module contracts
  ├── Dcx.Connectivity  SSH, sessions, files, reports, terminal bridge
  └── Dcx.Core          store, settings, records, audit, policy, seams
```

Each project has a README with what lives there and what it may reference. Namespaces stay `DcxManager.*` throughout
(folder = namespace inside each project). Lower projects never call the UI or the app directly: they offer a hook or go
through the seams in `Dcx.Core/Services/Ui.cs`, and `App.WireLayers()` connects them at start-up.
