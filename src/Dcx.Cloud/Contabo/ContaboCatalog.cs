namespace DcxManager.Contabo;

/// <summary>
/// The parts of the Contabo API v1 the app exposes (ADR-020), from the official spec (api.contabo.com, api-v1.yaml). Paths,
/// fields, enums and required values are the spec's; nothing here is guessed. Views and forms are generated from these
/// definitions by the shared cloud engine, exactly like Vultr's.
/// </summary>
public static class ContaboCatalog
{
    // ---- lookups ------------------------------------------------------------------------------------------------------
    public static readonly Lookup Images = new("/compute/images", "data", "imageId", "name", "version", "osType");
    public static readonly Lookup SshSecrets = new("/secrets?type=ssh", "data", "secretId", "name");
    public static readonly Lookup PasswordSecrets = new("/secrets?type=password", "data", "secretId", "name");
    public static readonly Lookup InstancesLookup = new("/compute/instances", "data", "instanceId", "displayName", "name", "ipConfig.v4.ip", "region");

    /// <summary>Regions accepted by create-instance and create-private-network (spec enum).</summary>
    public static readonly string[] Regions = { "EU", "US-central", "US-east", "US-west", "SIN", "UK", "AUS", "JPN", "IND" };
    public static readonly string[] DefaultUsers = { "root", "admin", "administrator" };

    private static Field SshKeysF => new("sshKeys", "SSH keys (secrets)", FieldKind.MultiLookup, Lookup: SshSecrets, Hint: "public keys stored in Contabo Secrets (Secrets ▸ upload one from the vault)");
    private static Field RootPasswordF => new("rootPassword", "Password (secret)", FieldKind.Lookup, Lookup: PasswordSecrets, Hint: "a password stored in Contabo Secrets; Linux: SSH keys are preferred");
    private static Field UserDataF => new("userData", "Cloud-init user data", FieldKind.Multiline, Hint: "optional #cloud-config");

