# Shared library add-on: design proposal (2026-10-19, approved by DarkCipher0x and built in 0.1.15)

> Built as ADR-033 (`docs/ARCHITECTURE.md`). The build differs from this proposal in four ways:
> - **Roll-back detection:** per-item revisions plus the roster and metadata sequence numbers, instead of one signed manifest per collection. A manifest would be a file every member writes to.
> - **Grants:** every roster entry carries its own grant signature, chained back to the owner.
> - **Admins:** only the owner can appoint them.
> - **Member names:** sealed in the metadata, not in the roster.

DarkCipher0x's answers: the library lives in a **file share or folder** (SMB/NAS/synced folder), the team has **10–50 people**, and it **must work offline** from an encrypted cached copy.

## 1. Shape: a data-only add-on with the engine in the app
- **The add-on package** is data-only, like a theme (ADR-030). `addon.json` gains a `library` field that points at `library.json` in the package. That file holds the library id, its name, a default path hint (`\\nas\team\dcx-library`), and the **owner's signing-key fingerprint**. The fingerprint is the trust anchor.
  - The package contains no secret, so it can be handed to the whole team. Installing and approving it is how someone joins a library.
  - Installation reuses everything that exists: approval screen, hash verification, enable / disable / remove, audit.
- **The engine runs in the app** (`Library/`), not in an add-on process. This is deliberate. Program add-ons run at Low IL, which can still read files and reach the network (ADR-012). Decrypted team secrets must never cross the bridge into such a process.
- **The share is untrusted.** It only ever holds ciphertext, signatures and public keys. Anyone with write access to the share can delete or roll back files. They cannot read anything, and they cannot forge anything without the change being detected and flagged.

## 2. Crypto (built into .NET 8, no new dependencies)
- **Member identity:** each member has a P-256 ECDH key pair (for encryption) and a P-256 ECDSA key pair (for signing).
  - The keys are generated on the member's PC, and the private keys stay in that member's own encrypted store (`library-keys.dcx`, sealed like every other store file).
  - Fingerprint = SHA-256 of both public keys, shown as 8 groups of 4 hex characters.
- **Collections** keep a 50-person team manageable. Examples: "Core infra", "Customer X", "Network". Each collection has a random 256-bit collection key (CK) with an **epoch** number.
  - **Items:** each host or credential is one file, sealed with AES-256-GCM under the CK. The AAD binds `libraryId|collectionId|itemId|epoch|kind`, so a blob can't be moved to another item or collection.
  - **Key wraps:** for each member with access, the CK is wrapped with ECDH-ES. That means an ephemeral P-256 key, HKDF-SHA256 with the context bound in, then AES-256-GCM. It is the same construction as HPKE base mode.
- **Signatures:** every file written to the share carries the author's ECDSA signature.
  - The **roster** (members, roles, public keys) is signed by an admin, and the admin chain leads back to the owner fingerprint pinned in the add-on.
  - A client ignores and flags anything that is unsigned, signed by someone unknown, or signed by someone whose role doesn't allow that write.
- **What the share reveals:** file names are random ids, so the share shows item counts, sizes, times and pseudonymous member ids. Names, addresses, usernames, notes and secrets are all inside ciphertext. Member display names are sealed too, under a library-wide metadata key that every member holds.
- **Rollback detection:** each collection has a signed manifest with a monotonic sequence number. Each client remembers the highest sequence it has seen (in its encrypted cache) and warns if the share goes backwards.

## 3. Members: join, approve, remove
- **Join:**
  1. The new member installs the add-on.
  2. The app generates their keys and writes a signed join request to `requests/` on the share. It can also export the request to a file if the member can't write to the share yet.
  3. The member's fingerprint is shown on their screen.
- **Approve:**
  1. An admin opens the request and checks the fingerprint out of band, for example by reading it out on a call.
  2. The admin picks a role and collections.
  3. The app signs the roster entry and wraps each chosen collection's current CK for the new member.
- **Remove**, which is also how a lost device is handled:
  1. The admin removes the member from the roster.
  2. Every collection that member could read moves to a new epoch with a new CK. Every item is re-encrypted, keys are re-wrapped for the remaining members, and the old epoch's wraps are deleted.
  3. **That protects only future changes.** The removed person has already seen the passwords. So the app gives the admin a **rotation checklist**: every credential that member could read, ticked off as each one is rotated (the actual password or key changed on the target). The checklist stays open until it is done.
- **Owner:** there is one owner, who can appoint admins. Losing the owner key means the roster can no longer be re-signed. Mitigation: a sealed owner-key backup in the owner's own store, plus an option to hand ownership to another admin while the owner key is still available.

## 4. Roles
- **Owner > admin > editor > reader.** Admins manage members and collections. Access to a collection is granted as **editor** or **reader**.
- **How it is enforced:** a reader holds the CK, so cryptography alone can't stop them writing a validly encrypted blob. Enforcement comes from the signed roster. Every client rejects and flags an item whose signer is not an editor of that collection. This is stated honestly in the UI.

