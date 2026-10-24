using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Notify;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: log sinks (Elasticsearch / OpenSearch, Grafana Loki, Azure Monitor / Sentinel, Splunk, Graylog, webhooks) and
// the dashboards card (Grafana, Kibana, SIEM links) — 0.4.0 Observability. The sink list and the send switches are
// admin-protected like syslog; each sink's secret is written straight to the encrypted settings (never protected, never a
// policy) and never shown again.
public sealed partial class SettingsView
{
    private Border LogSinksCard()
    {
        var sinks = LogShipper.Sinks;
        var pendingSecrets = new Dictionary<string, string>();
        var removed = new HashSet<string>();
        LogSink? editing = null;
        bool managed = PolicySettings.IsManaged(nameof(AppSettings.ObserveSinks));

        var list = SettingTag.For(new ListBox { Height = 110, Width = 560, HorizontalAlignment = HorizontalAlignment.Left }, nameof(AppSettings.ObserveSinks));
        void Fill()
        {
            list.Items.Clear();
            foreach (var s in sinks) list.Items.Add(new ListBoxItem { Content = $"{(s.Enabled ? "●" : "○")}  {s.Name}  —  {s.KindTitle}  —  {s.Host}", Tag = s, Padding = new Thickness(4, 2, 4, 2) });
            if (editing != null) list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == editing);
        }
        var on = Bound(new CheckBox { Content = "Ship logs to the sinks below (HTTPS only)", IsChecked = _s.ObserveEnabled }, nameof(AppSettings.ObserveEnabled));
        var audit = Bound(new CheckBox { Content = "The audit log (who did what, when)", IsChecked = _s.ObserveSendAudit }, nameof(AppSettings.ObserveSendAudit));
        var app = Bound(new CheckBox { Content = "The application (error) log", IsChecked = _s.ObserveSendAppLog }, nameof(AppSettings.ObserveSendAppLog));
        var sessions = Bound(new CheckBox { Content = "Terminal session logs (every logged session's lines; which sessions are logged: Terminal & connections → Session logs)", IsChecked = _s.ObserveSendSessionLogs }, nameof(AppSettings.ObserveSendSessionLogs));