    // Instance states (spec instanceStatus): provisioning, uninstalled, running, stopped, error, installing, unknown,
    // manual_provisioning, product_not_available, verification_required, rescue, pending_payment, other, reset_password.
    public static readonly ResourceDef Instance = new()
    {
        Title = "Instances", Group = "Compute", Path = "/compute/instances", Key = "data", IdField = "instanceId", NameField = "displayName", Icon = "▣",
        StatusField = "status", IsInstance = true, CanDelete = false,   // Contabo has no delete: a contract is cancelled (action below)
        Description = "Contabo VPS / VDS instances. Ordering a new one is billed for the chosen period; there is no delete — cancel the contract instead.",
        Columns = new Col[]
        {
            new("displayName", "Name", 160), new("name", "Host", 90), new("ipConfig.v4.ip", "IPv4", 120), new("region", "Region", 70), new("dataCenter", "Data center", 130),
            new("productName", "Product", 110), new("status", "Status", 90), new("cpuCores", "vCPU", 45), new("ramMb", "RAM MB", 65), new("diskMb", "Disk MB", 75),
            new("osType", "OS", 70), new("cancelDate", "Cancels on", 90),
        },
        Create = new Field[]
        {
            new("productId", "Product id", Required: true, Hint: "Contabo's API has no product catalogue: the ids are listed on contabo.com/en/product-list/?show_ids=true (the API's own default is V153)"),
            new("region", "Region", FieldKind.Select, true, "EU", Regions),
            new("imageId", "Image", FieldKind.Lookup, Lookup: Images, Hint: "empty = Contabo's default (Ubuntu)"),
            new("period", "Contract period (months)", FieldKind.IntSelect, true, "1", new[] { "1", "12", "24" }),
            new("displayName", "Display name"),
            new("defaultUser", "Default user", FieldKind.Select, Default: "admin", Options: DefaultUsers, Hint: "Linux: admin (sudo) or root · Windows: admin or administrator"),
            SshKeysF, RootPasswordF, UserDataF,
        },
        CreateConfirm = "This ORDERS a new Contabo server: product {form.productId} in {form.region} for {form.period} month(s), billed to this account. Continue?",
        Update = new Field[] { new("displayName", "Display name", Required: true, Default: "{displayName}") },
        Actions = new ActionDef[]
        {
            new("▶ Start", "POST", "/compute/instances/{instanceId}/actions/start", When: "stopped", Await: "running"),
            new("⏻ Shut down", "POST", "/compute/instances/{instanceId}/actions/shutdown", Confirm: "Shut down {displayName} ({ipConfig.v4.ip}) gracefully (ACPI)?", When: "running", Await: "stopped",
                Tip: "Graceful shutdown through the operating system"),
            new("↻ Restart", "POST", "/compute/instances/{instanceId}/actions/restart", Confirm: "Restart {displayName} ({ipConfig.v4.ip})?", When: "running,rescue", Await: "running"),
            new("■ Stop", "POST", "/compute/instances/{instanceId}/actions/stop", Confirm: "Stop {displayName} ({ipConfig.v4.ip})? This is a hard power-off.", Danger: true, When: "running,rescue", Await: "stopped"),
            new("⌨ SSH", "", "", Special: "ssh", Tip: "Open a terminal (uses or creates a saved host for this IP)"),
            new("＋ Add to hosts", "", "", Special: "addhost"),
            new("Rescue…", "POST", "/compute/instances/{instanceId}/actions/rescue", new[] { RootPasswordF, SshKeysF, UserDataF },
                Confirm: "Boot {displayName} into the rescue system? It restarts into rescue; the installed system is not started until you restart it again.", Danger: true, When: "running,stopped", Await: "rescue"),
            new("Reset password…", "POST", "/compute/instances/{instanceId}/actions/resetPassword", new[] { SshKeysF, RootPasswordF, UserDataF },
                Confirm: "Reset the login of {displayName} to the chosen secret(s)? The server restarts.", Danger: true, When: "running,stopped"),
            new("Reinstall…", "PUT", "/compute/instances/{instanceId}", new[]
            {
                new Field("imageId", "Image", FieldKind.Lookup, true, Default: "{imageId}", Lookup: Images),
                new Field("defaultUser", "Default user", FieldKind.Select, Default: "{defaultUser}", Options: DefaultUsers), SshKeysF, RootPasswordF, UserDataF,
            }, Confirm: "Reinstall {displayName}? The disk is wiped and the image installed again.", Danger: true, TypeToConfirm: "{displayName}",
                When: "running,stopped,error,rescue", Await: "running"),
            new("Upgrade options", "GET", "/compute/instances/{instanceId}/products/available", ShowResult: true,
                Tip: "What this instance could be upgraded to (read only: changing the product isn't possible through Contabo's API)"),
            new("Add Private Networking…", "POST", "/compute/instances/{instanceId}/upgrade", new[] { new Field("privateNetworking", "Add-on options (JSON)", FieldKind.Json, true, "{}", Hint: "Contabo's spec: an empty object for now") },
                Confirm: "ORDER the Private Networking add-on for {displayName}? It's billed monthly and needs a restart to show the new interface.", Idempotent: false),
            new("Add Automated Backup…", "POST", "/compute/instances/{instanceId}/upgrade", new[] { new Field("backup", "Add-on options (JSON)", FieldKind.Json, true, "{}", Hint: "Contabo's spec: an empty object for now") },
                Confirm: "ORDER the Automated Backup add-on for {displayName}? It's billed monthly.", Idempotent: false),
            new("Cancel contract…", "POST", "/compute/instances/{instanceId}/cancel", new[]
            {
                new Field("cancelDate", "Cancellation date", Hint: "yyyy-mm-dd; empty = the earliest date your contract allows"),
            }, Confirm: "Cancel the contract of {displayName} ({ipConfig.v4.ip})? The server and its data are removed by Contabo on the cancellation date.", Danger: true,
                TypeToConfirm: "{displayName}", Tip: "Contabo's way to end an instance (there is no immediate delete)"),
        },
        Children = new[] { ("Snapshots", Snapshot) },
    };

