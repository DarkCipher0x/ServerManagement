# Dcx.Core

The base everything stands on, with no WPF and no reference to any other project.

- **Services/** the encrypted store and keyring (SecureStore), settings and every stored record (AppSettings, AppData, Models), the audit log, offline mode and the network policy, the capability registry, secret masking (Redaction), the AI switch and its lock (AiFeatures, FeatureLock), the assistant contract (Assist), snapshots, the store schema, the updater, help search (HelpKb), environment labels (Envs), AppInfo / TestMode, and the **seams** lower code uses instead of the UI (`Ui.cs`: dialogs, shell, UI thread, log).
- **Notes/**, **Incidents/**, **Documents/**: encrypted notes and incidents, PDF text and document sources.
- **Reports/**, **Sessions/**, **Infra/**, **Cloud/**, **Browser/**, **Addons/**, **Api/**: only the stored records and contracts of those areas (the engines live in their own projects).
- **Assets/help/** the built-in help, embedded as `help/help.md`.

May reference: nothing of ours.
