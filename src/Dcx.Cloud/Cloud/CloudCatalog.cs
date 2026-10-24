namespace DcxManager.Cloud;

/// <summary>IntSelect: a choice of whole numbers sent as a number (e.g. a contract period).</summary>
public enum FieldKind { Text, Multiline, Int, Bool, Select, Lookup, MultiLookup, Base64, List, Json, Password, IntSelect }

/// <summary>A selectable list fed by another API endpoint (regions, plans, OS images, instances...).
/// SortBy = a numeric field to order by (e.g. monthly cost) instead of the label.</summary>
public sealed record Lookup(string Path, string Key, string Value, params string[] Label)
{
    public string? SortBy { get; init; }
}

/// <summary>One input on a create/update/action form. Default supports {field} templates from the selected row.
/// InPath = the value goes into the action's path as {form.name}, not into the body.</summary>
public sealed record Field(string Name, string Label, FieldKind Kind = FieldKind.Text, bool Required = false, string? Default = null,
    string[]? Options = null, Lookup? Lookup = null, string? Hint = null, bool InPath = false);

/// <summary>
/// A button on a resource view. Path supports {field} (selected row), {parent.field} (row of the parent view) and
/// {form.field} (an InPath form field). Special = handled in code by the view (ssh, addhost) or the provider (console…).
/// Fixed = body values that are always sent (templates allowed). NoRow = works without a selected row.
/// When = states (of the resource's StatusField) in which the action is possible ("stopped,error"); otherwise it is shown
/// disabled with the reason. Await = after success, watch the item until StatusField reaches this state (bounded).
/// Idempotent = safe to repeat after a rate-limit answer (orders are not).
/// </summary>
public sealed record ActionDef(string Label, string Method, string Path, Field[]? Fields = null, string? Confirm = null,
    bool Danger = false, string? Special = null, bool ShowResult = false, Dictionary<string, string>? Fixed = null,
    bool NoRow = false, string? TypeToConfirm = null, string? Tip = null, string? When = null, string? Await = null, bool Idempotent = true);

public sealed record Col(string Key, string Header, double Width = 140);

public sealed class ResourceDef
{
    public required string Title { get; init; }
    public string Group { get; init; } = "Compute";
    public required string Path { get; init; }
    public string? Key { get; init; }
    public string IdField { get; init; } = "id";
    public string NameField { get; init; } = "label";
    public Col[] Columns { get; init; } = Array.Empty<Col>();
    public Field[]? Create { get; init; }
    /// <summary>Where a create is sent (default: Path). May use {form.field} of InPath create fields (Azure's
    /// ".../resourceGroups/{form.name}"); those fields then go into the path, not the body.</summary>
    public string? CreatePath { get; init; }
    /// <summary>POST (default) or PUT (Azure creates by PUT on the new item's path).</summary>
    public string CreateMethod { get; init; } = "POST";
    /// <summary>Asked before a create is sent (orders that are billed).</summary>
    public string? CreateConfirm { get; init; }
    public Field[]? Update { get; init; }
    public string UpdateMethod { get; init; } = "PATCH";
    public string? ItemPath { get; init; }
    public bool CanDelete { get; init; } = true;
    /// <summary>Deleting asks for the item's name to be typed (servers, databases, volumes, clusters).</summary>
    public bool TypeToDelete { get; init; }
    /// <summary>"field=value[,value]": rows where Delete is possible (e.g. only custom images).</summary>
    public string? DeleteWhen { get; init; }
    public ActionDef[] Actions { get; init; } = Array.Empty<ActionDef>();
    public (string Label, ResourceDef Def)[] Children { get; init; } = Array.Empty<(string, ResourceDef)>();
    public bool Single { get; init; }
    public bool ReadOnly { get; init; }
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "▪";
    /// <summary>The field holding the resource's state, for When / Await (e.g. "status", "power_status").</summary>
    public string? StatusField { get; init; }
    /// <summary>Instances: rows that Normalize into a <see cref="CloudInstance"/> (SSH, add to hosts, copy IP).</summary>
    public bool IsInstance { get; init; }

    /// <summary>A child screen (its list needs a parent row): only reachable from its parent.</summary>
    public bool IsChild => Path.Contains("{parent.", StringComparison.Ordinal) || Path.Contains("{path:parent.", StringComparison.Ordinal);

    /// <summary>Path of one item: ItemPath or Path/{id}.</summary>
    public string ItemTemplate => ItemPath ?? (Path.Split('?')[0] + "/{" + IdField + "}");

    /// <summary>Whether a row is in one of the states of a "field=a,b" / "a,b" (StatusField) condition.</summary>
    public bool Allows(string? condition, System.Text.Json.JsonElement row)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        string field, values;
        int eq = condition.IndexOf('=');
        if (eq > 0) { field = condition[..eq]; values = condition[(eq + 1)..]; }
        else { field = StatusField ?? "status"; values = condition; }
        var v = J.Str(row, field);
        return values.Split(',', StringSplitOptions.TrimEntries).Any(x => x.Equals(v, StringComparison.OrdinalIgnoreCase));
    }
}