    public static ResourceDef Snapshot => new()
    {
        Title = "Snapshots", Group = "Compute", Path = "/compute/instances/{parent.instanceId}/snapshots", Key = "data", IdField = "snapshotId", NameField = "name", Icon = "◷",
        ItemPath = "/compute/instances/{parent.instanceId}/snapshots/{snapshotId}",
        Columns = new Col[] { new("name", "Name", 160), new("description", "Description", 220), new("createdDate", "Created", 150), new("autoDeleteDate", "Auto-deletes", 150), new("imageName", "Image", 160), new("snapshotId", "Id", 200) },
        Create = new Field[] { new("name", "Name", Required: true, Default: "{now}", Hint: "1–30 characters"), new("description", "Description") },
        Update = new Field[] { new("name", "Name", Default: "{name}"), new("description", "Description", Default: "{description}") },
        Actions = new ActionDef[]
        {
            new("↺ Roll back…", "POST", "/compute/instances/{parent.instanceId}/snapshots/{snapshotId}/rollback",
                Confirm: "Roll the instance back to snapshot “{name}”? Everything since then is lost, and newer snapshots are deleted.", Danger: true, TypeToConfirm: "{name}"),
        },
    };

    public static readonly ResourceDef Image = new()
    {
        Title = "Images", Group = "Compute", Path = "/compute/images", Key = "data", IdField = "imageId", NameField = "name", Icon = "◉",
        Description = "Standard images and your custom images. A custom image is uploaded by Contabo from a public URL (.qcow2 or .iso) and needs the Custom Images add-on.",
        DeleteWhen = "standardImage=no", StatusField = "status",
        Columns = new Col[] { new("name", "Name", 200), new("version", "Version", 90), new("osType", "OS", 70), new("standardImage", "Standard", 65), new("status", "Status", 80), new("sizeMb", "MB", 70), new("format", "Format", 60), new("imageId", "Id", 240) },
        Create = new Field[]
        {
            new("name", "Name", Required: true), new("url", "Image URL (https)", Required: true, Hint: "Contabo downloads it from here; .qcow2 or .iso"),
            new("osType", "OS type", FieldKind.Select, true, "Linux", new[] { "Linux", "Windows" }), new("version", "Version", Required: true), new("description", "Description"),
        },
        Update = new Field[] { new("name", "Name", Default: "{name}"), new("description", "Description", Default: "{description}") },
        Actions = new ActionDef[] { new("Image statistics", "GET", "/compute/images/stats", ShowResult: true, NoRow: true) },
    };

    public static readonly ResourceDef Secret = new()
    {
        Title = "Secrets", Group = "Access", Path = "/secrets", Key = "data", IdField = "secretId", NameField = "name", Icon = "⚿",
        Description = "SSH public keys and passwords that orders, reinstalls, rescue and password resets refer to. Values are never listed.",
        Columns = new Col[] { new("name", "Name", 220), new("type", "Type", 80), new("createdAt", "Created", 160), new("updatedAt", "Updated", 160), new("secretId", "Id", 80) },
        Create = new Field[]
        {
            new("name", "Name", Required: true), new("type", "Type", FieldKind.Select, true, "ssh", new[] { "ssh", "password" }),
            new("value", "Value", FieldKind.Password, true, Hint: "an SSH public key, or a password (8+ characters with upper and lower case, numbers and !@#$^&*?_~)"),
        },
        Update = new Field[] { new("name", "Name", Default: "{name}"), new("value", "New value", FieldKind.Password, Hint: "empty = keep") },
        Actions = new ActionDef[] { new("⇧ Upload a vault key…", "", "", Special: "uploadkey", NoRow: true, Tip: "Add the public half of an SSH key from this app's vault") },
    };

