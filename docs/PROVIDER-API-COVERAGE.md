# Provider API coverage — what each management API offers vs. what ServerManagement exposes

**Written:** 2026-10-19, version 0.1.14 (ADR-031). **Ask (DarkCipher0x):** "implement more server controls with the infrastructure API to
cover everything that each service has to offer".

Every row below was checked against the provider's own reference, never guessed:

| Provider | Source checked (2026-10-19) |
|---|---|
| Proxmox VE | `pve.proxmox.com/pve-docs/api-viewer/apidoc.js` (the API schema the PVE API viewer is built from): paths, parameters, required privileges |
| VMware vCenter | Broadcom developer portal, *vSphere Automation API* (latest = 8.0 U3): operation index for `vcenter/vm*` and `vcenter/host`, the operation pages for hardware CPU / memory, guest identity, tools and VM delete |
| Microsoft Hyper-V | Hyper-V PowerShell module cmdlet reference (Microsoft Learn): `Set-VMProcessor`, `Set-VMMemory`, `Rename-VM`, `Connect-VMNetworkAdapter`, `Remove-VM`, `Rename-VMSnapshot`, `Get-VMIntegrationService`, `Get-VMHardDiskDrive` |
| Docker Engine | Docker Engine API (v1.47): containers pause / unpause / kill / top / stats / json, `DELETE /images`, `/networks`, `/volumes`; docker CLI equivalents over SSH |
| Unraid | The official schema `unraid/api` → `api/generated-schema.graphql` (main, 2026-10) |
| Hetzner Cloud | `docs.hetzner.cloud/cloud.spec.json` (178 non-deprecated operations) |
| Contabo | `api.contabo.com/api-v1.yaml` (158 operations) |
| Vultr | Vultr's official Go client `vultr/govultr` (master, 2026-10) — the OpenAPI file at vultr.com/api is behind a bot challenge, so the client's request paths and bodies were used |

Legend: ✅ in the app before 0.1.14 · 🆕 added in 0.1.14 · ⛔ not offered, with the reason · 👁 read-only view.
Every 🆕 change follows ADR-007 / ADR-020: offered only with **Allow changes** (infrastructure) or a write-capable token (cloud),
disabled with the reason when the credential or the object's state forbids it, confirmed with target / environment /
consequence, **typed confirmation** for data loss (and in PRODUCTION), written to the audit log, and reported **verified** only
after the app re-read the object and saw the promised state.

---

## 1. Proxmox VE (REST API, API token)

