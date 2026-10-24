using DcxManager.Cloud;

namespace DcxManager.Vultr;

/// <summary>Every part of the Vultr API the app exposes. Views are generated from these definitions.</summary>
public static class VultrCatalog
{
    // ---- shared lookups -----------------------------------------------------------------------------------------
    public static readonly Lookup Regions = new("/regions", "regions", "id", "id", "city", "country");
    public static readonly Lookup Plans = new("/plans", "plans", "id", "id", "vcpu_count", "ram", "disk", "monthly_cost", "locations") { SortBy = "monthly_cost" };
    public static readonly Lookup MetalPlans = new("/plans-metal", "plans_metal", "id", "id", "cpu_count", "ram", "disk", "monthly_cost") { SortBy = "monthly_cost" };
    public static readonly Lookup Os = new("/os", "os", "id", "name", "family");
    public static readonly Lookup Apps = new("/applications", "applications", "id", "deploy_name", "type");
    public static readonly Lookup SshKeys = new("/ssh-keys", "ssh_keys", "id", "name");
    public static readonly Lookup Scripts = new("/startup-scripts", "startup_scripts", "id", "name", "type");
    public static readonly Lookup FirewallGroups = new("/firewalls", "firewall_groups", "id", "description", "id");
    public static readonly Lookup Instances = new("/instances", "instances", "id", "label", "main_ip", "region");
    public static readonly Lookup Snapshots = new("/snapshots", "snapshots", "id", "description", "date_created");
    public static readonly Lookup Isos = new("/iso", "isos", "id", "filename", "status");
    public static readonly Lookup Vpcs = new("/vpcs", "vpcs", "id", "description", "region", "v4_subnet");
    public static readonly Lookup Blocks = new("/blocks", "blocks", "id", "label", "size_gb", "region");

    private static Field RegionF => new("region", "Region", FieldKind.Lookup, true, Lookup: Regions);
    private static Field Label => new("label", "Label");
    private static Field Tags => new("tags", "Tags", FieldKind.List, Hint: "comma separated");

    private static readonly ActionDef[] PowerActions =
    {
        new("▶ Start", "POST", "/instances/{id}/start", When: "stopped", Await: "running"),
        new("↻ Reboot", "POST", "/instances/{id}/reboot", Confirm: "Reboot {label} ({main_ip})?", When: "running"),
        new("■ Halt", "POST", "/instances/{id}/halt", Confirm: "Power off {label} ({main_ip})? This is a hard power-off.", Danger: true, When: "running", Await: "stopped"),
    };