    public static readonly ResourceDef PrivateNetwork = new()
    {
        Title = "Private Networks", Group = "Network", Path = "/private-networks", Key = "data", IdField = "privateNetworkId", NameField = "name", Icon = "⇄",
        Description = "Private networks between instances (instances need the Private Networking add-on).",
        Columns = new Col[] { new("name", "Name", 160), new("region", "Region", 70), new("dataCenter", "Data center", 140), new("cidr", "CIDR", 120), new("availableIps", "Free IPs", 65), new("instances", "Instances", 260), new("description", "Description", 180) },
        Create = new Field[] { new("name", "Name", Required: true), new("region", "Region", FieldKind.Select, Default: "EU", Options: Regions), new("description", "Description") },
        Update = new Field[] { new("name", "Name", Default: "{name}"), new("description", "Description", Default: "{description}") },
        Actions = new ActionDef[]
        {
            new("Add instance…", "POST", "/private-networks/{privateNetworkId}/instances/{form.instanceId}",
                new[] { new Field("instanceId", "Instance", FieldKind.Lookup, true, Lookup: InstancesLookup, InPath: true) }, Confirm: "Add the instance to {name}? It may need a restart to see the network."),
            new("Remove instance…", "DELETE", "/private-networks/{privateNetworkId}/instances/{form.instanceId}",
                new[] { new Field("instanceId", "Instance", FieldKind.Lookup, true, Lookup: InstancesLookup, InPath: true) }, Confirm: "Remove the instance from {name}?", Danger: true),
        },
    };

    public static readonly ResourceDef Firewall = new()
    {
        Title = "Firewalls", Group = "Network", Path = "/firewalls", Key = "data", IdField = "firewallId", NameField = "name", Icon = "⛨", StatusField = "status",
        Description = "Contabo firewalls: inbound rules applied to the instances assigned to them. Rules are edited as JSON (protocol, destPorts, srcCidr, action, status).",
        Columns = new Col[] { new("name", "Name", 160), new("status", "Status", 70), new("description", "Description", 200), new("instances", "Instances", 240), new("rules.inbound", "Inbound rules", 260), new("firewallId", "Id", 240) },
        Create = new Field[]
        {
            new("name", "Name", Required: true), new("description", "Description"),
            new("status", "Status", FieldKind.Select, true, "active", new[] { "active", "inactive" }),
            new("rules", "Rules (JSON)", FieldKind.Json, Default: """{"inbound": [{"protocol": "tcp", "destPorts": ["22"], "srcCidr": {"ipv4": ["0.0.0.0/0"], "ipv6": []}, "action": "accept", "status": "active"}]}"""),
        },
        Update = new Field[]
        {
            new("name", "Name", Default: "{name}"), new("description", "Description", Default: "{description}"),
            new("status", "Status", FieldKind.Select, Default: "{status}", Options: new[] { "active", "inactive" }),
        },
        Actions = new ActionDef[]
        {
            new("Edit rules…", "PUT", "/firewalls/{firewallId}", new[] { new Field("rules", "Rules (JSON)", FieldKind.Json, true, "{json:rules}") },
                Confirm: "Replace the rules of {name}? They apply to every instance assigned to it.", Danger: true),
            new("Assign instance…", "POST", "/firewalls/{firewallId}/instances/{form.instanceId}",
                new[] { new Field("instanceId", "Instance", FieldKind.Lookup, true, Lookup: InstancesLookup, InPath: true) }, Confirm: "Apply {name} to the instance? Traffic not allowed by its rules is dropped."),
            new("Unassign instance…", "DELETE", "/firewalls/{firewallId}/instances/{form.instanceId}",
                new[] { new Field("instanceId", "Instance", FieldKind.Lookup, true, Lookup: InstancesLookup, InPath: true) }, Confirm: "Remove {name} from the instance?", Danger: true),
            new("Preset rules", "GET", "/firewalls/preset-rules", ShowResult: true, NoRow: true),
        },
    };

