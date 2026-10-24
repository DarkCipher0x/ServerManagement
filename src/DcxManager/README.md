# DcxManager (the app)

The exe (`DcxServerManager`): `App.xaml.cs` (start-up and `WireLayers()`, the composition root), the main window (`MainWindow.xaml.cs` plus `Shell/` for its sidebar, navigation, tabs, workspace and sessions), **Views/** every screen (`Settings/` and `Terminal/` hold the Settings screen's categories and the terminal's parts), **Services/** the app-level services that aggregate everything (dashboard overview, privacy report, crash recovery, screenshots, app lock, tab groups), **Api/** the local API and broadcast, **Rdp/** the embedded Remote Desktop, `Themes/Controls.xaml`, `Assets/web` (the terminal page), and **SelfTest/** (`--selftest`: the full suite by area, the AI-off suite, and the per-feature test files).

May reference: every project.
