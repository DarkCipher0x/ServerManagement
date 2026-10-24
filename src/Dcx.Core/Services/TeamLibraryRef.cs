namespace DcxManager.Services;

/// <summary>
/// A team library this profile knows without an add-on (0.3.0 §22: team features built in): which library, where its
/// share is and whose owner key to trust — the same four facts a team add-on carried. Holds no secret; the member keys
/// live in the library's own state file. Travels with the app settings in a backup.
/// </summary>
public sealed class TeamLibraryRef
{
    public string LibraryId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string OwnerFingerprint { get; set; } = "";
}