    public static readonly ResourceDef Instance = new()
    {
        Title = "Instances", Group = "Compute", Path = "/instances", Key = "instances", Icon = "▣",
        StatusField = "power_status", IsInstance = true, TypeToDelete = true,
        Description = "Cloud Compute, Optimized and GPU instances.",
        Columns = new Col[]
        {
            new("label", "Label", 170), new("main_ip", "IPv4", 120), new("region", "Region", 60), new("plan", "Plan", 150),
            new("os", "OS", 160), new("status", "Status", 70), new("power_status", "Power", 70), new("server_status", "Server", 90),
            new("vcpu_count", "vCPU", 45), new("ram", "RAM MB", 65), new("tags", "Tags", 120),
        },
        Create = new Field[]
        {
            RegionF,
            new("plan", "Plan", FieldKind.Lookup, true, Lookup: Plans, Hint: "check the plan is offered in the region (locations)"),
            new("os_id", "Operating system", FieldKind.Lookup, Lookup: Os, Hint: "or choose an app / snapshot / ISO below"),
            new("app_id", "Marketplace app", FieldKind.Lookup, Lookup: Apps),
            new("snapshot_id", "From snapshot", FieldKind.Lookup, Lookup: Snapshots),
            new("iso_id", "Boot ISO", FieldKind.Lookup, Lookup: Isos),
            new("label", "Label"), new("hostname", "Hostname"),
            new("sshkey_id", "SSH keys", FieldKind.MultiLookup, Lookup: SshKeys),
            new("script_id", "Startup script", FieldKind.Lookup, Lookup: Scripts),
            new("firewall_group_id", "Firewall group", FieldKind.Lookup, Lookup: FirewallGroups),
            new("attach_vpc", "Attach VPCs", FieldKind.MultiLookup, Lookup: Vpcs),
            new("enable_ipv6", "Enable IPv6", FieldKind.Bool, Default: "true"),
            new("disable_public_ipv4", "No public IPv4", FieldKind.Bool),
            new("backups", "Automatic backups", FieldKind.Select, Default: "disabled", Options: new[] { "disabled", "enabled" }),
            new("ddos_protection", "DDoS protection", FieldKind.Bool),
            new("activation_email", "Send activation email", FieldKind.Bool),
            Tags,
            new("user_data", "Cloud-init user data", FieldKind.Base64),
        },
        Update = new Field[]
        {
            new("label", "Label", Default: "{label}"), new("tags", "Tags", FieldKind.List, Default: "{tags}"),
            new("firewall_group_id", "Firewall group", FieldKind.Lookup, Lookup: FirewallGroups, Default: "{firewall_group_id}"),
            new("backups", "Automatic backups", FieldKind.Select, Options: new[] { "", "enabled", "disabled" }),
            new("ddos_protection", "DDoS protection", FieldKind.Select, Options: new[] { "", "true", "false" }),
            new("plan", "Upgrade plan", FieldKind.Lookup, Lookup: Plans, Hint: "upgrades only; the instance restarts"),
        },
        Actions = PowerActions.Concat(new ActionDef[]
        {
            new("⌨ SSH", "", "", Special: "ssh", Tip: "Open a terminal (uses or creates a saved host for this IP)"),
            new("🖥 Console", "", "", Special: "console", Tip: "Vultr web console (noVNC) — works even when SSH is down"),
            new("＋ Add to hosts", "", "", Special: "addhost"),
            new("Snapshot", "POST", "/snapshots", new[] { new Field("description", "Description", Default: "{label} {now}") }, Fixed: new() { ["instance_id"] = "{id}" }),
            new("Restore…", "POST", "/instances/{id}/restore", new[]
            {
                new Field("backup_id", "Backup", FieldKind.Lookup, Lookup: new("/backups?instance_id={id}", "backups", "id", "description", "date_created")),
                new Field("snapshot_id", "or snapshot", FieldKind.Lookup, Lookup: Snapshots),
            }, Confirm: "Restore {label}? Everything on its disk will be replaced.", Danger: true, TypeToConfirm: "{label}"),
            new("Reinstall…", "POST", "/instances/{id}/reinstall", new[] { new Field("hostname", "Hostname", Default: "{hostname}") },
                Confirm: "Reinstall {label}? The disk is wiped and the OS reinstalled.", Danger: true, TypeToConfirm: "{label}"),
            new("Attach ISO", "POST", "/instances/{id}/iso/attach", new[] { new Field("iso_id", "ISO", FieldKind.Lookup, true, Lookup: Isos) }, Confirm: "Attaching an ISO reboots {label}."),
            new("Detach ISO", "POST", "/instances/{id}/iso/detach", Confirm: "Detaching the ISO reboots {label}."),
            new("Backup schedule…", "POST", "/instances/{id}/backup-schedule", new[]
            {
                new Field("type", "Frequency", FieldKind.Select, true, "daily", new[] { "daily", "weekly", "monthly", "daily_alt_even", "daily_alt_odd" }),
                new Field("hour", "Hour (UTC, 0-23)", FieldKind.Int, Default: "3"), new Field("dow", "Day of week (1=Sun..7)", FieldKind.Int, Default: "1"),
                new Field("dom", "Day of month (1-28)", FieldKind.Int, Default: "1"),
            }),
            new("Reverse DNS…", "POST", "/instances/{id}/ipv4/reverse", new[] { new Field("ip", "IP", Required: true, Default: "{main_ip}"), new Field("reverse", "Hostname (PTR)", Required: true) }),
            new("Add IPv4", "POST", "/instances/{id}/ipv4", new[] { new Field("reboot", "Reboot to apply", FieldKind.Bool, Default: "true") }, Confirm: "Add an extra (billed) IPv4 address to {label}?"),
            new("Attach VPC", "POST", "/instances/{id}/vpcs/attach", new[] { new Field("vpc_id", "VPC", FieldKind.Lookup, true, Lookup: Vpcs) }),
            new("Detach VPC", "POST", "/instances/{id}/vpcs/detach", new[] { new Field("vpc_id", "VPC", FieldKind.Lookup, true, Lookup: Vpcs) }),
            new("Bandwidth", "GET", "/instances/{id}/bandwidth", ShowResult: true),
            new("IPv4 list", "GET", "/instances/{id}/ipv4", ShowResult: true),
            new("IPv6 list", "GET", "/instances/{id}/ipv6", ShowResult: true),
            // ADR-031 (govultr instance.go): the rest of the IP / reverse DNS and ISO endpoints.
            new("IPv6 reverse DNS…", "POST", "/instances/{id}/ipv6/reverse", new[] { new Field("ip", "IPv6 address", Required: true, Default: "{v6_main_ip}"), new Field("reverse", "Host name", Required: true) },
                Confirm: "Set the reverse DNS of that IPv6 address of {label}?"),
            new("IPv6 reverse list", "GET", "/instances/{id}/ipv6/reverse", ShowResult: true),
            new("Delete IPv6 reverse…", "DELETE", "/instances/{id}/ipv6/reverse/{form.ip}", new[] { new Field("ip", "IPv6 address", Required: true, InPath: true) },
                Confirm: "Delete the reverse DNS entry {form.ip} of {label}?", Danger: true),
            new("Default IPv4 reverse…", "POST", "/instances/{id}/ipv4/reverse/default", new[] { new Field("ip", "IPv4 address", Required: true, Default: "{main_ip}") },
                Confirm: "Reset the reverse DNS of that address of {label} to Vultr's default?"),
            new("Remove extra IPv4…", "DELETE", "/instances/{id}/ipv4/{form.ip}", new[] { new Field("ip", "Extra IPv4 address", Required: true, InPath: true, Hint: "never the main IP") },
                Confirm: "Remove {form.ip} from {label}? Traffic to it stops; the billing for it ends.", Danger: true),
            new("ISO status", "GET", "/instances/{id}/iso", ShowResult: true),
            new("Backup schedule", "GET", "/instances/{id}/backup-schedule", ShowResult: true),
            new("Available upgrades", "GET", "/instances/{id}/upgrades", ShowResult: true),
            new("User data", "GET", "/instances/{id}/user-data", Special: "userdata"),
            new("Neighbors", "GET", "/instances/{id}/neighbors", ShowResult: true),
            new("VPCs", "GET", "/instances/{id}/vpcs", ShowResult: true),
        }).ToArray(),
    };