| Area | API | Before | 0.1.14 | Verified by |
|---|---|---|---|---|
| Guest power | `status/start · shutdown · reboot · stop` (VM.PowerMgmt) | ✅ | ✅ | task OK + `status/current` |
| Pause / resume (VM) | `qemu/{vmid}/status/suspend · resume` (VM.PowerMgmt) | — | 🆕 | task OK + `qmpstatus` paused / running |
| Pause / resume (CT) | `lxc/{vmid}/status/suspend · resume` | — | ⛔ | experimental in PVE (CRIU); not offered |
| Snapshots | `GET/POST {type}/{vmid}/snapshot`, `DELETE …/snapshot/{name}`, `POST …/{name}/rollback` (VM.Snapshot / VM.Snapshot.Rollback) | — | 🆕 list 👁, take (VM: with RAM optional), roll back (typed), delete (typed) | snapshot listed / gone; rollback = `current.parent` is the snapshot |
| Clone | `POST {type}/{vmid}/clone` (VM.Clone), `GET cluster/nextid` | — | 🆕 (full or linked; empty VMID = next free) | task OK + new guest's `status/current` |
| Migrate | `POST {type}/{vmid}/migrate` (VM.Migrate) | — | 🆕 (VM online, CT restart) | task OK + `cluster/resources` shows the target node |
| Backup now | `POST nodes/{node}/vzdump` (VM.Backup + Datastore.AllocateSpace) | — | 🆕 (snapshot / suspend / stop, zstd / gzip / lzo / none) | task OK + a new `vzdump-*` volume of that VMID on the storage |
| Convert to template | `POST {type}/{vmid}/template` (VM.Allocate) | — | 🆕 (stopped only, typed) | `config.template = 1` |
| Guest agent | `qemu/{vmid}/agent/get-osinfo · get-host-name · network-get-interfaces` | — | 🆕 👁 | — |
| Node tasks | `GET nodes/{node}/tasks` | — | 🆕 👁 (last 50) | — |
| Storage contents | `GET nodes/{node}/storage/{storage}/content` | — | 🆕 👁 | — |
| Templates | — | start refused | 🆕 clone / backup only | — |
| Disk resize, hardware config (`PUT …/config`), create VM/CT | `PUT …/resize`, `POST nodes/{node}/qemu` | — | ⛔ | dozens of free-form parameters per guest type; a form that can't be verified field by field would be a fake control |
| Node reboot / shutdown | `POST nodes/{node}/status` (Sys.PowerMgmt) | — | ⛔ | can't be verified (the API answering is the thing that goes away) and takes every guest with it |
| HA, SDN, firewall, users/ACL, Ceph, apt, disks | `/cluster/ha`, `/cluster/sdn`, `…/firewall`, `/access`, `/nodes/{node}/ceph`, `/apt`, `/disks` | — | ⛔ | cluster administration, out of the workstation's scope; use the PVE web UI |
| Console (VNC / xterm.js) | `vncproxy`, `termproxy` | — | ⛔ | needs a VNC-over-WebSocket client; SSH to the node from the app instead |

Privileges are read from `/access/permissions` per path (`/vms/<id>` → `/vms` → `/`): each action shows the missing privilege
by name. A token with none of VM.PowerMgmt / Snapshot / Clone / Migrate / Backup / Allocate is read-only.

## 2. VMware vCenter (vSphere Automation REST API, `/api`)

| Area | API | Before | 0.1.14 | Verified by |
|---|---|---|---|---|
| Power | `POST /vcenter/vm/{vm}/power?action=start·stop·suspend·reset`, `…/guest/power?action=shutdown·reboot` | ✅ | ✅ | `GET …/power` |
| Clone | `POST /vcenter/vm?action=clone` | ✅ | ✅ | new VM's name |
| vCPU count | `PATCH /vcenter/vm/{vm}/hardware/cpu {count}` | — | 🆕 | `GET …/hardware/cpu` |
| Memory | `PATCH /vcenter/vm/{vm}/hardware/memory {size_MiB}` | — | 🆕 | `GET …/hardware/memory` |
| Delete VM | `DELETE /vcenter/vm/{vm}` (powered off; VirtualMachine.Inventory.Delete) | — | 🆕 typed | `GET` answers 404 |
| Guest info | `GET …/guest/identity`, `…/guest/networking/interfaces`, `…/tools` | — | 🆕 👁 | — |
| Host disconnect / reconnect | `POST /vcenter/host/{host}?action=disconnect·connect` | — | 🆕 | `connection_state` |
| **Snapshots** | — | — | ⛔ | **not in the vSphere Automation REST API** (the operation index has no snapshot service; snapshots exist only in the SOAP Web Services API) |
| Maintenance mode | — | — | ⛔ | not in the REST API (SOAP only) |
| Disks, NICs, CD-ROM, boot order | `…/hardware/disk · ethernet · cdrom · boot` | — | ⛔ | device-level editing with many interdependent fields; candidates for a later release with their own verification |
| Relocate / vMotion, instant clone, content-library deploy, guest customization, guest file/process ops | `?action=relocate`, `?action=instant-clone`, `/vcenter/vm-template`, `…/guest/customization`, `…/guest/processes` | — | ⛔ | placement and in-guest execution are beyond this release; guest processes would run commands in a guest with stored credentials |
| Console tickets | `POST …/console/tickets` | — | ⛔ | needs VMRC or a WebMKS client |
| Standalone ESXi | — | — | ⛔ | ESXi has no Automation API |