    public static readonly ResourceDef DnsZone = new()
    {
        Title = "DNS Zones", Group = "Network", Path = "/dns/zones", Key = "data", IdField = "zoneName", NameField = "zoneName", Icon = "◎",
        ItemPath = "/dns/zones/{zoneName}",
        Columns = new Col[] { new("zoneName", "Zone", 260), new("customerId", "Customer", 100) },
        Create = new Field[] { new("zoneName", "Zone name", Required: true, Hint: "example.com") },
        TypeToDelete = true,
        Children = new[]
        {
            ("Records", new ResourceDef
            {
                Title = "Records", Group = "Network", Path = "/dns/zones/{parent.zoneName}/records", Key = "data", IdField = "recordId", NameField = "name",
                ItemPath = "/dns/zones/{parent.zoneName}/records/{recordId}",
                Columns = new Col[] { new("name", "Name", 140), new("type", "Type", 60), new("data", "Data", 260), new("ttl", "TTL", 60), new("prio", "Prio", 45), new("port", "Port", 50), new("weight", "Weight", 55), new("recordId", "Id", 80) },
                Create = new Field[]
                {
                    new("name", "Name", Hint: "empty = the zone itself"), new("type", "Type", FieldKind.Select, true, "A", new[] { "A", "AAAA", "CAA", "CNAME", "MX", "SRV", "TXT" }),
                    new("data", "Data", Required: true), new("ttl", "TTL (s)", FieldKind.Int, true, "86400"), new("prio", "Priority", FieldKind.Int, true, "0"),
                    new("port", "Port (SRV)", FieldKind.Int), new("weight", "Weight (SRV)", FieldKind.Int), new("flag", "Flag (CAA)", FieldKind.Int), new("tag", "Tag (CAA)"),
                },
                Update = new Field[]
                {
                    new("type", "Type", FieldKind.Select, true, "{type}", new[] { "A", "AAAA", "CAA", "CNAME", "MX", "SRV", "TXT" }),
                    new("data", "Data", Required: true, Default: "{data}"), new("ttl", "TTL (s)", FieldKind.Int, true, "{ttl}"), new("prio", "Priority", FieldKind.Int, true, "{prio}"),
                    new("port", "Port (SRV)", FieldKind.Int, Default: "{port}"), new("weight", "Weight (SRV)", FieldKind.Int, Default: "{weight}"),
                },
            }),
        },
    };

    public static readonly ResourceDef DataCenter = new()
    {
        Title = "Data centers", Group = "Reference", Path = "/data-centers", Key = "data", IdField = "slug", NameField = "name", ReadOnly = true, CanDelete = false,
        Columns = new Col[] { new("name", "Name", 220), new("slug", "Slug", 80), new("regionName", "Region", 160), new("regionSlug", "Region id", 80), new("capabilities", "Capabilities", 260) },
    };

    public static readonly ResourceDef StandardImages = new()
    {
        Title = "Standard images", Group = "Reference", Path = "/compute/images?standardImage=true", Key = "data", IdField = "imageId", NameField = "name", ReadOnly = true, CanDelete = false,
        Columns = new Col[] { new("name", "Name", 220), new("version", "Version", 90), new("osType", "OS", 70), new("imageId", "Id", 260) },
    };

    public static readonly ResourceDef ActionAudits = new()
    {
        Title = "Action history", Group = "Reference", Path = "/compute/instances/actions/audits", Key = "data", IdField = "id", NameField = "action", ReadOnly = true, CanDelete = false,
        Description = "Instance actions recorded by Contabo (start, stop, restart, rescue …), whoever made them.",
        Columns = new Col[] { new("timestamp", "Time", 160), new("instanceId", "Instance", 90), new("action", "Action", 110), new("username", "By", 160), new("changes", "Changes", 260), new("requestId", "Request id", 240) },
    };