    public static readonly ResourceDef BareMetal = new()
    {
        Title = "Bare Metal", Group = "Compute", Path = "/bare-metals", Key = "bare_metals", Icon = "▤", IsInstance = true, TypeToDelete = true,
        Columns = new Col[] { new("label", "Label", 170), new("main_ip", "IPv4", 120), new("region", "Region", 60), new("plan", "Plan", 160), new("os", "OS", 160), new("status", "Status", 80), new("cpu_count", "CPU", 50), new("ram", "RAM", 90) },
        Create = new Field[]
        {
            RegionF, new("plan", "Plan", FieldKind.Lookup, true, Lookup: MetalPlans), new("os_id", "Operating system", FieldKind.Lookup, Lookup: Os),
            new("app_id", "Marketplace app", FieldKind.Lookup, Lookup: Apps), new("snapshot_id", "From snapshot", FieldKind.Lookup, Lookup: Snapshots),
            Label, new("hostname", "Hostname"), new("sshkey_id", "SSH keys", FieldKind.MultiLookup, Lookup: SshKeys),
            new("script_id", "Startup script", FieldKind.Lookup, Lookup: Scripts), new("enable_ipv6", "Enable IPv6", FieldKind.Bool, Default: "true"),
            new("activation_email", "Send activation email", FieldKind.Bool), Tags, new("user_data", "Cloud-init user data", FieldKind.Base64),
        },
        Update = new Field[] { new("label", "Label", Default: "{label}"), new("tags", "Tags", FieldKind.List, Default: "{tags}") },
        Actions = new ActionDef[]
        {
            new("▶ Start", "POST", "/bare-metals/{id}/start"),
            new("↻ Reboot", "POST", "/bare-metals/{id}/reboot", Confirm: "Reboot {label}?"),
            new("■ Halt", "POST", "/bare-metals/{id}/halt", Confirm: "Power off {label}?", Danger: true),
            new("⌨ SSH", "", "", Special: "ssh"),
            new("🖥 Console", "GET", "/bare-metals/{id}/vnc", Special: "vnc"),
            new("＋ Add to hosts", "", "", Special: "addhost"),
            new("Reinstall…", "POST", "/bare-metals/{id}/reinstall", new[] { new Field("hostname", "Hostname") }, Confirm: "Reinstall {label}? All data is erased.", Danger: true, TypeToConfirm: "{label}"),
            new("Bandwidth", "GET", "/bare-metals/{id}/bandwidth", ShowResult: true),
            new("IPv4 list", "GET", "/bare-metals/{id}/ipv4", ShowResult: true),
            new("User data", "GET", "/bare-metals/{id}/user-data", Special: "userdata"),
            new("IPv6 list", "GET", "/bare-metals/{id}/ipv6", ShowResult: true),
            new("Available upgrades", "GET", "/bare-metals/{id}/upgrades", ShowResult: true),
            new("VPCs", "GET", "/bare-metals/{id}/vpcs", ShowResult: true),
            new("Attach VPC", "POST", "/bare-metals/{id}/vpcs/attach", new[] { new Field("vpc_id", "VPC", FieldKind.Lookup, true, Lookup: Vpcs) }),
            new("Detach VPC", "POST", "/bare-metals/{id}/vpcs/detach", new[] { new Field("vpc_id", "VPC", FieldKind.Lookup, true, Lookup: Vpcs) }, Confirm: "Detach {label} from that VPC?", Danger: true),
        },
    };

    public static readonly ResourceDef Kubernetes = new()
    {
        Title = "Kubernetes", Group = "Compute", Path = "/kubernetes/clusters", Key = "vke_clusters", Icon = "☸", TypeToDelete = true,
        Columns = new Col[] { new("label", "Label", 170), new("region", "Region", 60), new("version", "Version", 120), new("status", "Status", 80), new("endpoint", "Endpoint", 220), new("ip", "IP", 120) },
        Create = new Field[]
        {
            Label, RegionF,
            new("version", "Version", FieldKind.Lookup, true, Lookup: new("/kubernetes/versions", "versions", "", "")),
            new("ha_controlplanes", "HA control plane", FieldKind.Bool), new("enable_firewall", "Managed firewall", FieldKind.Bool),
            new("node_pools", "Node pools (JSON)", FieldKind.Json, true, """[{"node_quantity": 2, "label": "pool-1", "plan": "vc2-2c-4gb"}]"""),
        },
        Update = new Field[] { new("label", "Label", Default: "{label}") },
        Actions = new ActionDef[]
        {
            new("Download kubeconfig", "GET", "/kubernetes/clusters/{id}/config", Special: "kubeconfig"),
            new("Available upgrades", "GET", "/kubernetes/clusters/{id}/available-upgrades", ShowResult: true),
            new("Upgrade…", "POST", "/kubernetes/clusters/{id}/upgrades", new[] { new Field("upgrade_version", "Version", Required: true) }, Confirm: "Upgrade cluster {label}?"),
            new("Delete with linked resources", "DELETE", "/kubernetes/clusters/{id}/delete-with-linked-resources", Confirm: "Delete {label} AND its block storage / load balancers?", Danger: true, TypeToConfirm: "{label}"),
        },
        Children = new[]
        {
            ("Node pools", new ResourceDef
            {
                Title = "Node pools", Path = "/kubernetes/clusters/{parent.id}/node-pools", Key = "node_pools",
                Columns = new Col[] { new("label", "Label", 150), new("plan", "Plan", 150), new("node_quantity", "Nodes", 60), new("status", "Status", 80), new("auto_scaler", "Autoscale", 70), new("min_nodes", "Min", 50), new("max_nodes", "Max", 50) },
                Create = new Field[] { new("label", "Label", Required: true), new("plan", "Plan", FieldKind.Lookup, true, Lookup: Plans), new("node_quantity", "Nodes", FieldKind.Int, true, "2"), new("auto_scaler", "Autoscale", FieldKind.Bool), new("min_nodes", "Min nodes", FieldKind.Int), new("max_nodes", "Max nodes", FieldKind.Int), new("tag", "Tag") },
                Update = new Field[] { new("node_quantity", "Nodes", FieldKind.Int, Default: "{node_quantity}"), new("auto_scaler", "Autoscale", FieldKind.Bool), new("min_nodes", "Min nodes", FieldKind.Int, Default: "{min_nodes}"), new("max_nodes", "Max nodes", FieldKind.Int, Default: "{max_nodes}") },
            }),
        },
    };

    public static readonly ResourceDef Block = new()
    {
        Title = "Block Storage", Group = "Storage", Path = "/blocks", Key = "blocks", Icon = "▥", TypeToDelete = true,
        Columns = new Col[] { new("label", "Label", 170), new("region", "Region", 60), new("size_gb", "GB", 60), new("block_type", "Type", 90), new("status", "Status", 70), new("attached_to_instance", "Attached to", 260), new("mount_id", "Mount ID", 200) },
        Create = new Field[] { RegionF, new("size_gb", "Size (GB)", FieldKind.Int, true, "40"), Label, new("block_type", "Type", FieldKind.Select, Default: "high_perf", Options: new[] { "high_perf", "storage_opt" }) },
        Update = new Field[] { new("label", "Label", Default: "{label}"), new("size_gb", "Size (GB, grow only)", FieldKind.Int, Default: "{size_gb}") },
        Actions = new ActionDef[]
        {
            new("Attach…", "POST", "/blocks/{id}/attach", new[] { new Field("instance_id", "Instance", FieldKind.Lookup, true, Lookup: Instances), new Field("live", "Live (no reboot)", FieldKind.Bool, Default: "true") }),
            new("Detach", "POST", "/blocks/{id}/detach", new[] { new Field("live", "Live (no reboot)", FieldKind.Bool, Default: "true") }, Confirm: "Detach {label}? Unmount it on the server first."),
        },
    };

