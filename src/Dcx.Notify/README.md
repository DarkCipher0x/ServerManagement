# Dcx.Notify

What leaves the app to tell someone (0.3.0 §3 and §4; 0.4.0 log sinks and dashboards, ADR-056). References only
`Dcx.Core`; no third-party packages.

| File | What |
|---|---|
| `Syslog/SyslogMessage.cs` | RFC 5424 formatting (structured data, escaping, masking) and RFC 5425 octet-counting framing; audit, error and (0.4.0) session-line messages |
| `Syslog/SyslogForwarder.cs` | The TLS-only connection (TLS 1.2/1.3, Windows trust or a SHA-256 pin, optional client certificate), the encrypted size-capped spool, back-off, offline mode, and the status the Privacy & network box shows |
| `Sinks/LogEvent.cs` | One event as a masked, bounded JSON document (ECS-like fields; PascalCase + `TimeGenerated` for Azure Monitor); round-trips through the spool |
| `Sinks/LogSink.cs` | A sink: kinds (Elasticsearch / OpenSearch, Grafana Loki, Azure Monitor / Sentinel, Splunk HEC, Graylog GELF, webhook), its rules (https only, bounded fields, the Azure ids), the list as JSON without secrets, the payload per kind, how a server's answer is judged |
| `Sinks/LogShipper.cs` | The queue with its encrypted spool, per-sink delivery and back-off, one pinned TLS `HttpClient` per sink on the offline-mode handler, per-sink status, the per-minute activity counters, the Azure Monitor token hook |
| `Grafana/GrafanaClient.cs` | The dashboard list through Grafana's search API (Bearer token, organisation header) and dashboard URLs that stay on the Grafana server |
| `Mail/SmtpSender.cs` | A small SMTP client: implicit TLS (465) or required STARTTLS (587), AUTH only inside TLS, header-injection-safe messages |
| `Mail/Notifier.cs` | Which events notify (incidents, host down, failed backups/snapshots, security events, syslog or a log sink failing), rate limit and summary |

Settings live in `AppSettings` (Syslog*, Observe*, Grafana*, Smtp*, Notify*); everything but the SMTP password and the
sink / Grafana secrets (`ObserveSecrets`) is admin-protected (`ProtectedSettings`, ADR-039) and a Group Policy (ADR-040).
Secrets never go to syslog, a sink or e-mail: every line is masked by `Redaction.Mask` first, and a sink's secret exists
only in a request header at send time.
