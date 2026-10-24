namespace DcxManager.Bitwarden;

// What the app keeps of Bitwarden's answers (0.6.14, ADR-093). Lists carry names and ids only, never a password, key or
// secret value; a value is read one item at a time, for the session or the copy that asked for it.

/// <summary>A Password Manager vault item that can sign in: a login (type 1) or an SSH key (type 5).</summary>
public sealed record VaultItem(string Id, string Name, int Type, string Username, string Uri, string OrganizationId, bool HasPassword, string Fingerprint)
{
    public string TypeText => Type == 5 ? "SSH key" : "login";
    public string Owner => OrganizationId.Length > 0 ? "organisation" : "personal";
}

/// <summary>A vault item's sign-in, read for one use: a password, or an SSH private key.</summary>
public sealed record VaultSecret(string Label, string Username, string Secret, bool IsKey, string Totp = "")
{
    /// <summary>Never the secret.</summary>
    public override string ToString() => $"{Label} ({(IsKey ? "SSH key" : "password")}{(Username.Length > 0 ? ", " + Username : "")})";
}

/// <summary>A Secrets Manager project.</summary>
public sealed record SmProject(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A Secrets Manager secret, without its value.</summary>
public sealed record SmSecret(string Id, string Key, string ProjectId, string Note);