    public static readonly ResourceDef BlockSnapshot = new()
    {
        Title = "Block snapshots", Group = "Storage", Path = "/blocks/snapshots", Key = "snapshots", NameField = "description", Icon = "◔", UpdateMethod = "PUT",
        ItemPath = "/blocks/snapshots/{id}", StatusField = "state",
        Description = "Point-in-time snapshots of block storage volumes (billed per GB per month).",
        Columns = new Col[] { new("description", "Description", 200), new("block_id", "Volume", 230), new("size", "GB", 50), new("state", "State", 80), new("added_at", "Created", 150), new("next_invoice_price", "Next invoice", 80), new("id", "Id", 230) },
        Create = new Field[] { new("block_id", "Volume", FieldKind.Lookup, true, Lookup: Blocks), new("description", "Description", Required: true, Default: "{now}") },
        CreateConfirm = "Snapshot that volume? Snapshots are billed per GB.",
        Update = new Field[] { new("description", "Description", Required: true, Default: "{description}") },
    };

    public static readonly ResourceDef Vfs = new()
    {
        Title = "File System (VFS)", Group = "Storage", Path = "/vfs", Key = "vfs", NameField = "label", Icon = "⛃", UpdateMethod = "PUT", TypeToDelete = true, StatusField = "status",
        Description = "Vultr File System: shared NFS-style storage that several instances in one region can mount at once.",
        Columns = new Col[] { new("label", "Label", 170), new("region", "Region", 60), new("storage_size.gb", "GB", 60), new("storage_used.gb", "Used GB", 65), new("disk_type", "Disk", 60), new("status", "Status", 80), new("billing.monthly", "Monthly $", 70), new("date_created", "Created", 150), new("id", "Id", 230) },
        Create = new Field[] { RegionF, new("label", "Label", Required: true), new("storage_size", "Size (JSON)", FieldKind.Json, true, "{\"gb\": 40}"), new("disk_type", "Disk type", FieldKind.Select, Default: "nvme", Options: new[] { "nvme", "hdd" }), Tags },
        CreateConfirm = "This CREATES billed Vultr file system storage “{form.label}”. Continue?",
        Update = new Field[] { new("label", "Label", Default: "{label}"), new("storage_size", "Size (JSON, grow only)", FieldKind.Json, Default: "{json:storage_size}") },
        Actions = new ActionDef[]
        {
            new("Attach to instance…", "PUT", "/vfs/{id}/attachments/{form.instance_id}", new[] { new Field("instance_id", "Instance", FieldKind.Lookup, true, Lookup: Instances, InPath: true) },
                Confirm: "Attach {label} to the instance? Mount it inside the server with the mount tag shown under Attachments."),
        },
        Children = new[]
        {
            ("Attachments", new ResourceDef
            {
                Title = "Attachments", Group = "Storage", Path = "/vfs/{parent.id}/attachments", Key = "attachments", IdField = "target_id", NameField = "target_id",
                ItemPath = "/vfs/{parent.id}/attachments/{target_id}",
                Columns = new Col[] { new("target_id", "Instance", 230), new("state", "State", 80), new("mount_tag", "Mount tag", 80) },
            }),
        },
    };

    public static readonly ResourceDef ObjectStorage = new()
    {
        Title = "Object Storage", Group = "Storage", Path = "/object-storage", Key = "object_storages", Icon = "◫",
        Columns = new Col[] { new("label", "Label", 170), new("region", "Region", 60), new("status", "Status", 70), new("s3_hostname", "S3 endpoint", 240), new("date_created", "Created", 160) },
        Create = new Field[] { new("cluster_id", "Cluster", FieldKind.Lookup, true, Lookup: new("/object-storage/clusters", "clusters", "id", "hostname", "region")), new("tier_id", "Tier id", FieldKind.Int, Hint: "optional; see the cluster's tiers"), Label },
        Update = new Field[] { new("label", "Label", Default: "{label}") },
        UpdateMethod = "PUT",
        Actions = new ActionDef[]
        {
            new("Regenerate keys", "POST", "/object-storage/{id}/regenerate-keys", Confirm: "Regenerate the S3 keys for {label}? Apps using the old keys stop working.", Danger: true, ShowResult: true),
        },
    };

    public static readonly ResourceDef Snapshot = new()
    {
        Title = "Snapshots", Group = "Storage", Path = "/snapshots", Key = "snapshots", NameField = "description", Icon = "◔",
        Columns = new Col[] { new("description", "Description", 220), new("status", "Status", 80), new("size", "Size", 100), new("compressed_size", "Compressed", 100), new("os_id", "OS id", 60), new("date_created", "Created", 170) },
        Create = new Field[] { new("instance_id", "Instance", FieldKind.Lookup, true, Lookup: Instances), new("description", "Description") },
        Update = new Field[] { new("description", "Description", Default: "{description}") },
        UpdateMethod = "PUT",
        Actions = new ActionDef[]
        {
            new("Create from URL…", "POST", "/snapshots/create-from-url", new[] { new Field("url", "Raw image URL", Required: true), new Field("description", "Description"), new Field("uefi", "UEFI", FieldKind.Bool) }, NoRow: true),
        },
    };

    public static readonly ResourceDef Backup = new()
    {
        Title = "Backups", Group = "Storage", Path = "/backups", Key = "backups", NameField = "description", ReadOnly = true, CanDelete = false, Icon = "◷",
        Columns = new Col[] { new("description", "Description", 260), new("status", "Status", 80), new("size", "Size", 100), new("date_created", "Created", 170) },
        Description = "Automatic backups. Restore one from Instances → Restore….",
    };

    public static readonly ResourceDef Iso = new()
    {
        Title = "ISOs", Group = "Storage", Path = "/iso", Key = "isos", NameField = "filename", Icon = "◉",
        Columns = new Col[] { new("filename", "File", 240), new("size", "Size", 100), new("status", "Status", 80), new("md5sum", "MD5", 220), new("date_created", "Created", 160) },
        Create = new Field[] { new("url", "ISO URL", Required: true, Hint: "Vultr downloads it from this address") },
    };

    public static readonly ResourceDef PublicIso = new()
    {
        Title = "Public ISOs", Group = "Reference", Path = "/iso-public", Key = "public_isos", NameField = "name", ReadOnly = true, CanDelete = false, Icon = "◌",
        Columns = new Col[] { new("name", "Name", 220), new("description", "Description", 320), new("md5sum", "MD5", 220) },
    };

