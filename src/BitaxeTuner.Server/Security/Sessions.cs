using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace BitaxeTuner.Server.Security;

public sealed record Session(string Id, Role Role, string Csrf, DateTime ExpiresUtc);

/// <summary>Browser-Sitzungen im Speicher (nach einem Neustart des Dienstes neu anmelden).</summary>
public sealed class SessionStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public Session Create(Role role, DateTime nowUtc)
    {
        foreach (var old in _sessions.Values.Where(s => s.ExpiresUtc < nowUtc)) _sessions.TryRemove(old.Id, out _);
        var session = new Session(AuthStore.Base64Url(RandomNumberGenerator.GetBytes(32)), role,
            AuthStore.Base64Url(RandomNumberGenerator.GetBytes(24)), nowUtc + Lifetime);
        _sessions[session.Id] = session;
        return session;
    }

    public Session? Get(string? id, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryGetValue(id, out var s)) return null;
        if (s.ExpiresUtc >= nowUtc) return s;
        _sessions.TryRemove(id, out _);
        return null;
    }

    public void Remove(string? id)
    {
        if (id is not null) _sessions.TryRemove(id, out _);
    }

    /// <summary>Nach Passwortänderung: alle Browser-Sitzungen beenden.</summary>
    public void Clear() => _sessions.Clear();
}

/// <summary>Sperre nach Fehlversuchen je Adresse (5 Fehlversuche → 5 Minuten).</summary>
public sealed class Lockout
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (int Count, DateTime Until)> _state = new();

    public bool IsLocked(string client, DateTime nowUtc) =>
        _state.TryGetValue(client, out var s) && s.Until > nowUtc;

    public void Fail(string client, DateTime nowUtc) =>
        _state.AddOrUpdate(client, _ => (1, DateTime.MinValue), (_, s) =>
        {
            var count = s.Until > DateTime.MinValue && s.Until <= nowUtc ? 1 : s.Count + 1; // abgelaufene Sperre: neu zählen
            return count >= MaxFailures ? (count, nowUtc + LockTime) : (count, DateTime.MinValue);
        });

    public void Success(string client) => _state.TryRemove(client, out _);
}

public static class NetworkRules
{
    /// <summary>
    /// Heimnetz, Loopback und VPN: 10/8, 172.16/12, 192.168/16, 100.64/10 (Tailscale/CGNAT), 169.254/16,
    /// IPv6 ULA fc00::/7 und Link-Local. Alles andere gilt als Internet.
    /// </summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }
}
