using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace VoicemeterAlt.Host.Web;

/// <summary>
/// In-memory store of valid /ws auth tokens. A token is issued on every
/// GET /api/session and accepted by /ws as a query-string parameter. The
/// store is process-lifetime; restarting the host invalidates every token.
/// </summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new();

    public string Issue()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToHexString(bytes).ToLowerInvariant();
        _tokens[token] = DateTimeOffset.UtcNow;
        return token;
    }

    public bool IsValid(string? token)
        => !string.IsNullOrEmpty(token) && _tokens.ContainsKey(token);
}