    public static readonly ResourceDef Vpc = new()
    {
        Title = "VPC Networks", Group = "Network", Path = "/vpcs", Key = "vpcs", NameField = "description", Icon = "⬡",
        Columns = new Col[] { new("description", "Description", 200), new("region", "Region", 60), new("v4_subnet", "Subnet", 120), new("v4_subnet_mask", "Mask", 50), new("date_created", "Created", 170) },
        Create = new Field[] { RegionF, new("description", "Description"), new("v4_subnet", "IPv4 subnet", Hint: "e.g. 10.10.0.0 (empty = auto)"), new("v4_subnet_mask", "Mask bits", FieldKind.Int, Default: "24") },
        Update = new Field[] { new("description", "Description", Default: "{description}") },
        UpdateMethod = "PUT",
        Children = new[]
        {
            ("Attachments", new ResourceDef { Title = "Attachments", Path = "/vpcs/{parent.id}/attachments", Key = "attachments", ReadOnly = true, CanDelete = false,
                Columns = new Col[] { new("label", "Label", 180), new("id", "Id", 260), new("type", "Type", 80) } }),
        },
    };

    public static readonly ResourceDef ReservedIp = new()
    {
        Title = "Reserved IPs", Group = "Network", Path = "/reserved-ips", Key = "reserved_ips", Icon = "⌖",
        Columns = new Col[] { new("label", "Label", 160), new("subnet", "IP", 160), new("ip_type", "Type", 50), new("region", "Region", 60), new("instance_id", "Attached to", 260) },
        Create = new Field[] { RegionF, new("ip_type", "Type", FieldKind.Select, true, "v4", new[] { "v4", "v6" }), Label },
        Update = new Field[] { new("label", "Label", Default: "{label}") },
        Actions = new ActionDef[]
        {
            new("Attach…", "POST", "/reserved-ips/{id}/attach", new[] { new Field("instance_id", "Instance", FieldKind.Lookup, true, Lookup: Instances) }),
            new("Detach", "POST", "/reserved-ips/{id}/detach", Confirm: "Detach {subnet}?"),
            new("Convert instance IP…", "POST", "/reserved-ips/convert", new[] { new Field("ip_address", "Existing instance IP", Required: true), new Field("label", "Label") }, NoRow: true),
        },
    };

    private static readonly ResourceDef FirewallRule = new()
    {
        Title = "Rules", Path = "/firewalls/{parent.id}/rules", Key = "firewall_rules", NameField = "notes",
        Columns = new Col[] { new("id", "#", 40), new("action", "Action", 60), new("ip_type", "IP", 40), new("protocol", "Proto", 50), new("port", "Port", 80), new("subnet", "Subnet", 130), new("subnet_size", "Size", 45), new("source", "Source", 90), new("notes", "Notes", 200) },
        Create = new Field[]
        {
            new("ip_type", "IP version", FieldKind.Select, true, "v4", new[] { "v4", "v6" }),
            new("protocol", "Protocol", FieldKind.Select, true, "tcp", new[] { "tcp", "udp", "icmp", "gre", "esp", "ah" }),
            new("port", "Port or range", Hint: "22 or 8000:8100"), new("subnet", "Source subnet", Default: "0.0.0.0", Hint: "use :: for any IPv6"),
            new("subnet_size", "Prefix length", FieldKind.Int, Default: "0", Hint: "0 = anywhere, 32 = one IPv4 address"),
            new("source", "Special source", FieldKind.Select, Options: new[] { "", "cloudflare" }), new("notes", "Notes"),
        },
    };

    public static readonly ResourceDef Firewall = new()
    {
        Title = "Firewalls", Group = "Network", Path = "/firewalls", Key = "firewall_groups", NameField = "description", Icon = "⛨",
        Columns = new Col[] { new("description", "Description", 220), new("instance_count", "Instances", 70), new("rule_count", "Rules", 50), new("max_rule_count", "Max", 50), new("date_modified", "Modified", 170) },
        Create = new Field[] { new("description", "Description", Required: true) },
        Update = new Field[] { new("description", "Description", Default: "{description}") },
        UpdateMethod = "PUT",
        Children = new[] { ("Rules", FirewallRule) },
    };

    private static readonly ResourceDef DnsRecord = new()
    {
        Title = "Records", Path = "/domains/{parent.domain}/records", Key = "records", NameField = "name",
        Columns = new Col[] { new("type", "Type", 60), new("name", "Name", 160), new("data", "Data", 300), new("ttl", "TTL", 60), new("priority", "Prio", 45) },
        Create = new Field[]
        {
            new("type", "Type", FieldKind.Select, true, "A", new[] { "A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV", "CAA", "SSHFP" }),
            new("name", "Name", Hint: "empty = the domain itself"), new("data", "Data", Required: true),
            new("ttl", "TTL", FieldKind.Int, Default: "300"), new("priority", "Priority", FieldKind.Int, Default: "0"),
        },
        Update = new Field[] { new("name", "Name", Default: "{name}"), new("data", "Data", Default: "{data}"), new("ttl", "TTL", FieldKind.Int, Default: "{ttl}"), new("priority", "Priority", FieldKind.Int, Default: "{priority}") },
    };

    public static readonly ResourceDef Dns = new()
    {
        Title = "DNS", Group = "Network", Path = "/domains", Key = "domains", IdField = "domain", NameField = "domain", Icon = "≋",
        Columns = new Col[] { new("domain", "Domain", 240), new("dns_sec", "DNSSEC", 70), new("date_created", "Created", 170) },
        Create = new Field[] { new("domain", "Domain", Required: true), new("ip", "Default A record IP", Hint: "optional"), new("dns_sec", "DNSSEC", FieldKind.Select, Default: "disabled", Options: new[] { "disabled", "enabled" }) },
        Update = new Field[] { new("dns_sec", "DNSSEC", FieldKind.Select, true, "{dns_sec}", new[] { "disabled", "enabled" }) },
        UpdateMethod = "PUT",
        Actions = new ActionDef[]
        {
            new("SOA", "GET", "/domains/{domain}/soa", ShowResult: true),
            new("DNSSEC info", "GET", "/domains/{domain}/dnssec", ShowResult: true),
        },
        Children = new[] { ("Records", DnsRecord) },
    };

