namespace DcxManager.Services;

/// <summary>The last LAN discovery result (0.3.0 §7, "saved encrypted"): what answered on the SSH port, kept in the store so the
/// list is back when the screen reopens. Working data, never exported.</summary>
public sealed class LanLastScan
{
    public string Subnet { get; set; } = "";
    public int Port { get; set; } = 22;
    public DateTime When { get; set; }
    public List<LanFound> Found { get; set; } = new();
}

public sealed class LanFound
{
    public string Address { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Name { get; set; } = "";
    public string Banner { get; set; } = "";
}
