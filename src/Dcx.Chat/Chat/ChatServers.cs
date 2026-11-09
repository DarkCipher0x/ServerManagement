using CipherChat.Transport;
using DcxManager.Services;

namespace DcxManager.Chat;

/// <summary>
/// Which CipherChat servers this PC may talk to, and over what (ADR-117). Real servers are https:// only, over
/// CipherChat's post-quantum-only TLS 1.3 transport. Group Policy can limit the servers to a list
/// (<see cref="AppSettings.ChatAllowedServers"/>). A plain-http loopback server (CipherChat's development mode) is
/// accepted only by the self-test or with <c>DCX_CHAT_DEV=1</c>, and CipherChat's own dev transport refuses any host
/// but 127.0.0.1 / localhost, so it can never carry real traffic.
/// </summary>
public static class ChatServers
{
    public const string DevEnvVar = "DCX_CHAT_DEV";

    private static bool DevAllowed => TestMode.Running || Environment.GetEnvironmentVariable(DevEnvVar) == "1";

    /// <summary>The origin of an address ("https://chat.example.com"), or null when it isn't a server address.</summary>
    public static string? Origin(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var u) || u.UserInfo.Length > 0 || u.Host.Length == 0) return null;
        return u.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    /// <summary>Why this server can't be used (null = it can).</summary>
    public static string? Check(string address)
    {
        if (Origin(address) is not { } origin) return "That isn't a server address (https://…).";
        var u = new Uri(origin);
        if (u.Scheme == Uri.UriSchemeHttp)
        {
            if (!(u.IsLoopback || u.Host == "localhost")) return "CipherChat servers are https:// only.";
            if (!DevAllowed) return "A development server (http://127.0.0.1) can only be used for testing.";
        }
        else if (u.Scheme != Uri.UriSchemeHttps) return "CipherChat servers are https:// only.";
        var allowed = AppSettings.Current.ChatAllowedServers;
        if (allowed.Count > 0 && !allowed.Any(a => Origin(a) == origin) && u.Scheme == Uri.UriSchemeHttps)
            return "Your organisation allows only these CipherChat servers: " + string.Join(", ", allowed);
        return null;
    }

    /// <summary>The transport for an allowed server: post-quantum TLS, or the loopback dev transport for a test server.</summary>
    public static ITransport TransportFor(Uri origin)
    {
        if (Check(origin.ToString()) is { } why) throw new InvalidOperationException(why);
        return origin.Scheme == Uri.UriSchemeHttp ? new LoopbackDevTransport(origin) : new PqTlsTransport(origin);
    }
}