    public static readonly ResourceDef LoadBalancer = new()
    {
        Title = "Load Balancers", Group = "Network", Path = "/load-balancers", Key = "load_balancers", Icon = "⇶",
        Columns = new Col[] { new("label", "Label", 170), new("region", "Region", 60), new("status", "Status", 80), new("ipv4", "IPv4", 120), new("ipv6", "IPv6", 200), new("instances", "Instances", 220) },
        Create = new Field[]
        {
            RegionF, Label, new("instances", "Instances", FieldKind.MultiLookup, Lookup: Instances),
            new("forwarding_rules", "Forwarding rules (JSON)", FieldKind.Json, true, """[{"frontend_protocol": "http", "frontend_port": 80, "backend_protocol": "http", "backend_port": 80}]"""),
            new("health_check", "Health check (JSON)", FieldKind.Json, Default: """{"protocol": "http", "port": 80, "path": "/", "check_interval": 15, "response_timeout": 5, "unhealthy_threshold": 5, "healthy_threshold": 5}"""),
            new("balancing_algorithm", "Algorithm", FieldKind.Select, Default: "roundrobin", Options: new[] { "roundrobin", "leastconn" }),
            new("ssl_redirect", "Redirect HTTP→HTTPS", FieldKind.Bool), new("proxy_protocol", "PROXY protocol", FieldKind.Bool),
        },
        Update = new Field[] { new("label", "Label", Default: "{label}"), new("instances", "Instances", FieldKind.MultiLookup, Lookup: Instances) },
        Children = new[]
        {
            ("Forwarding rules", new ResourceDef { Title = "Forwarding rules", Path = "/load-balancers/{parent.id}/forwarding-rules", Key = "forwarding_rules",
                Columns = new Col[] { new("frontend_protocol", "Front", 60), new("frontend_port", "Port", 50), new("backend_protocol", "Back", 60), new("backend_port", "Port", 50), new("id", "Id", 260) },
                Create = new Field[] { new("frontend_protocol", "Frontend protocol", FieldKind.Select, true, "http", new[] { "http", "https", "tcp" }), new("frontend_port", "Frontend port", FieldKind.Int, true, "80"),
                    new("backend_protocol", "Backend protocol", FieldKind.Select, true, "http", new[] { "http", "https", "tcp" }), new("backend_port", "Backend port", FieldKind.Int, true, "80") } }),
            ("Firewall rules", new ResourceDef { Title = "Firewall rules", Path = "/load-balancers/{parent.id}/firewall-rules", Key = "firewall_rules", ReadOnly = true, CanDelete = false,
                Columns = new Col[] { new("port", "Port", 60), new("source", "Source", 160), new("ip_type", "IP", 40), new("id", "Id", 260) } }),
        },
    };

    public static readonly ResourceDef Database = new()
    {
        Title = "Databases", Group = "Databases", Path = "/databases", Key = "databases", Icon = "⛁", TypeToDelete = true,
        Columns = new Col[] { new("label", "Label", 160), new("database_engine", "Engine", 70), new("database_engine_version", "Ver", 45), new("region", "Region", 60), new("plan", "Plan", 170), new("status", "Status", 80), new("host", "Host", 240), new("port", "Port", 55) },
        Create = new Field[]
        {
            new("database_engine", "Engine", FieldKind.Select, true, "pg", new[] { "pg", "mysql", "valkey", "kafka" }),
            new("database_engine_version", "Version", Required: true, Default: "16", Hint: "e.g. pg 16, mysql 8, valkey 7, kafka 3.8"),
            RegionF, new("plan", "Plan", FieldKind.Lookup, true, Lookup: new("/databases/plans", "plans", "id", "id", "number_of_nodes", "vcpu_count", "ram", "monthly_cost")),
            new("label", "Label", Required: true), new("tag", "Tag"), new("vpc_id", "VPC", FieldKind.Lookup, Lookup: Vpcs),
            new("trusted_ips", "Trusted IPs", FieldKind.List, Hint: "comma separated CIDRs (empty = anyone)"),
        },
        Update = new Field[] { new("label", "Label", Default: "{label}"), new("tag", "Tag", Default: "{tag}"), new("trusted_ips", "Trusted IPs", FieldKind.List, Default: "{trusted_ips}"),
            new("plan", "Plan", FieldKind.Lookup, Lookup: new("/databases/plans", "plans", "id", "id", "number_of_nodes", "vcpu_count", "ram", "monthly_cost")) },
        Actions = new ActionDef[]
        {
            new("Usage", "GET", "/databases/{id}/usage", ShowResult: true),
            new("Backups", "GET", "/databases/{id}/backups", ShowResult: true),
            new("Alerts", "GET", "/databases/{id}/alerts", ShowResult: true),
            new("Maintenance updates", "GET", "/databases/{id}/maintenance", ShowResult: true),
            new("Start maintenance", "POST", "/databases/{id}/maintenance", Confirm: "Start pending maintenance updates on {label} now?"),
            new("Add read replica…", "POST", "/databases/{id}/read-replica", new[] { RegionF, new Field("label", "Label", Required: true) }),
            new("Fork…", "POST", "/databases/{id}/fork", new[] { new Field("label", "Label", Required: true), RegionF, new Field("plan", "Plan", Required: true, Default: "{plan}") }),
        },
        Children = new[]
        {
            ("Users", new ResourceDef { Title = "Users", Path = "/databases/{parent.id}/users", Key = "users", IdField = "username", NameField = "username",
                Columns = new Col[] { new("username", "User", 160), new("encryption", "Encryption", 140) },
                Create = new Field[] { new("username", "Username", Required: true), new("password", "Password (empty = generated)", FieldKind.Password) },
                Update = new Field[] { new("password", "New password", FieldKind.Password, true) } }),
            ("Logical DBs", new ResourceDef { Title = "Logical DBs", Path = "/databases/{parent.id}/dbs", Key = "dbs", IdField = "name", NameField = "name",
                Columns = new Col[] { new("name", "Name", 200) }, Create = new Field[] { new("name", "Name", Required: true) } }),
            ("Connection pools", new ResourceDef { Title = "Connection pools", Path = "/databases/{parent.id}/connection-pools", Key = "connection_pools", IdField = "name", NameField = "name",
                Columns = new Col[] { new("name", "Name", 140), new("database", "DB", 120), new("username", "User", 120), new("mode", "Mode", 90), new("size", "Size", 50) },
                Create = new Field[] { new("name", "Name", Required: true), new("database", "Database", Required: true), new("username", "User", Required: true), new("mode", "Mode", FieldKind.Select, true, "transaction", new[] { "session", "transaction", "statement" }), new("size", "Size", FieldKind.Int, true, "10") } }),
        },
    };

