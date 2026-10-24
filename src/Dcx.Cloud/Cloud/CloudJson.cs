using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DcxManager.Cloud;

/// <summary>Helpers for reading cloud API JSON in views.</summary>
public static class J
{
    /// <summary>Value at a dotted path ("region", "ipConfig.v4.ip"), formatted for display.</summary>
    public static string Str(JsonElement e, string path)
    {
        var v = Get(e, path);
        return v.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => "",
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ValueKind == JsonValueKind.Object ? Brief(x) : x.GetRawText())),
            _ => Brief(v),
        };
    }

    public static JsonElement Get(JsonElement e, string path)
    {
        var cur = e;
        foreach (var part in path.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out cur)) return default;
        }
        return cur;
    }

    private static string Brief(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return o.GetRawText();
        foreach (var k in new[] { "label", "name", "displayName", "id", "description" })
            if (o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
        var s = o.GetRawText();
        return s.Length > 120 ? s[..120] + "…" : s;
    }

    public static string Pretty(JsonElement e) =>
        e.ValueKind == JsonValueKind.Undefined ? "(no content)" : JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>A field named "properties.priority" fills {"properties": {"priority": …}} (Azure's and Google's nested
    /// bodies); a plain name is a top-level key.</summary>
    public static void SetPath(JsonObject body, string name, JsonNode? value)
    {
        var parts = name.Split('.');
        var cur = body;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is not JsonObject next) { next = new JsonObject(); cur[parts[i]] = next; }
            cur = next;
        }
        cur[parts[^1]] = value;
    }

    /// <summary>The value at a dotted path of a form body (the inverse of <see cref="SetPath"/>); a flat key wins.</summary>
    public static JsonNode? GetPath(JsonObject? body, string name)
    {
        if (body == null) return null;
        if (body[name] is { } flat) return flat;
        JsonNode? cur = body;
        foreach (var part in name.Split('.'))
        {
            if (cur is not JsonObject o) return null;
            cur = o[part];
        }
        return cur;
    }
}

/// <summary>A row of any cloud list; columns bind to "[path]" through the indexer.</summary>
public sealed class JRow(JsonElement raw)
{
    public JsonElement Raw { get; } = raw;
    public string this[string key] => J.Str(Raw, key);
}

public sealed record LookupItem(string Label, JsonNode? Value, JsonElement Raw)
{
    public override string ToString() => Label;
}

/// <summary>Turns {field}, {parent.field}, {form.field} and {now} into values. In URLs every value is escaped, so a
/// value can never add a path segment or query parameter of its own. {path:field} is the one exception, for providers
/// whose ids are resource paths (Azure's "/subscriptions/…/virtualMachines/vm1", Google's relative "_path"): the value is
/// split on "/" and each segment escaped on its own, so it can add path segments but never a query, a fragment, an
/// encoded separator or a "." / ".." segment (such a value becomes empty and the request fails safely).</summary>
public static class Tpl
{
    /// <summary>A resource path made safe for a URL (see the class remarks), or "" when it isn't a plain path.</summary>
    public static string SafePath(string value)
    {
        if (value.Length == 0 || value.Length > 2048) return "";
        var segments = value.Split('/');
        if (segments.Skip(1).Any(s => s is "" or "." or "..") || segments[0].Length > 0 && segments[0] is "." or "..") return "";
        return string.Join("/", segments.Select(Uri.EscapeDataString));
    }

    public static string Fill(string template, JsonElement? row, JsonElement? parent, bool url = false, JsonObject? form = null)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            int open = template.IndexOf('{', i);
            if (open < 0) { sb.Append(template, i, template.Length - i); break; }
            int close = template.IndexOf('}', open);
            if (close < 0) { sb.Append(template, i, template.Length - i); break; }
            sb.Append(template, i, open - i);
            var key = template[(open + 1)..close];
            if (key.StartsWith("path:", StringComparison.Ordinal))
            {
                var field = key[5..];
                var raw = field.StartsWith("parent.", StringComparison.Ordinal) ? (parent is { } pp ? J.Str(pp, field[7..]) : "") : row is { } pr ? J.Str(pr, field) : "";
                sb.Append(url ? SafePath(raw) : raw);
                i = close + 1;
                continue;
            }
            string value = key switch
            {
                "now" => DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                _ when key.StartsWith("parent.") => parent is { } p ? J.Str(p, key[7..]) : "",
                _ when key.StartsWith("form.") => J.GetPath(form, key[5..]) is JsonValue fv ? (fv.TryGetValue<string>(out var fs) ? fs : fv.ToJsonString()) : "",
                // {json:field}: the field as raw JSON (to prefill a JSON editor with an object or a list)
                _ when key.StartsWith("json:") => row is { } jr && J.Get(jr, key[5..]) is { ValueKind: not JsonValueKind.Undefined } jv ? jv.GetRawText() : "",
                _ => row is { } r ? J.Str(r, key) : "",
            };
            sb.Append(url ? Uri.EscapeDataString(value) : value);
            i = close + 1;
        }
        return sb.ToString();
    }
}