## 5. Conflicts and merging
- **One file per item**, so concurrent edits to different items never conflict. Writes go to a temporary file and are then renamed.
- **Optimistic concurrency:** each item carries `rev` and `baseRev`.
  - Saving checks the current revision first.
  - If two revisions share a base (two people saved at once, or a sync tool such as OneDrive or Syncthing left a "conflict copy"), the item is marked **CONFLICT**. Both versions are kept and shown side by side, and an editor picks one or merges them field by field.
  - There is no silent last-writer-wins.
- **Deletes** are signed tombstones, kept for 90 days so an offline member who comes back doesn't bring the item back.

## 6. Audit
- **Shared audit:** one append-only file per member per month (`audit/<memberId>-YYYY-MM.log`).
  - Each line is encrypted under an audit key that only admins hold, signed, and hash-chained to the line before it.
  - Logged: join, approve, remove, rotate, item create / edit / delete, and conflict resolutions.
- **Local audit:** the existing encrypted `AuditLog` also records every use of a shared item on this PC: connect with a shared credential, reveal, copy, and copy to my hosts.
- **What audit can't do:** reads on another member's PC are self-reported by that member's client. A modified client could skip them, and the hash chain only shows truncation of lines that other clients had already seen. The UI says this plainly.

## 7. How shared items appear
- Shared hosts and credentials live in their own in-memory model (`SharedLibrary`). They are **never merged into `AppData.Hosts` / `Credentials`**.
- **Hosts tree:** a top-level "⇄ Shared · *library name*" node, with collections as folders. Every shared row carries a SHARED badge and the collection name, and its tooltip shows the author and last change.
- **Vault:** a separate "Shared" section with the same badge. Revealing or copying a secret is audited.
- **Connecting** a shared host works like connecting a local one: terminal, files, RDP and manage. The host's credential is resolved from the library at the moment of connecting.
- **Local hosts can reference a shared credential** (`CredentialId = "lib:<libraryId>/<itemId>"`), so a rotated team password reaches every member's own hosts.
- **Nothing is overwritten:**
  - **Copy to my hosts** creates a new local item with a new id.
  - **Publish to library** copies a local item into a collection. It is an explicit action, open to editors only, and shows a preview of exactly which fields go.
  - A local item and a shared item with the same name or address are both shown, and the shared one is labelled.
  - Sync never writes to `data.dcx`.

## 8. Offline
- **The cache:** the last good decrypted state is kept as `library-<id>.dcx` in the member's own store, sealed with the store key like every other file. The cache also records the highest manifest sequence seen.
- **When the share is unreachable**, or Offline mode is on and the share is a remote UNC path:
  - shared items stay usable from the cache, labelled "cached · as of 14:02";
  - edits wait in an encrypted outbox and are pushed with the normal conflict check once the share is back.
- **Network policy:** a UNC path counts as a network destination in `NetworkPolicy` and goes through the `Offline` gate. A local folder, such as a synced OneDrive or Syncthing folder, does not.
- **Sync** runs when the app starts, when the user presses **Sync**, and on a `FileSystemWatcher` for local folders. There is an optional interval (default off, per "no silent polling"). A share on an internal LAN is not an external destination, so it raises no ⚠.

## 9. What the add-on model can and cannot do today, and what this adds
- **Can do today:**
  - install, approve, hash-verify, enable, disable, remove and audit a data-only package (themes);
  - give program add-ons hosts without credentials (`hosts.read`);
  - give program add-ons one credential per dialog (`credentials.request`).
- **Cannot do today:**
  - an add-on cannot add hosts or credentials;
  - it cannot add a section to the hosts tree or the vault;
  - it cannot hold keys.
  - It **should not** be able to do these from a Low-IL process holding team secrets.
- **Additions:**
  1. Manifest field `library` (a third kind of add-on, next to program and theme). A library-only add-on is refused if it also has `entry`, `commands` or `permissions`. `LibraryProfile.Parse` is strict, like `ThemePack.Parse`.
  2. App engine `Library/`: `LibraryCrypto`, `LibraryStore` (share I/O, atomic writes), `LibrarySync` (verify, merge, cache, outbox), `SharedLibrary` (the model the UI reads).
  3. A new permission for program add-ons, **`library.read`**: shared hosts without secrets, flagged `shared: true`, in `hosts.list`. `credentials.request` may then name a shared credential, and its dialog shows the library and collection. No write permission is added.
  4. UI:
     - a **Shared library** screen with members, requests, collections, conflicts, the rotation checklist and audit;
     - the shared node in the hosts tree;
     - the Shared section in the vault;
     - Copy to my hosts and Publish to library on the context menus.
- **Self-test** (`SelfTestLibrary.cs`): a scratch folder plays the share, and three simulated members run in one process (owner, editor, reader). Checks:
  - no demo hostname, address, username or secret appears in any byte on the share;
  - a non-member can't decrypt anything;
  - removing a member moves to a new epoch, and the removed member's keys can't open new items;
  - a reader's write is rejected;
  - tamper and rollback are detected;
  - a conflict keeps both versions;
  - the offline cache works and the outbox replays;
  - shared rows are labelled;
  - local data is never changed by sync;
  - the screens render with no window and no dialog.

## 10. Not in v1
A sync server, SSO / LDAP groups, hardware keys (YubiKey PIV), per-item ACLs inside a collection, field-level history beyond the conflict pair, and sharing snippets / tunnels / infra providers. The model leaves room for all of them.
