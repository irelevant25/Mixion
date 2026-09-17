using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Mixion.Host.Web;

/// <summary>
/// In-memory store of /ws auth tokens: 32 random bytes each, not derived from
/// any key, so there is nothing to leak and a host restart revokes them all.
/// A token is issued on every GET /api/session and redeemed by /ws.
///
/// <para>
/// Tokens are single-use and expire <see cref="Lifetime"/> after issue. The
/// SPA fetches a fresh one before every connect (first load and each
/// reconnect), so neither limit costs it anything — but a token that ends up
/// somewhere it shouldn't (the request log records the /ws URL) is useless,
/// and the store doesn't grow with every page load for the life of the process.
/// </para>
///
/// <para>
/// The token is not what keeps other local processes out: /api/session hands
/// one to anything that can reach 127.0.0.1. It keeps web pages out, together
/// with <see cref="LoopbackRequestGuard"/> — a page can't read /api/session
/// (no CORS headers, and the guard refuses rebound host names), and the /ws
/// upgrade is refused for foreign origins.
/// </para>
/// </summary>
public sealed class SessionStore
{
    /// <summary>How long an issued token can be redeemed. Covers the host's startup wait on the page's side.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiries = new();
    private readonly TimeProvider _time;

    public SessionStore() : this(TimeProvider.System) { }

    public SessionStore(TimeProvider time) => _time = time;

    /// <summary>Number of tokens issued and neither redeemed nor pruned yet.</summary>
    public int Count => _expiries.Count;

    public string Issue()
    {
        var now = _time.GetUtcNow();
        PruneExpired(now);

        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToHexString(bytes).ToLowerInvariant();
        _expiries[token] = now + Lifetime;
        return token;
    }

    /// <summary>
    /// Consumes <paramref name="token"/>: true when it was issued, unexpired and
    /// not redeemed before. Either way it can't be redeemed again.
    /// </summary>
    public bool TryRedeem(string? token)
        => !string.IsNullOrEmpty(token)
        && _expiries.TryRemove(token, out var expiry)
        && _time.GetUtcNow() < expiry;

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var (token, expiry) in _expiries)
        {
            if (now >= expiry) _expiries.TryRemove(token, out _);
        }
    }
}