    // ---- ADR-031: the rest of the spec's everyday resources ----------------------------------------------------------

    /// <summary>Object storage regions (spec CreateObjectStorageRequest).</summary>
    public static readonly string[] ObjectStorageRegions = { "EU", "US-central", "SIN" };

    // Object storage states (spec): READY, PROVISIONING, UPGRADING, CANCELLED, ERROR, DISABLED, MANUAL_PROVISIONING, …
    public static readonly ResourceDef ObjectStorage = new()
    {
        Title = "Object Storage", Group = "Storage", Path = "/object-storages", Key = "data", IdField = "objectStorageId", NameField = "displayName", Icon = "◫",
        StatusField = "status", CanDelete = false,   // like instances: no delete, the contract is cancelled
        Description = "S3-compatible object storage. Ordering is billed monthly; the space can only grow; there is no delete — cancel instead. S3 keys are managed in Contabo's customer panel.",
        Columns = new Col[]
        {
            new("displayName", "Name", 160), new("region", "Region", 80), new("dataCenter", "Data center", 130), new("totalPurchasedSpaceTB", "TB", 50), new("status", "Status", 90),
            new("autoScaling.state", "Autoscaling", 80), new("autoScaling.sizeLimitTB", "Limit TB", 65), new("s3Url", "S3 URL", 220), new("cancelDate", "Cancels on", 90), new("objectStorageId", "Id", 220),
        },
        Create = new Field[]
        {
            new("region", "Region", FieldKind.Select, true, "EU", ObjectStorageRegions),
            new("totalPurchasedSpaceTB", "Space (TB)", FieldKind.Json, true, "0.25", Hint: "a number, e.g. 0.25, 1 or 2.5 (billed per TB)"),
            new("autoScaling", "Autoscaling (JSON)", FieldKind.Json, Default: "{\"state\": \"disabled\", \"sizeLimitTB\": 0}", Hint: "state enabled/disabled and the size limit in TB"),
            new("displayName", "Display name"),
        },
        CreateConfirm = "This ORDERS Contabo object storage: {form.totalPurchasedSpaceTB} TB in {form.region}, billed monthly to this account. Continue?",
        Update = new Field[] { new("displayName", "Display name", Required: true, Default: "{displayName}") },
        Actions = new ActionDef[]
        {
            new("Usage", "GET", "/object-storages/{objectStorageId}/stats", ShowResult: true, Tip: "Used space and object count"),
            new("Resize…", "POST", "/object-storages/{objectStorageId}/resize", new[]
            {
                new Field("totalPurchasedSpaceTB", "New total space (TB)", FieldKind.Json, Default: "{totalPurchasedSpaceTB}", Hint: "larger than now (no downgrade); the extra space is billed"),
                new Field("autoScaling", "Autoscaling (JSON)", FieldKind.Json, Default: "{json:autoScaling}"),
            }, Confirm: "Change {displayName}'s space / autoscaling? Added space is billed from now on.", When: "READY", Idempotent: false),
            new("Cancel contract…", "PATCH", "/object-storages/{objectStorageId}/cancel", new[] { new Field("cancelDate", "Cancellation date", Hint: "yyyy-mm-ddThh:mm:ssZ; empty = the earliest date your contract allows") },
                Confirm: "Cancel {displayName}? The storage and ALL ITS OBJECTS are removed by Contabo on the cancellation date.", Danger: true, TypeToConfirm: "{displayName}"),
        },
    };

