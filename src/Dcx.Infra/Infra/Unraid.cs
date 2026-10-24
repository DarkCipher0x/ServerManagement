using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Infra;

/// <summary>
/// Unraid through its official GraphQL API (Unraid 7.x; API key with a role, header <c>x-api-key</c>). Capability
/// discovery comes first: the schema is introspected and the app only requests fields and offers mutations that this
/// server's API version actually has (docker start/stop/restart/pause/unpause, VM start/stop/reboot/pause/resume/force-stop/
/// reset when present; ADR-031). Array, disk
/// and share operations are deliberately NOT offered (disruptive; out of scope). Every change is verified by re-reading
/// the container/VM. The API key's role decides what the server accepts; a refusal is reported as such.
/// </summary>
public sealed class UnraidProvider : IInfraProvider, ITlsPinned
{
    private readonly ProviderConfig _c;
    private readonly HttpClient _http;
    private Schema? _schema;

    public ProviderConfig Config => _c;
    public string Where => _c.Endpoint;
    public NetClass? NetworkClass => NetworkPolicy.Classify(_c.Endpoint);
    public string LastSeenSha256 { get; private set; } = "";
    public TlsDecision LastTls { get; private set; } = TlsDecision.Accept;
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromSeconds(120);
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(2);

    public UnraidProvider(ProviderConfig c, HttpMessageHandler? handler = null)
    {
        _c = c;
        handler ??= TlsPin.Handler(() => _c.TlsSha256, (sha, d) => { LastSeenSha256 = sha; LastTls = d; });
        // 0.6.17 (ADR-096): the key is added when each request is sent (it may be kept in Bitwarden or Password Safe).
        handler = new CredentialAuthHandler(() => _c.CredentialId, "Unraid " + c.Name, (req, cred) =>
        {
            if (cred.Secret.Length > 0) req.Headers.TryAddWithoutValidation("x-api-key", cred.Secret);
        }, handler);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>https anywhere; plain http only to a local / on-premises address (Unraid's default), with a warning note.</summary>
    public static string? ValidateEndpoint(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return "Enter the Unraid address, e.g. https://tower.lan";
        if (u.Scheme == "http" && NetworkPolicy.Classify(url) is not (NetClass.Local or NetClass.OnPremises))
            return "Plain http is only allowed for an address on your own network; use https (the API key would otherwise cross the internet in clear text).";
        return null;
    }

    private string Gql => _c.Endpoint.Trim().TrimEnd('/') + "/graphql";

    // ---- GraphQL plumbing -------------------------------------------------------------------------------------------

    /// <summary>One GraphQL POST. A GraphQL answer is returned whatever the HTTP status (Apollo answers validation errors
    /// with 400); a key refusal or redirect throws; any other non-GraphQL failure throws with the status.</summary>
    private async Task<JsonObject> PostRaw(string query, JsonObject? variables, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query };
        if (variables != null) body["variables"] = variables;
        using var req = new HttpRequestMessage(HttpMethod.Post, Gql) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var r = await _http.SendAsync(req, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("the API key was rejected (check it under Settings → Management Access → API Keys)");
        if ((int)r.StatusCode is >= 300 and < 400) throw new InvalidOperationException("the server redirected (e.g. http → https); use the address it redirects to");
        JsonObject? j = null;
        try { j = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) when (!r.IsSuccessStatusCode) { }
        if (!r.IsSuccessStatusCode && j?["errors"] is not JsonArray) throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        return j ?? throw new InvalidOperationException("not a GraphQL response");
    }

    private static List<string> Errors(JsonObject j) =>
        (j["errors"] as JsonArray)?.Select(e => e?["message"]?.ToString()).OfType<string>().ToList() ?? new List<string>();

    /// <summary>True when the server refused the schema query because introspection is switched off (the default on a
    /// production Unraid API).</summary>
    public static bool IntrospectionRefused(JsonObject j) =>
        j["errors"] is JsonArray errs && errs.Any(e => e?["extensions"]?["code"]?.ToString() == "INTROSPECTION_DISABLED" || (e?["message"]?.ToString() ?? "").Contains("introspection", StringComparison.OrdinalIgnoreCase));

    private async Task<JsonObject> Post(string query, JsonObject? variables, CancellationToken ct)
    {
        var j = await PostRaw(query, variables, ct);
        if (j["errors"] is JsonArray { Count: > 0 } errs)
        {
            var msg = string.Join("; ", errs.Select(e => e?["message"]?.ToString()).Where(m => m != null).Take(3));
            if (j["data"] is not JsonObject) throw new InvalidOperationException(msg.Contains("orbidden", StringComparison.Ordinal) || msg.Contains("ermission") ? "the API key's role doesn't allow this: " + msg : msg);
            j["dcxErrors"] = msg;   // partial data: keep what came back, report the rest
        }
        return j;
    }

    public const string IntrospectionQuery =
        "query { __schema { queryType { name } mutationType { name } types { name kind fields { name args { name type { name kind ofType { name kind ofType { name kind } } } } " +
        "type { name kind ofType { name kind ofType { name kind ofType { name kind } } } } } } } }";

    /// <summary>The introspected schema: type → field → (unwrapped type name, kind, args with GraphQL type strings).</summary>
    public sealed class Schema
    {
        public string Query = "Query", Mutation = "";
        public readonly Dictionary<string, Dictionary<string, (string Type, string Kind, Dictionary<string, string> Args)>> Types = new();

        public static Schema From(JsonObject data)
        {
            // A server's introspection answer is untrusted input: missing names and nulls skip that entry; duplicate keys and
            // wrongly shaped nodes make the whole answer invalid instead of throwing NullReference (fuzzing, s23).
            try { return Build(data); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { throw new System.IO.InvalidDataException("The API's schema answer is malformed: " + ex.Message); }
        }

        private static Schema Build(JsonObject data)
        {
            var s = new Schema();
            if (data["__schema"] is not JsonObject sch) throw new System.IO.InvalidDataException("The API returned no schema.");
            s.Query = sch["queryType"]?["name"]?.ToString() ?? "Query";
            s.Mutation = sch["mutationType"]?["name"]?.ToString() ?? "";
            foreach (var t in (sch["types"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                if (t["fields"] is not JsonArray fields || t["name"]?.ToString() is not { } tn || tn.StartsWith("__")) continue;
                var map = new Dictionary<string, (string, string, Dictionary<string, string>)>();
                foreach (var f in fields.OfType<JsonObject>())
                {
                    if (f["name"]?.ToString() is not { Length: > 0 } fname) continue;
                    var (name, kind) = Unwrap(f["type"]);
                    var args = new Dictionary<string, string>();
                    foreach (var a in (f["args"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                        if (a["name"]?.ToString() is { Length: > 0 } an) args[an] = TypeString(a["type"]);
                    map[fname] = (name, kind, args);
                }
                s.Types[tn] = map;
            }
            return s;
        }

        private static (string, string) Unwrap(JsonNode? t)
        {
            for (int depth = 0; t is JsonObject && t["name"] == null && depth < 20; depth++) t = t["ofType"];
            if (t is not JsonObject) return ("", "");
            return (t?["name"]?.ToString() ?? "", t?["kind"]?.ToString() ?? "");
        }

        /// <summary>GraphQL type string for a variable declaration, e.g. "PrefixedID!".</summary>
        public static string TypeString(JsonNode? t) => t?["kind"]?.ToString() switch
        {
            "NON_NULL" => TypeString(t["ofType"]) + "!",
            "LIST" => "[" + TypeString(t["ofType"]) + "]",
            _ => t?["name"]?.ToString() ?? "String",
        };

        /// <summary>Type reached by following field names from the query root (null when any step is missing).</summary>
        public string? Resolve(string root, params string[] path)
        {
            string type = root;
            foreach (var p in path)
            {
                if (!Types.TryGetValue(type, out var fs) || !fs.TryGetValue(p, out var f)) return null;
                type = f.Type;
            }
            return type;
        }

        public bool HasField(string type, string field) => Types.TryGetValue(type, out var fs) && fs.ContainsKey(field);

        /// <summary>The scalar/enum fields of <paramref name="type"/> that are in <paramref name="wanted"/>, as a selection.</summary>
        public string Pick(string type, params string[] wanted) =>
            Types.TryGetValue(type, out var fs) ? string.Join(" ", wanted.Where(w => fs.TryGetValue(w, out var f) && f.Kind is "SCALAR" or "ENUM")) : "";
    }

    // ---- discovery --------------------------------------------------------------------------------------------------

    /// <summary>What this server's API offers (also used by the self-test).</summary>
    public sealed record Discovered(bool Info, bool Array, bool Docker, bool Vms, IReadOnlyList<string> DockerMutations, IReadOnlyList<string> VmMutations);

    public Discovered Discover(Schema s)
    {
        string? Mut(string root) => s.Mutation.Length > 0 ? s.Resolve(s.Mutation, root) : null;
        IReadOnlyList<string> Ops(string? type, params string[] names) =>
            type == null ? Array.Empty<string>() : names.Where(n => s.Types.TryGetValue(type, out var fs) && fs.TryGetValue(n, out var f) && f.Args.ContainsKey("id")).ToList();
        return new Discovered(
            s.HasField(s.Query, "info"), s.HasField(s.Query, "array"),
            s.Resolve(s.Query, "docker", "containers") != null, VmListField(s) != null,
            Ops(Mut("docker"), DockerOps), Ops(Mut("vm"), VmOps));
    }

    /// <summary>The id-only mutations of the official Unraid API schema (unraid/api generated-schema.graphql, 2026-10) that
    /// the app offers when this server has them (ADR-031).</summary>
    public static readonly string[] DockerOps = { "start", "stop", "restart", "pause", "unpause" };
    public static readonly string[] VmOps = { "start", "stop", "reboot", "forceStop", "pause", "resume", "reset" };

    private static string? VmListField(Schema s) => new[] { "domains", "domain" }.FirstOrDefault(f => s.Resolve(s.Query, "vms", f) != null);

    private Discovered? _found;

    // ---- discovery without introspection ----------------------------------------------------------------------------
    // A production Unraid API refuses __schema. The app then sends the selection it would like (the known Unraid API
    // fields) and lets the server's own validation errors say which fields don't exist, removing those until the query
    // validates; that surviving selection becomes a synthetic Schema, so everything downstream is unchanged. Mutations
    // are probed WITHOUT their required $id variable: validation runs first, and a missing non-null variable fails
    // before any resolver, so nothing on the server can change.

    private sealed class Sel(string name, params Sel[] kids)
    {
        public readonly string Name = name;
        public readonly List<Sel> Kids = kids.ToList();
        public int Col;
    }

    private static Sel[] Leaves(params string[] names) => names.Select(n => new Sel(n)).ToArray();

    /// <summary>Everything the app would like to read, as the known Unraid API names it.</summary>
    private static List<Sel> Wanted()
    {
        Sel Disks(string g) => new(g, Leaves("id", "name", "device", "size", "status", "temp", "type"));
        return new List<Sel>
        {
            new("info", new Sel("os", Leaves("hostname", "distro", "release", "uptime")), new Sel("cpu", Leaves("brand", "cores", "threads")),
                new Sel("memory", Leaves("total", "free", "used")), new Sel("versions", new Sel("core", Leaves("unraid")))),
            new("array", new Sel("state"), new Sel("capacity", new Sel("kilobytes", Leaves("free", "used", "total"))), Disks("parities"), Disks("disks"), Disks("caches")),
            new("docker", new Sel("containers", Leaves("id", "names", "image", "state", "status", "autoStart"))),
            new("vms", new Sel("domains", Leaves("id", "uuid", "name", "state")), new Sel("domain", Leaves("id", "uuid", "name", "state"))),
        };
    }

    /// <summary>One-line query; each field's 1-based column is recorded so a validation error's location names it exactly.</summary>
    private static string Render(List<Sel> root)
    {
        var sb = new StringBuilder("query {");
        void Walk(List<Sel> sels)
        {
            foreach (var s in sels)
            {
                sb.Append(' '); s.Col = sb.Length + 1; sb.Append(s.Name);
                if (s.Kids.Count > 0) { sb.Append(" {"); Walk(s.Kids); sb.Append(" }"); }
            }
        }
        Walk(root);
        return sb.Append(" }").ToString();
    }

    private static IEnumerable<(Sel Node, List<Sel> Parent)> All(List<Sel> sels)
    {
        foreach (var s in sels)
        {
            yield return (s, sels);
            foreach (var x in All(s.Kids)) yield return x;
        }
    }

    /// <summary>Removes objects whose every child was removed. Leaves are recognised by never having had children.</summary>
    private static void Prune(List<Sel> sels, HashSet<Sel> objects)
    {
        foreach (var s in sels) Prune(s.Kids, objects);
        sels.RemoveAll(s => objects.Contains(s) && s.Kids.Count == 0);
    }

    private async Task<Schema> ProbeSchemaAsync(CancellationToken ct)
    {
        var root = Wanted();
        var objects = All(root).Where(x => x.Node.Kids.Count > 0).Select(x => x.Node).ToHashSet();
        for (int round = 0; ; round++)
        {
            if (root.Count == 0) throw new InvalidOperationException("the API offers none of the Unraid fields this app reads (is this the Unraid GraphQL API?)");
            var j = await PostRaw(Render(root), null, ct);
            if (j["data"] is JsonObject && Errors(j).Count == 0) break;
            if (round >= 12) throw new InvalidOperationException("the API kept refusing the probe query: " + string.Join("; ", Errors(j).Take(3)));
            bool changed = false;
            foreach (var e in (j["errors"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var msg = e["message"]?.ToString() ?? "";
                (Sel Node, List<Sel> Parent)? hit = null;
                if (e["locations"] is JsonArray { Count: > 0 } locs && int.TryParse(locs[0]?["column"]?.ToString(), out var col))
                    hit = All(root).Where(x => x.Node.Col == col).Select(x => ((Sel, List<Sel>)?)x).FirstOrDefault();
                else if (e["path"] is JsonArray { Count: > 0 } path)   // an execution error (e.g. VMs disabled): drop that top-level section
                    hit = root.Where(s => s.Name == path[0]?.ToString()).Select(s => ((Sel, List<Sel>)?)(s, root)).FirstOrDefault();
                if (hit is not { } h) continue;
                if (msg.Contains("must not have a selection", StringComparison.Ordinal)) { if (h.Node.Kids.Count > 0) { h.Node.Kids.Clear(); objects.Remove(h.Node); changed = true; } }
                else if (h.Parent.Remove(h.Node)) changed = true;
            }
            if (!changed)
            {
                // Validation passed but a section failed while running: keep the answer's sections that came back.
                if (j["data"] is JsonObject) break;
                throw new InvalidOperationException(string.Join("; ", Errors(j).Take(3)) is { Length: > 0 } m ? m : "the API refused the probe query");
            }
            Prune(root, objects);
        }

        var s = new Schema { Query = "Query" };
        void Add(string type, List<Sel> sels)
        {
            var map = new Dictionary<string, (string, string, Dictionary<string, string>)>();
            foreach (var x in sels)
            {
                if (objects.Contains(x)) { map[x.Name] = (type + "." + x.Name, "OBJECT", new()); Add(type + "." + x.Name, x.Kids); }
                else map[x.Name] = ("", "SCALAR", new());
            }
            s.Types[type] = map;
        }
        Add("Query", root);

        var mutation = new Dictionary<string, (string, string, Dictionary<string, string>)>();
        foreach (var (rootName, ops) in new[] { ("docker", DockerOps), ("vm", VmOps) })
        {
            var opMap = new Dictionary<string, (string, string, Dictionary<string, string>)>();
            foreach (var op in ops)
            {
                var found = await ProbeMutationAsync(rootName, op, ct);
                if (found == null) continue;
                var (isObject, idType) = found.Value;
                var ret = $"Mutation.{rootName}.{op}";
                if (isObject) s.Types[ret] = new();
                opMap[op] = (isObject ? ret : "", isObject ? "OBJECT" : "SCALAR", new Dictionary<string, string> { ["id"] = idType });
            }
            if (opMap.Count == 0) continue;
            s.Types["Mutation." + rootName] = opMap;
            mutation[rootName] = ("Mutation." + rootName, "OBJECT", new());
        }
        if (mutation.Count > 0) { s.Mutation = "Mutation"; s.Types["Mutation"] = mutation; }
        return s;
    }

    /// <summary>Whether <c>mutation { root { op(id:) } }</c> exists, sent WITHOUT the required $id so it can never run.
    /// Returns (returns an object, the id's GraphQL type) or null.</summary>
    private async Task<(bool, string)?> ProbeMutationAsync(string root, string op, CancellationToken ct)
    {
        string idType = "PrefixedID!", sel = "";
        var fallbacks = new Queue<string>(new[] { "ID!", "String!" });
        for (int i = 0; i < 6; i++)
        {
            var j = await PostRaw($"mutation($id: {idType}) {{ {root} {{ {op}(id: $id){sel} }} }}", null, ct);
            var msgs = Errors(j);
            if (msgs.Count == 0) return null;   // can't happen with a missing non-null variable; refuse to guess
            string all = string.Join("\n", msgs);
            if (msgs.Any(m => m.Contains("was not provided", StringComparison.Ordinal))) return (sel.Length > 0, idType);
            if (all.Contains("Cannot query field", StringComparison.Ordinal) || all.Contains("Unknown argument", StringComparison.Ordinal) || all.Contains("not configured for mutations", StringComparison.Ordinal)) return null;
            if (all.Contains("must have a selection of subfields", StringComparison.Ordinal) && sel.Length == 0) { sel = " { __typename }"; continue; }
            if (all.Contains("must not have a selection", StringComparison.Ordinal) && sel.Length > 0) { sel = ""; continue; }
            if (Regex.Match(all, "expecting type \"([A-Za-z_][A-Za-z0-9_]*)!?\"") is { Success: true } want && want.Groups[1].Value + "!" != idType) { idType = want.Groups[1].Value + "!"; continue; }
            if (all.Contains("Unknown type", StringComparison.Ordinal) && fallbacks.Count > 0) { idType = fallbacks.Dequeue(); continue; }
            return null;
        }
        return null;
    }

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        if (ValidateEndpoint(_c.Endpoint) is { } bad) return ProviderCaps.Unreachable(bad);
        if (!ExternalCredentials.HasSecret(AppData.Current.FindCredential(_c.CredentialId)))
            return ProviderCaps.Unreachable("choose a vault credential holding the Unraid API key (as its password)");
        try
        {
            var notes = new List<string>();
            var intro = await PostRaw(IntrospectionQuery, null, ct);
            if (IntrospectionRefused(intro))
            {
                _schema = await ProbeSchemaAsync(ct);
                notes.Add("This server has schema introspection switched off (normal for a production Unraid API), so the app found what it offers by probing the known Unraid API fields instead. Nothing was changed while probing.");
            }
            else
            {
                if (intro["data"] is not JsonObject { } idata || idata["__schema"] is not JsonObject)
                    throw new InvalidOperationException(string.Join("; ", Errors(intro).Take(3)) is { Length: > 0 } m ? m : "the API returned no schema");
                _schema = Schema.From(idata);
            }
            var d = _found = Discover(_schema);
            if (_c.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) notes.Add("⚠ Plain http: the API key travels unencrypted on your network. Prefer https.");
            notes.Add($"API offers: {string.Join(", ", new[] { d.Info ? "server info" : null, d.Array ? "array/disks" : null, d.Docker ? "Docker" : null, d.Vms ? "VMs" : null }.Where(x => x != null))}" +
                      $" · changes: {(d.DockerMutations.Count + d.VmMutations.Count == 0 ? "none offered by this API version" : string.Join(", ", d.DockerMutations.Select(m => "docker " + m).Concat(d.VmMutations.Select(m => "vm " + m))))}.");
            notes.Add("Array, disk and share operations are not offered by this app (deliberately).");
            notes.Add("What the server allows depends on the API key's role (a VIEWER key is refused for changes).");
            string version = "";
            if (d.Info && _schema.Resolve(_schema.Query, "info", "versions", "core") is { } core && _schema.Pick(core, "unraid") is { Length: > 0 })
            {
                try { version = (await Post("query { info { versions { core { unraid } } } }", null, ct))["data"]?["info"]?["versions"]?["core"]?["unraid"]?.ToString() ?? ""; } catch { }
            }
            bool read = d.Info || d.Array || d.Docker || d.Vms;
            return new ProviderCaps(true, read, d.DockerMutations.Count + d.VmMutations.Count > 0, version, $"Unraid {(version.Length > 0 ? version : "(version not exposed)")} · GraphQL API", notes);
        }
        catch (HttpRequestException ex) when (LastTls != TlsDecision.Accept || ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return ProviderCaps.Unreachable(TlsPin.UntrustedMessage(LastTls, LastSeenSha256));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or InvalidOperationException or NullReferenceException or System.Text.Json.JsonException)
        {
            return ProviderCaps.Unreachable(ex switch
            {
                TaskCanceledException => "timed out",
                NullReferenceException or System.Text.Json.JsonException => "the server didn't answer like the Unraid GraphQL API (is the API enabled? Unraid 7.x or the Unraid Connect plugin is needed)",
                _ => ex.Message,
            });
        }
    }

    // ---- inventory --------------------------------------------------------------------------------------------------

    /// <summary>The inventory query, built only from fields the schema has.</summary>
    public string BuildInventoryQuery(Schema s, Discovered d)
    {
        var parts = new List<string>();
        if (d.Info)
        {
            var sub = new List<string>();
            foreach (var (field, wanted) in new[] { ("os", new[] { "hostname", "distro", "release", "uptime" }), ("cpu", new[] { "brand", "cores", "threads" }), ("memory", new[] { "total", "free", "used" }) })
                if (s.Resolve(s.Query, "info", field) is { } t && s.Pick(t, wanted) is { Length: > 0 } sel) sub.Add($"{field} {{ {sel} }}");
            if (sub.Count > 0) parts.Add($"info {{ {string.Join(" ", sub)} }}");
        }
        if (d.Array && s.Resolve(s.Query, "array") is { } at)
        {
            var sub = new List<string>();
            if (s.Pick(at, "state") is { Length: > 0 } st) sub.Add(st);
            if (s.Resolve(at, "capacity", "kilobytes") is { } kb && s.Pick(kb, "free", "used", "total") is { Length: > 0 } ksel) sub.Add($"capacity {{ kilobytes {{ {ksel} }} }}");
            foreach (var group in new[] { "parities", "disks", "caches" })
                if (s.Resolve(at, group) is { } dt && s.Pick(dt, "id", "name", "device", "size", "status", "temp", "type") is { Length: > 0 } dsel) sub.Add($"{group} {{ {dsel} }}");
            if (sub.Count > 0) parts.Add($"array {{ {string.Join(" ", sub)} }}");
        }
        if (d.Docker && s.Resolve(s.Query, "docker", "containers") is { } ct && s.Pick(ct, "id", "names", "image", "state", "status", "autoStart") is { Length: > 0 } csel)
            parts.Add($"docker {{ containers {{ {csel} }} }}");
        if (d.Vms && VmListField(s) is { } vf && s.Resolve(s.Query, "vms", vf) is { } vt && s.Pick(vt, "id", "uuid", "name", "state") is { Length: > 0 } vsel)
            parts.Add($"vms {{ {vf} {{ {vsel} }} }}");
        return "query { " + string.Join(" ", parts) + " }";
    }

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct)
    {
        if (_schema == null || _found == null) throw new InvalidOperationException("not connected");
        var j = await Post(BuildInventoryQuery(_schema, _found), null, ct);
        return Parse(j["data"] as JsonObject ?? new JsonObject(), _c.Name);
    }

    public static string NormalizeState(string kind, string? raw) => (raw ?? "").ToLowerInvariant() switch
    {
        "running" => "running",
        "exited" or "created" or "dead" => kind == "container" ? "exited" : "stopped",
        "paused" or "pmsuspended" => "paused",
        "shutoff" or "shutdown" or "nostate" or "crashed" => "stopped",
        var s => s.Length > 0 ? s : "unknown",
    };

    public static List<InfraObject> Parse(JsonObject data, string serverName)
    {
        var list = new List<InfraObject>();
        string S(JsonNode? n, string k) => n?[k]?.ToString() ?? "";
        static string Kb(string v) => long.TryParse(v, out var kb) && kb > 0 ? $"{kb / 1048576.0:0.#} GB" : "?";
        static string Bytes(string v) => long.TryParse(v, out var b) && b > 0 ? Fmt.Bytes(b) : "?";
        if (data["info"] is JsonObject info)
        {
            var os = info["os"]; var cpu = info["cpu"]; var mem = info["memory"];
            var host = S(os, "hostname") is { Length: > 0 } h ? h : serverName;
            list.Add(new InfraObject("server", "server", host, "online", "",
                $"{S(os, "distro")} {S(os, "release")}".Trim() + (cpu != null ? $" · {S(cpu, "brand")} {S(cpu, "cores")}c/{S(cpu, "threads")}t" : "") + (mem != null ? $" · RAM {Bytes(S(mem, "total"))}" : ""),
                new Dictionary<string, string>()));
        }
        if (data["array"] is JsonObject arr)
        {
            var kb = arr["capacity"]?["kilobytes"];
            list.Add(new InfraObject("array", "array", "Array", S(arr, "state").ToLowerInvariant() is { Length: > 0 } st ? st : "unknown", "",
                kb != null ? $"{Kb(S(kb, "used"))} used of {Kb(S(kb, "total"))} · {Kb(S(kb, "free"))} free" : "", new Dictionary<string, string>()));
            foreach (var group in new[] { "parities", "disks", "caches" })
                foreach (var d in (arr[group] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                {
                    var temp = S(d, "temp");
                    list.Add(new InfraObject(S(d, "id").Length > 0 ? S(d, "id") : group + ":" + S(d, "name"), "disk", S(d, "name"), S(d, "status").ToLowerInvariant(), group == "disks" ? "array" : group.TrimEnd('s'),
                        $"{(group == "parities" ? "parity" : group == "caches" ? "cache" : "data")} · {S(d, "device")} · {Kb(S(d, "size"))}" + (temp.Length > 0 && temp != "null" ? $" · {temp} °C" : ""), new Dictionary<string, string>()));
                }
        }
        foreach (var c in (data["docker"]?["containers"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var names = c["names"] is JsonArray na ? string.Join(", ", na.Select(n => n?.ToString().TrimStart('/'))) : S(c, "names").TrimStart('/');
            list.Add(new InfraObject(S(c, "id"), "container", names.Length > 0 ? names : S(c, "id"), NormalizeState("container", S(c, "state")), "docker",
                $"{S(c, "image")} · {S(c, "status")}" + (S(c, "autoStart") == "true" ? " · autostart" : ""), new Dictionary<string, string> { ["image"] = S(c, "image") }));
        }
        var vms = data["vms"] as JsonObject;
        foreach (var v in ((vms?["domains"] ?? vms?["domain"]) as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            list.Add(new InfraObject(S(v, "id").Length > 0 ? S(v, "id") : S(v, "uuid"), "vm", S(v, "name"), NormalizeState("vm", S(v, "state")), "vms", "virtual machine", new Dictionary<string, string>()));
        return list;
    }

    // ---- actions ----------------------------------------------------------------------------------------------------

    public static readonly InfraAction CStart = new("docker:start", "▶ Start", true, Consequence.None, "running", "Starts the container.");
    public static readonly InfraAction CStop = new("docker:stop", "■ Stop", true, Consequence.ServiceInterruption, "exited", "Stops the container. Whatever it serves goes down.");
    public static readonly InfraAction VStart = new("vm:start", "▶ Start", true, Consequence.None, "running", "Starts the VM.");
    public static readonly InfraAction VStop = new("vm:stop", "⏻ Shut down (graceful)", true, Consequence.ServiceInterruption, "stopped", "Asks the VM's OS to shut down.");
    public static readonly InfraAction VReboot = new("vm:reboot", "↻ Reboot", true, Consequence.ServiceInterruption, "running", "Asks the VM's OS to reboot.");
    public static readonly InfraAction VForce = new("vm:forceStop", "■ Force stop (power-off)", true, Consequence.DataLoss, "stopped", "Cuts the VM's power immediately: unsaved data inside the VM can be lost. Prefer Shut down.");
    public static readonly InfraAction CRestart = new("docker:restart", "↻ Restart", true, Consequence.ServiceInterruption, "running", "Stops and starts the container. Whatever it serves is briefly down.");
    public static readonly InfraAction CPause = new("docker:pause", "‖ Pause", true, Consequence.ServiceInterruption, "paused", "Freezes the container's processes until it's unpaused.");
    public static readonly InfraAction CUnpause = new("docker:unpause", "▷ Unpause", true, Consequence.None, "running", "Resumes a paused container.");
    public static readonly InfraAction VPause = new("vm:pause", "‖ Pause", true, Consequence.ServiceInterruption, "paused", "Pauses the VM in memory until it's resumed.");
    public static readonly InfraAction VResume = new("vm:resume", "▷ Resume", true, Consequence.None, "running", "Resumes a paused VM.");
    public static readonly InfraAction VReset = new("vm:reset", "⚡ Reset (hard)", true, Consequence.DataLoss, "running", "Hard reset, like the reset button: unsaved data inside the VM can be lost.");

    /// <summary>Only actions whose mutation this server's API actually has.</summary>
    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o)
    {
        var d = _found;
        if (d == null) return Array.Empty<InfraAction>();
        return o.Kind switch
        {
            "container" => new[] { CStart, CStop, CRestart, CPause, CUnpause }.Where(a => d.DockerMutations.Contains(a.Id[7..])).ToList(),
            "vm" => new[] { VStart, VStop, VReboot, VPause, VResume, VForce, VReset }.Where(a => d.VmMutations.Contains(a.Id[3..])).ToList(),
            _ => Array.Empty<InfraAction>(),
        };
    }

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => a.Id switch
    {
        "docker:start" or "vm:start" when o.Status == "running" => "already running",
        "docker:stop" or "vm:stop" or "vm:reboot" or "vm:forceStop" or "docker:restart" or "docker:pause" or "vm:pause" or "vm:reset" when o.Status != "running" => "not running",
        "docker:unpause" or "vm:resume" when o.Status != "paused" => "not paused",
        _ => null,
    };

    /// <summary>Ids only reach the server as GraphQL variables, and only if they look like Unraid ids.</summary>
    public static bool ValidId(string id) => Regex.IsMatch(id, @"^[A-Za-z0-9:_.\-]{1,200}$");

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct)
    {
        if (_schema == null || _found == null || !ValidId(o.Id)) return new ActionResult(false, false, "refused: not connected or unexpected id");
        var (root, op) = (a.Id.Split(':')[0], a.Id.Split(':')[1]);
        var mutType = _schema.Resolve(_schema.Mutation, root);
        if (mutType == null || !_schema.Types[mutType].TryGetValue(op, out var f) || !f.Args.TryGetValue("id", out var idType))
            return new ActionResult(false, false, "this server's API doesn't offer that operation");
        // An object result needs a selection: its id when the schema says it has one, otherwise __typename (always valid).
        var ret = f.Kind is "OBJECT" or "INTERFACE" or "UNION" ? $" {{ {(_schema.Pick(f.Type, "id") is { Length: > 0 } sel ? sel : "__typename")} }}" : "";
        var query = $"mutation($id: {idType}) {{ {root} {{ {op}(id: $id){ret} }} }}";
        try { await Post(query, new JsonObject { ["id"] = o.Id }, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new ActionResult(false, false, "refused: " + ex.Message); }

        var until = DateTime.UtcNow + VerifyFor;
        string state = "?";
        while (DateTime.UtcNow < until)
        {
            var now = (await InventoryAsync(ct)).FirstOrDefault(x => x.Id == o.Id && x.Kind == o.Kind);
            state = now?.Status ?? "gone";
            if (state == a.ExpectedStatus) return new ActionResult(true, true, $"verified: the {o.Kind} is now “{state}”");
            await Task.Delay(PollEvery, ct);
        }
        return new ActionResult(true, false, $"requested, but NOT verified: the {o.Kind} reports “{state}” after {VerifyFor.TotalSeconds:0} s (expected “{a.ExpectedStatus}”)");
    }
}
