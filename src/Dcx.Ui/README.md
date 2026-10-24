# Dcx.Ui

The WPF kit every screen uses: **Views/** Dialogs (and the error box), Menus, Hint, NetUi, EnvUi (environment pills, EnvTab), DetachedWindow, the module contracts (`AppModules.cs`: IAppModule / IAppHost) and their contribution types (`ModuleContributions.cs`); **Services/** ThemeManager and ThemePack, Typography, NavLayout, UiErrorGuard, ShareService.

May reference: Dcx.Core. It knows no screen: what it needs from the app is registered in `App.WireLayers()`. The theme XAML (`Themes/Controls.xaml`) stays in the app.