## 3. Microsoft Hyper-V (PowerShell module, local or WinRM/CIM)

| Area | Cmdlets | Before | 0.1.14 | Verified by |
|---|---|---|---|---|
| Power / save / pause / resume / turn off / reset | `Start-VM`, `Stop-VM`, `Save-VM`, `Suspend-VM`, `Resume-VM`, `Restart-VM` | ✅ | ✅ | inventory re-read |
| Checkpoints | `Checkpoint-VM`, `Restore-VMSnapshot`, `Remove-VMSnapshot` | ✅ | ✅ | inventory |
| Rename checkpoint | `Rename-VMSnapshot` | — | 🆕 | inventory name |
| Export / import / new VM | `Export-VM`, `Import-VM -Copy -GenerateNewId`, `New-VM` | ✅ | ✅ | inventory |
| vCPU count | `Set-VMProcessor -Count` (VM off) | — | 🆕 | `ProcessorCount` |
| Startup memory | `Set-VMMemory -StartupBytes` (VM off) | — | 🆕 | `MemoryStartup` |
| Rename VM | `Rename-VM` | — | 🆕 | inventory name |
| Switch connection | `Connect-VMNetworkAdapter -SwitchName` (first adapter) | — | 🆕 | adapter's `SwitchName` |
| Delete VM | `Remove-VM -Force` (VM off; disks kept) | — | 🆕 typed | VM gone from inventory |
| Details | `NetworkAdapters` (IPs), `Get-VMHardDiskDrive`, `Get-VMIntegrationService`, automatic start/stop | — | 🆕 👁 | — |
| Dynamic memory, NUMA, secure boot / TPM, disks add/resize (`Resize-VHD`), adapters add, replication, live migration (`Move-VM`), integration services on/off | various | — | ⛔ | each has its own preconditions; not in this release (candidates, see §9) |

Values still reach PowerShell only as JSON on standard input; scripts are fixed text (the self-test sends a hostile VM name).

## 4. Docker (Engine API over the named pipe, or the docker CLI over SSH)

| Area | API | Before | 0.1.14 | Verified by |
|---|---|---|---|---|
| Start / stop / restart / remove container | `POST /containers/{id}/start·stop·restart`, `DELETE /containers/{id}` | ✅ | ✅ | `State.Status` |
| Logs | `GET /containers/{id}/logs` | ✅ 👁 | ✅ 👁 | — |
| Pause / unpause | `POST /containers/{id}/pause·unpause` | — | 🆕 | `State.Status` paused / running |
| Kill | `POST /containers/{id}/kill` | — | 🆕 typed | `State.Status` exited |
| Inspect / processes / resource usage | `GET /containers/{id}/json · top · stats?stream=false` | — | 🆕 👁 | — |
| Remove image / network / volume | `DELETE /images/{id}`, `/networks/{id}`, `/volumes/{name}` | — | 🆕 (volume & image typed; built-in networks never) | object answers 404 |
| Create / run containers, exec, pull, build, prune, compose | `POST /containers/create`, `/exec`, `/images/create`, `/build`, `…/prune` | — | ⛔ | container creation and exec are code execution on the host; prune deletes in bulk — not offered |

## 5. Unraid (GraphQL API)

