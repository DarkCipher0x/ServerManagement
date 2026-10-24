using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Cloud;

/// <summary>A form generated from <see cref="Field"/> definitions for any cloud provider. Returns the JSON body to send.</summary>
public sealed class CloudFormWindow : Window
{
    private readonly Field[] _fields;
    private readonly JsonElement? _row, _parent;
    private readonly ICloudApi _client;
    private readonly Dictionary<string, FrameworkElement> _inputs = new();
    private readonly Dictionary<string, List<LookupItem>> _lookupAll = new();
    private readonly bool _isUpdate;

    public JsonObject? Body { get; private set; }

    public CloudFormWindow(string title, string intro, Field[] fields, ICloudApi client, JsonElement? row, JsonElement? parent, bool isUpdate, string action = "Create")
    {
        _fields = fields; _row = row; _parent = parent; _client = client; _isUpdate = isUpdate;
        Title = title;
        SizeToContent = SizeToContent.Height;
        Typography.Size(this, 640);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip;
        this.Fs(FontStep.Control);
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Fg");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        try { Owner = Dialogs.Owner; } catch { }

        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 8) };
        form.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("H1") });
        if (intro.Length > 0) form.Children.Add(new TextBlock { Text = intro, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new Border { Height = 12 });

        foreach (var f in fields)
        {
            form.Children.Add(new TextBlock { Text = f.Label.ToUpperInvariant() + (f.Required ? "  *" : ""), Style = (Style)FindResource("Label") });
            var input = Build(f);
            input.Margin = new Thickness(0, 0, 0, f.Hint != null ? 2 : 10);
            form.Children.Add(input);
            if (f.Hint != null) form.Children.Add(new TextBlock { Text = f.Hint, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Caption));
            _inputs[f.Name] = input;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 8, 20, 16) };
        var ok = new Button { Content = action, Style = (Style)FindResource("PrimaryButton"), IsDefault = true, MinWidth = 110 };
        var preview = new Button { Content = "Preview JSON", ToolTip = "Show the exact request body" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        buttons.Children.Add(preview); buttons.Children.Add(ok); buttons.Children.Add(cancel);
        ok.Click += (_, _) => { if (Collect() is { } b) { Body = b; DialogResult = true; } };
        preview.Click += (_, _) => { if (Collect() is { } b) Dialogs.ShowText("Request body", Masked(b).ToJsonString(new JsonSerializerOptions { WriteIndented = true }), modal: true); };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Typography.Track(this, root);   // UI scale + text size (ADR-015)
        Loaded += async (_, _) => await LoadLookupsAsync();
    }

    /// <summary>The preview never shows what was typed into password fields.</summary>
    private JsonObject Masked(JsonObject b)
    {
        var copy = (JsonObject)b.DeepClone();
        foreach (var f in _fields.Where(f => f.Kind == FieldKind.Password && copy.ContainsKey(f.Name))) copy[f.Name] = "••••••••";
        return copy;
    }

    private string Initial(Field f) => f.Default == null ? "" : Tpl.Fill(f.Default, _row, _parent);

    private FrameworkElement Build(Field f)
    {
        switch (f.Kind)
        {
            case FieldKind.Bool:
                return new CheckBox { IsChecked = Initial(f) is "true" or "yes" };
            case FieldKind.Select or FieldKind.IntSelect:
                var cb = new ComboBox { ItemsSource = f.Options };
                cb.SelectedItem = f.Options?.FirstOrDefault(o => o == Initial(f)) ?? f.Options?.FirstOrDefault();
                return cb;
            case FieldKind.Lookup:
                var lc = new ComboBox { IsTextSearchEnabled = true, MaxDropDownHeight = 420 };
                lc.Items.Add("loading…"); lc.SelectedIndex = 0;
                if (f.Name == "region") lc.SelectionChanged += (_, _) => FilterByRegion();
                return lc;
            case FieldKind.MultiLookup:
                return new ListBox { Height = 120, SelectionMode = SelectionMode.Multiple };
            case FieldKind.Password:
                return new PasswordBox();
            case FieldKind.Multiline or FieldKind.Base64 or FieldKind.Json:
                return new TextBox
                {
                    Text = f.Kind == FieldKind.Json && f.Default != null ? PrettyJson(Initial(f)) : Initial(f),
                    AcceptsReturn = true, AcceptsTab = true, Height = f.Kind == FieldKind.Multiline ? 90 : 150,
                    TextWrapping = f.Kind == FieldKind.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, Padding = new Thickness(5, 4, 5, 4),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                }.Fs(FontStep.Meta).Mono();
            default:
                var tb = new TextBox { Text = Initial(f), Padding = new Thickness(5, 4, 5, 4) };
                return f.Kind == FieldKind.Int ? tb.Mono() : tb;
        }
    }

    private static string PrettyJson(string s)
    {
        try { return JsonNode.Parse(s)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? s; } catch { return s; }
    }

    private static double Num(JsonElement e, string field) => J.Get(e, field) is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : double.TryParse(J.Str(e, field), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    private async Task LoadLookupsAsync()
    {
        foreach (var f in _fields.Where(f => f.Lookup != null))
        {
            var lk = f.Lookup!;
            List<LookupItem> items;
            try
            {
                var raw = await _client.ListAsync(Tpl.Fill(lk.Path, _row, _parent, url: true), lk.Key);
                items = raw.Select(e => ToItem(e, lk)).OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase).ToList();
                if (lk.SortBy != null) items = items.OrderBy(i => Num(i.Raw, lk.SortBy)).ToList();
            }
            catch (Exception ex)
            {
                if (_inputs[f.Name] is ComboBox c0) { c0.Items.Clear(); c0.Items.Add("✕ " + ex.Message.Split('\n')[0]); c0.SelectedIndex = 0; }
                continue;
            }
            _lookupAll[f.Name] = items;
            var current = Initial(f);
            if (_inputs[f.Name] is ComboBox cb)
            {
                cb.Items.Clear();
                if (!f.Required || _isUpdate) cb.Items.Add("(none)");
                foreach (var i in items) cb.Items.Add(i);
                cb.SelectedItem = items.FirstOrDefault(i => current.Length > 0 && ValueText(i.Value) == current) ?? cb.Items[0];
                TextSearch.SetTextPath(cb, "Label");
            }
            else if (_inputs[f.Name] is ListBox lb)
            {
                var chosen = current.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                lb.Items.Clear();
                foreach (var i in items)
                {
                    var item = new ListBoxItem { Content = i.Label, Tag = i, Padding = new Thickness(4, 2, 4, 2) };
                    lb.Items.Add(item);
                    if (chosen.Contains(ValueText(i.Value))) item.IsSelected = true;
                }
            }
        }
        FilterByRegion();
    }

    /// <summary>Plans list only the ones sold in the chosen region (from each plan's "locations").</summary>
    private void FilterByRegion()
    {
        if (!_inputs.TryGetValue("region", out var r) || r is not ComboBox { SelectedItem: LookupItem region }) return;
        var rid = ValueText(region.Value);
        foreach (var (name, all) in _lookupAll)
        {
            if (name == "region" || _inputs[name] is not ComboBox cb) continue;
            if (!all.Any(i => J.Get(i.Raw, "locations").ValueKind == JsonValueKind.Array)) continue;
            var keep = cb.SelectedItem;
            cb.Items.Clear();
            var f = _fields.First(x => x.Name == name);
            if (!f.Required || _isUpdate) cb.Items.Add("(none)");
            foreach (var i in all.Where(i => J.Get(i.Raw, "locations").EnumerateArray().Any(l => l.GetString() == rid))) cb.Items.Add(i);
            cb.SelectedItem = cb.Items.Contains(keep) ? keep : cb.Items.Count > 0 ? cb.Items[0] : null;
        }
    }

    private static LookupItem ToItem(JsonElement e, Lookup lk)
    {
        if (e.ValueKind != JsonValueKind.Object)   // e.g. Kubernetes versions: an array of strings
            return new LookupItem(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText(), JsonNode.Parse(e.GetRawText()), e);
        var parts = lk.Label.Select(l =>
        {
            var s = J.Str(e, l);
            return l switch
            {
                "ram" when s.Length > 0 => $"{s} MB RAM",
                "disk" when s.Length > 0 => $"{s} GB disk",
                "vcpu_count" or "cpu_count" when s.Length > 0 => $"{s} vCPU",
                "monthly_cost" when s.Length > 0 => $"${s}/mo",
                "locations" => "",
                _ => s,
            };
        }).Where(s => s.Length > 0);
        var value = J.Get(e, lk.Value);
        return new LookupItem(string.Join("  ·  ", parts), value.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(value.GetRawText()), e);
    }

    private static string ValueText(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : n?.ToJsonString() ?? "";

    private JsonObject? Collect()
    {
        var body = new JsonObject();
        foreach (var f in _fields)
        {
            var input = _inputs[f.Name];
            JsonNode? value = null;
            string text = "";
            switch (input)
            {
                case CheckBox c:
                    value = JsonValue.Create(c.IsChecked == true);
                    break;
                case PasswordBox p:
                    text = p.Password;
                    if (text.Length > 0) value = JsonValue.Create(text);
                    break;
                case ComboBox cb when f.Kind == FieldKind.IntSelect:
                    text = cb.SelectedItem as string ?? "";
                    if (text.Length > 0 && long.TryParse(text, out var iv)) value = JsonValue.Create(iv);
                    break;
                case ComboBox cb when f.Kind == FieldKind.Select:
                    text = cb.SelectedItem as string ?? "";
                    if (text.Length > 0) value = JsonValue.Create(text);
                    break;
                case ComboBox cb:
                    if (cb.SelectedItem is LookupItem li) { value = li.Value?.DeepClone(); text = ValueText(li.Value); }
                    break;
                case ListBox lb:
                    var arr = new JsonArray(lb.SelectedItems.OfType<ListBoxItem>().Select(i => ((LookupItem)i.Tag).Value?.DeepClone()).ToArray());
                    if (arr.Count > 0) { value = arr; text = "x"; }
                    break;
                case TextBox tb:
                    text = tb.Text;
                    if (text.Trim().Length == 0) break;
                    switch (f.Kind)
                    {
                        case FieldKind.Int:
                            if (!long.TryParse(text.Trim(), out var n)) { Dialogs.Error(Title, $"{f.Label} must be a whole number."); return null; }
                            value = JsonValue.Create(n);
                            break;
                        case FieldKind.Base64:
                            value = JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));
                            break;
                        case FieldKind.List:
                            value = new JsonArray(text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
                            break;
                        case FieldKind.Json:
                            try { value = JsonNode.Parse(text); }
                            catch (JsonException ex) { Dialogs.Error(Title, $"{f.Label} isn't valid JSON: {ex.Message}"); return null; }
                            break;
                        case FieldKind.Multiline:
                            value = JsonValue.Create(text.Replace("\r\n", "\n"));
                            break;
                        default:
                            value = JsonValue.Create(text.Trim());
                            break;
                    }
                    break;
            }
            if (value == null)
            {
                if (f.Required && !_isUpdate) { Dialogs.Error(Title, $"{f.Label} is required."); return null; }
                continue;
            }
            J.SetPath(body, f.Name, value);
        }
        return body;
    }
}