    public static readonly ResourceDef Registry = new()
    {
        Title = "Container Registry", Group = "Databases", Path = "/registries", Key = "registries", NameField = "name", ItemPath = "/registry/{id}", CreatePath = "/registry", Icon = "⧉",
        Columns = new Col[] { new("name", "Name", 160), new("urn", "URN", 260), new("public", "Public", 55), new("metadata.region.name", "Region", 80), new("storage.used.gb_used", "GB used", 70) },
        Create = new Field[] { new("name", "Name", Required: true, Hint: "lowercase letters and digits"), new("region", "Region", Required: true, Default: "ewr"), new("plan", "Plan", FieldKind.Select, true, "start_up", new[] { "start_up", "business", "premium", "enterprise" }), new("public", "Public", FieldKind.Bool) },
        Update = new Field[] { new("public", "Public", FieldKind.Bool), new("plan", "Plan", FieldKind.Select, Options: new[] { "", "start_up", "business", "premium", "enterprise" }) },
        UpdateMethod = "PUT",
        Actions = new ActionDef[]
        {
            new("Docker credentials", "OPTIONS", "/registry/{id}/docker-credentials?expiry_seconds=3600&read_write=true", ShowResult: true, Tip: "Temporary docker login credentials (1 hour)"),
        },
        Children = new[]
        {
            ("Repositories", new ResourceDef { Title = "Repositories", Path = "/registry/{parent.id}/repositories", Key = "repositories", IdField = "image", NameField = "name", ItemPath = "/registry/{parent.id}/repository/{image}",
                Columns = new Col[] { new("name", "Name", 200), new("image", "Image", 160), new("artifact_count", "Artifacts", 70), new("pull_count", "Pulls", 60), new("updated_at", "Updated", 170) } }),
        },
    };

    public static readonly ResourceDef SshKey = new()
    {
        Title = "SSH Keys", Group = "Account", Path = "/ssh-keys", Key = "ssh_keys", NameField = "name", Icon = "⚿",
        Columns = new Col[] { new("name", "Name", 200), new("ssh_key", "Key", 420), new("date_created", "Created", 170) },
        Create = new Field[] { new("name", "Name", Required: true), new("ssh_key", "Public key", FieldKind.Multiline, true) },
        Update = new Field[] { new("name", "Name", Default: "{name}"), new("ssh_key", "Public key", FieldKind.Multiline, Default: "{ssh_key}") },
        Actions = new ActionDef[] { new("Upload from vault…", "", "", Special: "uploadkey", NoRow: true, Tip: "Send the public half of a key from your local vault") },
    };

    public static readonly ResourceDef StartupScript = new()
    {
        Title = "Startup Scripts", Group = "Account", Path = "/startup-scripts", Key = "startup_scripts", NameField = "name", Icon = "⚙",
        Columns = new Col[] { new("name", "Name", 220), new("type", "Type", 60), new("date_modified", "Modified", 170) },
        Create = new Field[] { new("name", "Name", Required: true), new("type", "Type", FieldKind.Select, true, "boot", new[] { "boot", "pxe" }), new("script", "Script", FieldKind.Base64, true, "#!/bin/bash\n") },
        Update = new Field[] { new("name", "Name", Default: "{name}"), new("script", "Script (leave as is to keep)", FieldKind.Base64) },
        Actions = new ActionDef[] { new("View script", "GET", "/startup-scripts/{id}", Special: "script") },
    };

    public static readonly ResourceDef User = new()
    {
        Title = "Users", Group = "Account", Path = "/users", Key = "users", NameField = "email", Icon = "☺",
        Columns = new Col[] { new("email", "Email", 220), new("name", "Name", 160), new("api_enabled", "API", 45), new("acls", "Permissions", 360) },
        Create = new Field[]
        {
            new("email", "Email", Required: true), new("name", "Name", Required: true), new("password", "Password", FieldKind.Password, true),
            new("api_enabled", "API access", FieldKind.Bool),
            new("acls", "Permissions", FieldKind.List, Hint: "manage_users, subscriptions_view, subscriptions, billing, support, provisioning, dns, abuse, upgrade, firewall, alerts, objstore, loadbalancer"),
        },
        Update = new Field[] { new("name", "Name", Default: "{name}"), new("api_enabled", "API access", FieldKind.Bool), new("acls", "Permissions", FieldKind.List, Default: "{acls}") },
    };

    public static readonly ResourceDef Account = new()
    {
        Title = "Account", Group = "Account", Path = "/account", Key = "account", Single = true, ReadOnly = true, CanDelete = false, Icon = "◎",
        Columns = new Col[] { new("name", "Name", 160), new("email", "Email", 220), new("balance", "Balance", 80), new("pending_charges", "Pending", 80), new("last_payment_date", "Last payment", 170), new("last_payment_amount", "Amount", 70), new("acls", "Permissions", 300) },
        Actions = new ActionDef[] { new("Bandwidth pool", "GET", "/account/bandwidth", ShowResult: true, NoRow: true) },
    };

    public static readonly ResourceDef BillingHistory = new()
    {
        Title = "Billing History", Group = "Account", Path = "/billing/history", Key = "billing_history", NameField = "description", ReadOnly = true, CanDelete = false, Icon = "≡",
        Columns = new Col[] { new("date", "Date", 170), new("type", "Type", 80), new("description", "Description", 320), new("amount", "Amount", 80), new("balance", "Balance", 80) },
    };