| Area | Mutation | Before | 0.1.14 |
|---|---|---|---|
| Containers | `docker.start · stop` | ✅ | ✅ |
| Containers | `docker.restart · pause · unpause` | — | 🆕 (each only when this server's schema has it) |
| VMs | `vm.start · stop · reboot · forceStop` | ✅ | ✅ |
| VMs | `vm.pause · resume · reset` | — | 🆕 (reset typed) |
| Containers | `docker.removeContainer`, `updateContainer(s)`, `updateAutostartConfiguration` | — | ⛔ removal and image updates can't be verified with the fields the app reads; autostart is a list-wide setting |
| Array | `array.setState`, `addDiskToArray`, `mountArrayDisk`, `parityCheck.*` | — | ⛔ array operations stay deliberately out (ADR-007: disruptive, and a wrong click stops every share) |
| Notifications, API keys, rclone, plugins, identity | various | — | ⛔ administration of the server itself, not of workloads |

Discovery is unchanged: with introspection the schema says which of these exist; without it each mutation is probed with
its `$id` missing (so nothing can run).

## 6. Hetzner Cloud (`api.hetzner.cloud/v1`)

| Area | Operations | Before | 0.1.14 |
|---|---|---|---|
| Everything listed in ADR-028 (servers, images, volumes, networks, firewalls, floating / primary IPs, load balancers, certificates, SSH keys, DNS zones & records, placement groups, reference lists) | 112 of the app's operations | ✅ | ✅ |
| Server metrics | `GET /servers/{id}/metrics` (cpu, disk, network; last hour, 60 s steps) | — | 🆕 👁 summary (avg / peak / latest) |
| Load balancer metrics | `GET /load_balancers/{id}/metrics` | — | 🆕 👁 |
| Alias IPs | `POST /servers/{id}/actions/change_alias_ips` | — | 🆕 |
| Network members | `GET /networks/{id}/members` | — | 🆕 👁 |
| LB service update, LB reverse DNS | `…/update_service`, `…/change_dns_ptr` | — | 🆕 |
| Records: add / remove records, protection | `rrsets/…/add_records · remove_records · change_protection` | — | 🆕 |
| Secondary zone primaries | `zones/{id}/actions/change_primary_nameservers` | — | 🆕 (secondary zones only) |
| Console | `POST /servers/{id}/actions/request_console` | — | ⛔ VNC over WebSocket; no client in the app |
| Action history polling | `GET /…/actions/{id}` | used internally | used internally (every change is followed to the end) |
| Robot (dedicated servers), Storage Box API | separate APIs and credentials | — | ⛔ |

After 0.1.14 the app reaches every non-deprecated Cloud API operation except `request_console`. The self-test checks every
catalog path against the spec list (`SelfTestRelease.cs`).

## 7. Contabo (`api.contabo.com/v1`)

| Area | Operations | Before | 0.1.14 |
|---|---|---|---|
| Instances, snapshots, images, secrets, private networks, firewalls, DNS zones & records, data centers, action history | 48 | ✅ | ✅ |
| Object storage | list, order (billed), rename, usage stats, resize (grow only), cancel (typed) | — | 🆕 |
| VIPs (additional / floating IPs) | list, assign to instance, unassign | — | 🆕 |
| Reverse DNS (PTR) | list, create, change, delete | — | 🆕 |
| Tags | list, create, change, delete, tag an instance, assignments (list, remove) | — | 🆕 |
| Instance add-ons | `POST /compute/instances/{id}/upgrade` (Private Networking, Automated Backup; billed) | — | 🆕 |
| Region change | `GET …/regionChange/check`, `POST …/regionChange` | — | ⛔ moves a server between data centres (new IP, downtime, billed); left to the customer panel |
| DNS records bulk delete | `DELETE …/records/bulk` | — | ⛔ single deletes are offered; a bulk JSON delete adds risk for little gain |
| Users, roles, SSO, domains, troubleshooting, per-resource audits | `/users`, `/roles`, `/domains`, `/troubleshooting`, `…/audits` | — | ⛔ account administration and domain registration are out of scope; instance action history is shown |

The self-test checks every Contabo catalog path against the spec list (`SelfTestInfraCoverage.cs`): 70 operations on 13 screens.

## 8. Vultr (`api.vultr.com/v2`)

| Area | Operations | Before | 0.1.14 |
|---|---|---|---|
| Instances, bare metal, Kubernetes, block / object storage, snapshots, backups, ISOs, VPCs, reserved IPs, firewalls, DNS, load balancers, CDN, databases, registry, SSH keys, startup scripts, users, account, billing, inference, reference lists | ~175 | ✅ | ✅ |
| Instance IPv6 reverse DNS | set, list, delete | — | 🆕 |
| Instance IPv4 | reset reverse DNS to default, remove an extra IPv4, ISO status | — | 🆕 |
| Bare metal | IPv6 list, upgrades, VPCs, attach / detach VPC | — | 🆕 |
| Block storage snapshots | list, create, rename, delete | — | 🆕 |
| Vultr File System (VFS) | list, create, resize / rename, delete, attach to an instance, attachments, detach | — | 🆕 |
| Sub-accounts | list, create | — | 🆕 |
| Logs | `GET /logs` (start/end time required) | — | ⛔ needs a time-range picker the cloud engine doesn't have yet |
| Marketplace app variables, OIDC, organisations | `/marketplace`, `/oidc`, `/organizations` | — | ⛔ niche / enterprise SSO administration |

## 10. Microsoft Azure (Azure Resource Manager, `management.azure.com`) — 0.2.0, extended in 0.4.0

Checked against the Azure REST API reference (Microsoft Learn, 2026-10-21): Compute 2024-07-01, Network 2024-05-01, Private DNS
2024-06-01, Container Instances 2023-05-01, DBforPostgreSQL 2024-08-01, DBforMySQL 2023-12-30, Advisor 2023-01-01,
ResourceHealth 2022-10-01. Legend as above; 🆕 = added in 0.4.0 (DarkCipher0x: "double check the Azure and Google APIs … include
all features"). Every path is subscription-scoped and carries its `api-version`; the self-test refuses any that isn't.

| Area | Resource Manager | 0.2.0 | 0.4.0 |
|---|---|---|---|
| Virtual machines | list (statusOnly), start / deallocate / powerOff / restart / redeploy, resize (vmSizes), instanceView, delete | ✅ | ✅ |
| VM extensions | `…/virtualMachines/{vm}/extensions` | — | 🆕 👁 (publisher, type, version, state — settings never read: they may hold secrets) |
| VM scale sets | list, start / deallocate / restart (all instances), **scale** (`PATCH sku.capacity`), delete | — | 🆕 |
| Availability sets | list | — | 🆕 👁 |
| AKS | clusters start / stop, node pools | ✅ | ✅ |
| App Service | sites start / stop / restart | ✅ | ✅ |
| Container instances | container groups start / stop / restart, delete | — | 🆕 |
| Disks, snapshots | list, snapshot (PUT), delete unattached | ✅ | ✅ |
| Storage accounts | settings 👁 (keys never listed) | ✅ | ✅ |
| Virtual networks, NSGs + rules, public IPs, load balancers | list, rule create (PUT), delete | ✅ | ✅ |
| Network interfaces | list | — | 🆕 👁 |
| DNS zones + records | list, A record PUT, delete | ✅ | ✅ |
| Private DNS zones | zones, `ALL` record sets, A record PUT, virtual network links 👁, delete | — | 🆕 |
| Azure SQL | servers + databases | ✅ | ✅ |
| PostgreSQL / MySQL flexible servers | list, start / stop / restart, delete | — | 🆕 |
| Key vaults | settings 👁 (never secrets / keys / certificates) | ✅ | ✅ |
| Resource groups, all resources, locations, API explorer | list / create / delete; GET any path | ✅ | ✅ |
| Resource Health | `Microsoft.ResourceHealth/availabilityStatuses` | — | 🆕 👁 |
| Advisor | `Microsoft.Advisor/recommendations` | — | 🆕 👁 |
| Activity log | `Microsoft.Insights/eventtypes/management/values` | — | ⛔ needs a `$filter` with a time range the cloud engine has no picker for yet |
| Role assignments, Key Vault secrets, storage keys, serial console, Bastion, Monitor alerts | — | — | ⛔ identity and secrets stay in the portal; the console is a WebSocket protocol |

## 11. Google Cloud (`*.googleapis.com`) — 0.2.0, extended in 0.4.0

Checked against the Google Cloud REST references (2026-10-21): Compute Engine v1, Cloud Functions v2, Artifact Registry
v1, Pub/Sub v1, Cloud SQL Admin v1. Hosts are allow-listed in `GcpClient.Hosts`; the self-test refuses a catalog path on
any other host. IAM (`iam.googleapis.com`) stays out on purpose.

| Area | API | 0.2.0 | 0.4.0 |
|---|---|---|---|
| VM instances | aggregated list, start / stop / reset / suspend / resume, machine type, serial output, delete | ✅ | ✅ |
| Managed instance groups | `aggregated/instanceGroupManagers`, **resize** (`POST …/resize?size=`), delete | — | 🆕 |
| Instance templates, machine images | `global/instanceTemplates`, `global/machineImages`, delete | — | 🆕 |
| Custom images, disks (snapshot), snapshots | list, createSnapshot, delete | ✅ | ✅ |
| GKE clusters + node pools (setSize) | Container v1 | ✅ | ✅ |
| Cloud Run services + revisions | Run v2 | ✅ | ✅ |
| Cloud Functions (2nd gen) | `cloudfunctions.googleapis.com/v2 …/locations/-/functions`, delete | — | 🆕 |
| Artifact Registry | `artifactregistry.googleapis.com/v1 …/repositories` | — | 🆕 👁 (contents never listed) |
| Cloud SQL | instances start / stop / restart, databases, backups 👁 | ✅ | ✅ |
| Cloud SQL on-demand backup | `POST …/instances/{name}/backupRuns` | — | 🆕 |
| Cloud Storage buckets | settings 👁, delete empty | ✅ | ✅ |
| VPC networks, subnets, firewall rules (create), addresses, forwarding rules | Compute v1 | ✅ | ✅ |
| Routes, Cloud Routers / NAT, backend services, health checks, SSL certificates, VPN tunnels | `global/routes`, `aggregated/routers`, `global/backendServices`, `global/healthChecks`, `global/sslCertificates`, `aggregated/vpnTunnels` | — | 🆕 👁 (shared secrets and private keys never read) |
| Cloud DNS zones + records | DNS v1 | ✅ | ✅ |
| Pub/Sub | topics (create by PUT, delete), subscriptions (delete) | — | 🆕 (messages never read) |
| Regions, API explorer | Compute v1; GET any allowed path | ✅ | ✅ |
| IAM, service-account keys, Secret Manager, Cloud SQL users, object / message contents, serial console | — | — | ⛔ identity and secrets stay in the console; no WebSocket client |

## 9. Coverage summary

Counted as operations a user can trigger from the app (lists and read views included; internal re-reads and polling excluded).

| Provider | Before (0.1.13) | After (0.1.14) | Note |
|---|---|---|---|
| Proxmox VE (guest / node / storage scope) | 4 actions | 17 actions | +snapshots, pause, clone, migrate, vzdump, template, agent, tasks, contents |
| VMware vCenter | 7 | 13 | snapshots impossible over REST |
| Hyper-V | 13 | 20 | |
| Docker | 5 | 14 | |
| Unraid | 6 mutations | 12 mutations | when the server's schema has them |
| Hetzner Cloud | 112 | 122 | every Cloud API operation except the VNC console |
| Contabo | 48 | 70 | all in-scope operations except region change and bulk DNS delete |
| Vultr | ~175 | ~201 | |
| Microsoft Azure (0.2.0 → 0.4.0) | 16 screens | 25 screens | + scale sets, availability sets, extensions, NICs, private DNS (+ records, links), container instances, PostgreSQL / MySQL flexible servers, Resource Health, Advisor |
| Google Cloud (0.2.0 → 0.4.0) | 15 screens | 27 screens | + MIGs (resize), templates, machine images, Cloud Functions, Artifact Registry, routes, routers / NAT, backend services, health checks, SSL certificates, VPN tunnels, Pub/Sub, Cloud SQL backup now |

**Candidates for a later release** (each needs its own verification design): Proxmox disk resize and guest hardware
editing; vCenter disks / NICs / CD-ROM and relocate; Hyper-V dynamic memory, disk resize, live migration; Vultr logs (time
range picker); Unraid parity check (pause / resume / cancel).