        // ---- the editor for one sink ----
        var name = new TextBox { Width = FieldWidth };
        var kind = new ComboBox { Width = FieldWidth };
        foreach (var k in LogSink.Kinds) kind.Items.Add(new ComboBoxItem { Content = LogSink.TitleOf(k), Tag = k });
        var url = new TextBox { Width = FieldWidth + 200 };
        var index = new TextBox { Width = FieldWidth };
        var tenant = new TextBox { Width = FieldWidth };
        var auth = new ComboBox { Width = 220 };
        foreach (var a in LogSink.Auths) auth.Items.Add(new ComboBoxItem { Content = a switch { "none" => "none", "apikey" => "API key", "basic" => "user name + password", "bearer" => "bearer token", _ => "Azure subscription (Entra app registration)" }, Tag = a });
        var user = new TextBox { Width = 220 };
        var secret = new PasswordBox { Width = FieldWidth };
        var pin = new TextBox { Width = FieldWidth + 200 };
        var azure = new ComboBox { Width = FieldWidth, DisplayMemberPath = "Name", ItemsSource = _s.AzureAccounts.Where(a => !a.Disabled).ToList() };
        var enabled = new CheckBox { Content = "Enabled", IsChecked = true };
        var kindHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        kindHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var editor = new StackPanel { Visibility = Visibility.Collapsed };
        string Kind() => (kind.SelectedItem as ComboBoxItem)?.Tag as string ?? "elastic";
        string Auth() => (auth.SelectedItem as ComboBoxItem)?.Tag as string ?? "apikey";
        void HintFor()
        {
            kindHint.Text = Kind() switch
            {
                "elastic" => "URL: the Elasticsearch / OpenSearch server (https://es.example.com:9200). Index: where events go (one JSON document each, bulk API). Authentication: an API key (Kibana → Stack Management → API keys) or a user name + password.",
                "loki" => "URL: the Loki server or Grafana Cloud logs endpoint (https://logs-prod-xx.grafana.net). Tenant: X-Scope-OrgID for multi-tenant Loki (empty for a single tenant). Grafana Cloud: user name = the numeric instance id, password = an access-policy token. Index = the job label (optional).",
                "azuremonitor" => "URL: the data collection endpoint (https://<dce>.<region>-1.ingest.monitor.azure.com). Tenant: the data collection rule's immutable id (dcr-…). Index: the stream name (Custom-<table>_CL). Sign-in: one of your Azure subscriptions' app registrations — give it the Monitoring Metrics Publisher role on the rule. Microsoft Sentinel reads the same Log Analytics workspace.",
                "splunk" => "URL: the HTTP Event Collector (https://splunk.example.com:8088). API key: the HEC token. Index: optional (the token's default otherwise).",
                "gelf" => "URL: Graylog's GELF HTTP input (https://graylog.example.com:12201). One GELF message per event. Authentication: usually none (restrict the input by network) or a bearer token behind a proxy.",
                _ => "URL: any HTTPS endpoint that accepts a JSON array of events (POST). Authentication: bearer token, API key (X-API-Key header) or user name + password.",
            };
            bool entra = Kind() == "azuremonitor";
            azure.Visibility = entra ? Visibility.Visible : Visibility.Collapsed;
            if (entra) auth.SelectedItem = auth.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "entra");
            else if (Auth() == "entra") auth.SelectedItem = auth.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "apikey");
        }
        kind.SelectionChanged += (_, _) => HintFor();
        void Load(LogSink? s)
        {
            editing = s;
            editor.Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
            if (s == null) return;
            name.Text = s.Name; url.Text = s.Url; index.Text = s.Index; tenant.Text = s.Tenant; user.Text = s.User; pin.Text = s.Pin; enabled.IsChecked = s.Enabled;
            kind.SelectedItem = kind.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Kind) ?? kind.Items[0];
            auth.SelectedItem = auth.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Auth) ?? auth.Items[1];
            azure.SelectedItem = _s.AzureAccounts.FirstOrDefault(a => a.Id == s.AzureAccountId);
            secret.Clear();
            secret.ToolTip = _s.ObserveSecrets.ContainsKey(s.Id) || pendingSecrets.ContainsKey(s.Id) ? "A secret is stored (encrypted). Leave empty to keep it." : "No secret stored yet.";
            HintFor();
        }
        list.SelectionChanged += (_, _) => Load((list.SelectedItem as ListBoxItem)?.Tag as LogSink);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        void Show()
        {
            var st = LogShipper.Statuses;
            status.Text = st.Count == 0 ? (managed ? "The sink list is set by your organisation's policy." : "No sinks yet. Add one, save it, then Apply.")
                : string.Join("\n", st.Select(x => $"{x.Name}: {x.State}" + (x.LastSent is { } t ? $" · last sent {t:HH:mm:ss}" : "") + (x.Sent > 0 ? $" · {x.Sent} sent" : "") + (x.Queued > 0 ? $" · {x.Queued} queued" : "") + (x.LastError != null ? $" · {x.LastError}" : "")));
        }
        Show();
        void Changed() => Dispatcher.BeginInvoke(Show);
        status.Loaded += (_, _) => { LogShipper.StatusChanged -= Changed; LogShipper.StatusChanged += Changed; Show(); };
        status.Unloaded += (_, _) => LogShipper.StatusChanged -= Changed;

        bool SaveSink()
        {
            if (editing == null) return true;
            var draft = new LogSink
            {
                Id = editing.Id, Name = name.Text.Trim(), Kind = Kind(), Url = url.Text.Trim(), Index = index.Text.Trim(), Tenant = tenant.Text.Trim(), Auth = Auth(), User = user.Text.Trim(),
                Pin = pin.Text.Replace(":", "").Trim().ToUpperInvariant(), AzureAccountId = (azure.SelectedItem as AzureAccount)?.Id ?? "", Enabled = enabled.IsChecked == true,
            };
            if (draft.Validate() is { } why) { Dialogs.Error("Log sinks", $"{draft.Name}: {why}."); return false; }
            var i = sinks.IndexOf(editing);
            if (i >= 0) sinks[i] = draft; else sinks.Add(draft);
            editing = draft;
            if (secret.Password.Length > 0) { pendingSecrets[draft.Id] = secret.Password; secret.Clear(); }
            Fill();
            return true;
        }
        void Add()
        {
            if (sinks.Count >= LogSink.MaxSinks) { Dialogs.Error("Log sinks", $"At most {LogSink.MaxSinks} sinks."); return; }
            var s = new LogSink { Name = "New sink", Url = "https://" };
            sinks.Add(s); Fill(); Load(s); list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == s);
            name.Focus();
        }
        void Remove()
        {
            if (editing == null) return;
            if (!Dialogs.Confirm("Remove sink", $"Remove the sink “{editing.Name}”? Its stored secret is deleted when you Apply.", "Remove")) return;
            removed.Add(editing.Id); pendingSecrets.Remove(editing.Id); sinks.Remove(editing); editing = null; Fill(); Load(null);
        }
        async void Apply()
        {
            if (!SaveSink()) return;
            if (on.IsChecked == true && !sinks.Any(s => s.Enabled)) { Dialogs.Error("Log sinks", "Add and enable at least one sink first."); return; }
            // Secrets first (not protected: they are secrets, kept only in the encrypted settings).
            bool secretsChanged = pendingSecrets.Count > 0 || removed.Any(_s.ObserveSecrets.ContainsKey);
            foreach (var (id, v) in pendingSecrets) _s.ObserveSecrets[id] = v;
            foreach (var id in removed) _s.ObserveSecrets.Remove(id);
            pendingSecrets.Clear(); removed.Clear();
            if (secretsChanged) { AppSettings.Save(); AuditLog.Write("you", "log sinks", "sink secret(s) changed"); }
            var want = new Dictionary<string, JsonNode?>
            {
                ["observe.enabled"] = on.IsChecked == true, ["observe.sinks"] = LogSink.ToJson(sinks),
                ["observe.audit"] = audit.IsChecked == true, ["observe.appLog"] = app.IsChecked == true, ["observe.sessionLogs"] = sessions.IsChecked == true,
            };
            if (await ApplyProtected("change the log sinks", want)) { sinks = LogShipper.Sinks; Fill(); Show(); MainWindow.Instance?.Status("Log sinks saved."); }
        }
        async void Test()
        {
            if (!SaveSink()) return;
            if (editing != null && !LogShipper.Sinks.Any(s => s.Id == editing.Id)) { status.Text = "Apply first, then test."; return; }
            status.Text = "Sending a test event…";
            var r = await LogShipper.SendTestAsync(editing?.Id);
            Show(); status.Text = r + "\n" + status.Text;
        }

        editor.Children.Add(Row(Labeled("NAME", name), Labeled("KIND", kind)));
        editor.Children.Add(kindHint);
        editor.Children.Add(Labeled("URL (https only)", url));
        editor.Children.Add(Row(Labeled("INDEX / STREAM / JOB", index), Labeled("TENANT / RULE ID", tenant)));
        editor.Children.Add(Row(Labeled("AUTHENTICATION", auth), Labeled("USER NAME (basic)", user), Labeled("API KEY / PASSWORD / TOKEN (stored encrypted, never shown again)", secret)));
        editor.Children.Add(Labeled("AZURE SUBSCRIPTION (Azure Monitor: whose app registration signs in)", azure));
        editor.Children.Add(Labeled("PIN THE SERVER'S CERTIFICATE (optional SHA-256 of its certificate or CA)", pin));
        editor.Children.Add(enabled);
        editor.Children.Add(Row(Btn("Save sink to the list", () => SaveSink()), Btn("Send test event", Test), Btn("✕ Remove sink", Remove, "DangerButton")));
        Fill();
        var card = Card("Log sinks",
            "Ships the same events as syslog — the audit log, the application (error) log and, if you choose, terminal session logs — to Elasticsearch / OpenSearch (bulk API), Grafana Loki, " +
            "Azure Monitor / Microsoft Sentinel (Logs Ingestion API), Splunk (HTTP Event Collector), Graylog (GELF) or any HTTPS webhook. HTTPS only (TLS 1.2+; pin a self-signed certificate), secrets masked before anything " +
            "leaves, an encrypted queue of up to 5,000 events while a sink is unreachable, per-sink back-off. The sink list needs admin approval and can be set by Group Policy; each sink's key or " +
            "password stays in the encrypted settings on this PC and is never a policy.",
            on, L("SEND"), audit, app, sessions,
            L("SINKS"), list,
            Row(Btn("＋ Add sink", Add, "SmallButton")),
            editor,
            Row(AdminApproval.Button("Apply", Apply), Btn("Send test to every sink", async () => { status.Text = "Sending…"; var r = await LogShipper.SendTestAsync(); Show(); status.Text = r + "\n" + status.Text; }), Btn("Open Observability", () => MainWindow.Instance.Navigate("observe"))),
            status);
        if (managed)
        {
            foreach (var c in new UIElement[] { list, editor }) c.IsEnabled = false;
            editor.Visibility = Visibility.Collapsed;
            list.ToolTip = $"Set by your organisation's policy ({PolicySettings.Get(nameof(AppSettings.ObserveSinks))?.Scope} policy ObserveSinks).";
        }
        return card;
    }

    private Border DashboardsCard()
    {
        var url = Bound(new TextBox { Text = _s.GrafanaUrl, Width = FieldWidth + 200 }, nameof(AppSettings.GrafanaUrl));
        var org = Bound(new TextBox { Text = _s.GrafanaOrgId > 0 ? _s.GrafanaOrgId.ToString() : "", Width = 90 }, nameof(AppSettings.GrafanaOrgId));
        var token = Bound(new PasswordBox { Width = FieldWidth, ToolTip = _s.ObserveSecrets.ContainsKey("grafana") ? "A token is stored (encrypted). Leave empty to keep it." : "A Grafana service-account token with the Viewer role." }, nameof(AppSettings.ObserveSecrets));
        var links = Bound(new TextBox { Text = string.Join(Environment.NewLine, _s.ObserveLinks), AcceptsReturn = true, MinHeight = 60, Width = FieldWidth + 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "One per line: Label=https://… (Kibana, OpenSearch Dashboards, Microsoft Sentinel, Splunk, your SIEM). They open in the app's browser window from Observability." }, nameof(AppSettings.ObserveLinks));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        foreach (var (setting, box) in new (string, FrameworkElement)[] { (nameof(AppSettings.GrafanaUrl), url), (nameof(AppSettings.GrafanaOrgId), org), (nameof(AppSettings.ObserveLinks), links) })
            if (PolicySettings.Get(setting) is { } m) { box.IsEnabled = false; box.ToolTip = $"Set by your organisation's policy ({m.Scope} policy {setting} = {m.Shown})."; }

        bool Save()
        {
            var u = url.Text.Trim();
            if (u.Length > 0 && GrafanaClient.ValidateUrl(u) is { } bad) { Dialogs.Error("Dashboards", "Grafana URL: " + bad + "."); return false; }
            int o = 0;
            if (org.Text.Trim().Length > 0 && (!int.TryParse(org.Text.Trim(), out o) || o < 0 || o > 1_000_000)) { Dialogs.Error("Dashboards", "The organisation id is a whole number (empty = the token's default)."); return false; }
            var ls = new List<string>();
            foreach (var raw in links.Text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                var (label, link) = ObservabilityView.ParseLink(raw);
                var why = label == null ? "write it as Label=https://…" : GrafanaClient.ValidateUrl(link);
                if (why != null) { Dialogs.Error("Dashboards", $"Link “{raw}”: {why}."); return false; }
                ls.Add(raw);
            }
            // 0.5.0: the Grafana address is admin-protected (ADR-059); it changes once approved.
            if (url.IsEnabled && u != _s.GrafanaUrl) _ = ProtectedSettings.ChangeAsync("change the Grafana address", "observe.grafanaUrl", u);
            if (org.IsEnabled) _s.GrafanaOrgId = o;
            if (links.IsEnabled) _s.ObserveLinks = ls;
            if (token.Password.Length > 0) { _s.ObserveSecrets["grafana"] = token.Password; token.Clear(); AuditLog.Write("you", "dashboards", "Grafana token changed"); }
            base_Save();
            return true;
        }
        async void Test()
        {
            if (!Save()) return;
            if (_s.GrafanaUrl.Length == 0) { status.Text = "Enter the Grafana URL first."; return; }
            status.Text = "Asking Grafana for its dashboards…";
            try
            {
                var list = await GrafanaClient.DashboardsAsync(_s.GrafanaUrl, _s.ObserveSecrets.GetValueOrDefault("grafana"), _s.GrafanaOrgId);
                status.Text = $"✓ {list.Count} dashboard(s)" + (list.Count > 0 ? ": " + string.Join(", ", list.Take(6).Select(d => d.Title)) + (list.Count > 6 ? "…" : "") : "");
            }
            catch (Exception ex) { status.Text = "✕ " + ex.Message; }
        }
        return Card("Dashboards & SIEM links",
            "Observability lists your Grafana dashboards (read-only, through a service-account token stored encrypted here) and opens them — and any link below (Kibana, OpenSearch Dashboards, Microsoft Sentinel, Splunk…) — in the app's own browser window. HTTPS only; offline mode applies.",
            Row(Labeled("GRAFANA URL", url), Labeled("ORG ID", org)),
            Labeled("GRAFANA SERVICE-ACCOUNT TOKEN (Viewer; stored encrypted, never shown again)", token),
            Labeled("LINKS (one per line: Label=https://…)", links),
            Row(Btn("Save", () => { if (Save()) status.Text = "Saved."; }, "PrimaryButton"), Btn("Test Grafana", Test), Btn("Open Observability", () => MainWindow.Instance.Navigate("observe"))),
            status);
    }

    private void base_Save() => Save();
}