    public static readonly ResourceDef Vip = new()
    {
        Title = "VIPs (additional IPs)", Group = "Network", Path = "/vips", Key = "data", IdField = "v4.ip", NameField = "v4.ip", Icon = "⌖", CanDelete = false,
        ItemPath = "/vips/{v4.ip}",
        Description = "Additional and floating IPv4 addresses bought for this account, and what they're assigned to. Buying one is done in the customer panel.",
        Columns = new Col[]
        {
            new("v4.ip", "IPv4", 120), new("type", "Type", 70), new("region", "Region", 80), new("dataCenter", "Data center", 130),
            new("resourceType", "Assigned to", 80), new("resourceDisplayName", "Name", 160), new("resourceId", "Resource id", 90), new("vipId", "Id", 220),
        },
        Actions = new ActionDef[]
        {
            new("Assign to instance…", "POST", "/vips/{v4.ip}/instances/{form.instanceId}",
                new[] { new Field("instanceId", "Instance", FieldKind.Lookup, true, Lookup: InstancesLookup, InPath: true) },
                Confirm: "Assign {v4.ip} to the instance? Configure the address inside the server afterwards (Contabo doesn't do that)."),
            new("Unassign", "DELETE", "/vips/{v4.ip}/{resourceType}/{resourceId}", Confirm: "Unassign {v4.ip} from {resourceDisplayName}? Traffic to it stops reaching the server.", Danger: true,
                When: "resourceType=instances,bare-metal"),
        },
    };

    public static readonly ResourceDef PtrRecord = new()
    {
        Title = "Reverse DNS (PTR)", Group = "Network", Path = "/dns/ptrs", Key = "data", IdField = "ip", NameField = "ip", Icon = "↩", UpdateMethod = "PUT",
        ItemPath = "/dns/ptrs/{ip}",
        Description = "PTR records of this account's IPv4 and IPv6 addresses (mail servers need a matching reverse DNS name).",
        Columns = new Col[] { new("ip", "IP", 200), new("ptr", "PTR name", 260), new("ttl", "TTL", 70) },
        Create = new Field[] { new("ip", "IP address", Required: true), new("ptr", "PTR name", Required: true, Hint: "e.g. mail.example.com"), new("ttl", "TTL (s)", FieldKind.Int, true, "86400") },
        Update = new Field[] { new("ptr", "PTR name", Required: true, Default: "{ptr}") },
    };

    public static readonly ResourceDef Tag = new()
    {
        Title = "Tags", Group = "Access", Path = "/tags", Key = "data", IdField = "tagId", NameField = "name", Icon = "#",
        Description = "Tags to group instances, images and object storage (also used by Contabo's user roles to limit access).",
        Columns = new Col[] { new("name", "Name", 200), new("color", "Color", 80), new("tagId", "Id", 90) },
        Create = new Field[] { new("name", "Name", Required: true, Hint: "letters, numbers, colons, dashes and underscores"), new("color", "Color", Required: true, Default: "#0A78C3", Hint: "hex, e.g. #0A78C3") },
        Update = new Field[] { new("name", "Name", Default: "{name}"), new("color", "Color", Default: "{color}") },
        Actions = new ActionDef[]
        {
            new("Tag an instance…", "POST", "/tags/{tagId}/assignments/instance/{form.instanceId}",
                new[] { new Field("instanceId", "Instance", FieldKind.Lookup, true, Lookup: InstancesLookup, InPath: true) }, Confirm: "Add the tag {name} to the instance?"),
        },
        Children = new[]
        {
            ("Assignments", new ResourceDef
            {
                Title = "Assignments", Group = "Access", Path = "/tags/{parent.tagId}/assignments", Key = "data", IdField = "resourceId", NameField = "resourceName",
                ItemPath = "/tags/{parent.tagId}/assignments/{resourceType}/{resourceId}",
                Columns = new Col[] { new("resourceType", "Type", 100), new("resourceName", "Name", 200), new("resourceId", "Resource id", 220), new("tagName", "Tag", 120) },
            }),
        },
    };

    public static readonly ResourceDef[] All = { Instance, Image, Secret, Tag, PrivateNetwork, Firewall, DnsZone, Vip, PtrRecord, ObjectStorage, DataCenter, StandardImages, ActionAudits };
    public static readonly string[] GroupOrder = { "Compute", "Storage", "Access", "Network", "Reference" };
}