    public static readonly ResourceDef Invoices = new()
    {
        Title = "Invoices", Group = "Account", Path = "/billing/invoices", Key = "billing_invoices", NameField = "description", ReadOnly = true, CanDelete = false, Icon = "≡",
        Columns = new Col[] { new("id", "Id", 90), new("date", "Date", 170), new("description", "Description", 280), new("amount", "Amount", 80), new("balance", "Balance", 80) },
        Children = new[]
        {
            ("Items", new ResourceDef { Title = "Items", Path = "/billing/invoices/{parent.id}/items", Key = "invoice_items", ReadOnly = true, CanDelete = false, NameField = "description",
                Columns = new Col[] { new("description", "Description", 300), new("product", "Product", 140), new("units", "Units", 60), new("unit_type", "Unit", 60), new("unit_price", "Price", 70), new("total", "Total", 70), new("start_date", "From", 170), new("end_date", "To", 170) } }),
        },
    };

    public static readonly ResourceDef PendingCharges = new()
    {
        Title = "Pending Charges", Group = "Account", Path = "/billing/pending-charges", Key = "pending_charges", NameField = "description", ReadOnly = true, CanDelete = false, Icon = "≡",
        Columns = new Col[] { new("description", "Description", 300), new("product", "Product", 160), new("units", "Units", 60), new("unit_type", "Unit", 60), new("unit_price", "Price", 70), new("total", "Total", 70) },
    };

    public static readonly ResourceDef CdnPull = new()
    {
        Title = "CDN Pull Zones", Group = "Network", Path = "/cdns/pull-zones", Key = "pull_zones", Icon = "☁",
        Columns = new Col[] { new("label", "Label", 160), new("cdn_url", "CDN URL", 240), new("origin_domain", "Origin", 200), new("origin_scheme", "Scheme", 60), new("status", "Status", 70) },
        Create = new Field[] { new("label", "Label", Required: true), new("origin_scheme", "Origin scheme", FieldKind.Select, true, "https", new[] { "https", "http" }), new("origin_domain", "Origin domain", Required: true), new("vanity_domain", "Vanity domain"), new("cors", "CORS", FieldKind.Bool), new("gzip", "Gzip", FieldKind.Bool, Default: "true") },
        Actions = new ActionDef[] { new("Purge cache", "DELETE", "/cdns/pull-zones/{id}/purge", Confirm: "Purge all cached files of {label}?") },
    };

    public static readonly ResourceDef CdnPush = new()
    {
        Title = "CDN Push Zones", Group = "Network", Path = "/cdns/push-zones", Key = "push_zones", Icon = "☁",
        Columns = new Col[] { new("label", "Label", 160), new("cdn_url", "CDN URL", 240), new("status", "Status", 70) },
        Create = new Field[] { new("label", "Label", Required: true), new("vanity_domain", "Vanity domain") },
    };

    public static readonly ResourceDef Inference = new()
    {
        Title = "Serverless Inference", Group = "Compute", Path = "/inference", Key = "subscriptions", Icon = "◈",   // ✦ is the app's own AI mark
        Columns = new Col[] { new("label", "Label", 180), new("api_key", "API key", 260), new("date_created", "Created", 170) },
        Create = new Field[] { new("label", "Label", Required: true) },
        Update = new Field[] { new("label", "Label", Default: "{label}") },
        Actions = new ActionDef[] { new("Usage", "GET", "/inference/{id}/usage", ShowResult: true) },
    };

    public static readonly ResourceDef RegionList = new()
    {
        Title = "Regions", Group = "Reference", Path = "/regions", Key = "regions", NameField = "city", ReadOnly = true, CanDelete = false, Icon = "◍",
        Columns = new Col[] { new("id", "Id", 60), new("city", "City", 140), new("country", "Country", 60), new("continent", "Continent", 110), new("options", "Options", 300) },
    };

    public static readonly ResourceDef PlanList = new()
    {
        Title = "Plans", Group = "Reference", Path = "/plans?type=all", Key = "plans", NameField = "id", ReadOnly = true, CanDelete = false, Icon = "◍",
        Columns = new Col[] { new("id", "Id", 170), new("type", "Type", 60), new("vcpu_count", "vCPU", 45), new("ram", "RAM MB", 65), new("disk", "Disk GB", 60), new("bandwidth", "BW GB", 60), new("monthly_cost", "$/mo", 55), new("locations", "Regions", 300) },
    };

    public static readonly ResourceDef OsList = new()
    {
        Title = "OS Images", Group = "Reference", Path = "/os", Key = "os", NameField = "name", ReadOnly = true, CanDelete = false, Icon = "◍",
        Columns = new Col[] { new("id", "Id", 60), new("name", "Name", 260), new("arch", "Arch", 60), new("family", "Family", 100) },
    };

    public static readonly ResourceDef AppList = new()
    {
        Title = "Marketplace Apps", Group = "Reference", Path = "/applications", Key = "applications", NameField = "deploy_name", ReadOnly = true, CanDelete = false, Icon = "◍",
        Columns = new Col[] { new("id", "Id", 60), new("deploy_name", "Name", 260), new("type", "Type", 80), new("vendor", "Vendor", 120), new("image_id", "Image id", 160) },
    };

    public static readonly ResourceDef SubAccount = new()
    {
        Title = "Sub-accounts", Group = "Account", Path = "/subaccounts", Key = "subaccounts", NameField = "subaccount_name", Icon = "☷", CanDelete = false,
        Description = "Sub-accounts of this (reseller / enterprise) account. Vultr's API can list and create them; removing one is done in the portal.",
        Columns = new Col[] { new("subaccount_name", "Name", 180), new("email", "E-mail", 200), new("subaccount_id", "Your id", 100), new("activated", "Active", 55), new("balance", "Balance", 70), new("pending_charges", "Pending", 70), new("id", "Id", 230) },
        Create = new Field[] { new("email", "E-mail", Required: true), new("subaccount_name", "Name"), new("subaccount_id", "Your own id") },
    };

    public static readonly ResourceDef[] All =
    {
        Instance, BareMetal, Kubernetes, Inference,
        Block, BlockSnapshot, Vfs, ObjectStorage, Snapshot, Backup, Iso,
        Firewall, Vpc, ReservedIp, Dns, LoadBalancer, CdnPull, CdnPush,
        Database, Registry,
        Account, SubAccount, SshKey, StartupScript, User, BillingHistory, Invoices, PendingCharges,
        RegionList, PlanList, OsList, AppList, PublicIso,
    };

    public static readonly string[] GroupOrder = { "Compute", "Storage", "Network", "Databases", "Account", "Reference" };
}
