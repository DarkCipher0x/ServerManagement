namespace DcxManager.Cloud;

// Stored records (0.2.0 phase 6): kept in the encrypted store, so they live in Dcx.Core with the store; the code that uses them stays here.

/// <summary>An account of any provider (the secrets live in the provider's own account type, in the encrypted settings).</summary>
public interface ICloudAccount
{
    string Id { get; }
    string Name { get; }
    bool Disabled { get; }
}
