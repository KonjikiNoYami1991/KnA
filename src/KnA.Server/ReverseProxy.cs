using System.Collections.Concurrent;
using System.Net;
using KnA.Core.Settings;
using Microsoft.AspNetCore.HttpOverrides;

namespace KnA.Server;

/// <summary>
/// KnA behind a reverse proxy (nginx, Caddy, Traefik, Nginx Proxy Manager…):
///   KNA_TRUSTED_PROXIES  addresses / networks whose X-Forwarded-For, -Proto, -Host and -Prefix are believed
///                        (comma separated: 10.0.0.5, 172.18.0.0/16, "private" = every private network);
///                        this machine is always trusted. Anybody else's X-Forwarded-* headers are ignored, so a client
///                        can't fake its address (sign-in throttling) or HTTPS (the cookie's Secure flag).
///   KNA_BASE_PATH        the path KnA is published under (e.g. /kna), whether the proxy strips it or not; a proxy may
///                        also send it as X-Forwarded-Prefix.
/// The web UI uses relative URLs only, so it works under any path; progress (Server-Sent Events) carries
/// "X-Accel-Buffering: no" and a heartbeat every 15 s, uploads shrink their pieces by themselves when a proxy refuses
/// large request bodies.
/// </summary>
public static class ReverseProxy
{
    private static readonly ConcurrentDictionary<string, byte> Warned = new();

    /// <summary>"/kna" from "kna", "/kna/", " /kna " …; null for none or "/".</summary>
    public static string? NormalizeBasePath(string? value)
    {
        var v = value?.Trim().Trim('/');
        return string.IsNullOrEmpty(v) ? null : "/" + v;
    }

    /// <summary>The trusted proxies of a KNA_TRUSTED_PROXIES value (loopback always included); unreadable entries are returned apart.</summary>
    public static (List<IPAddress> Proxies, List<System.Net.IPNetwork> Networks, List<string> Invalid) ParseTrusted(string? value)
    {
        var proxies = new List<IPAddress>();
        var networks = new List<System.Net.IPNetwork> { System.Net.IPNetwork.Parse("127.0.0.0/8"), System.Net.IPNetwork.Parse("::1/128") };
        var invalid = new List<string>();
        foreach (var raw in (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("private", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var n in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "fc00::/7", "fe80::/10" })
                    networks.Add(System.Net.IPNetwork.Parse(n));
            }
            else if (raw.Contains('/') && System.Net.IPNetwork.TryParse(raw, out var net)) networks.Add(net);
            else if (IPAddress.TryParse(raw, out var ip)) proxies.Add(ip);
            else invalid.Add(raw);
        }
        return (proxies, networks, invalid);
    }

    public static bool IsTrusted(IPAddress? address, IReadOnlyList<IPAddress> proxies, IReadOnlyList<System.Net.IPNetwork> networks)
    {
        if (address is null) return false;
        var candidates = address.IsIPv4MappedToIPv6 ? new[] { address, address.MapToIPv4() } : [address];
        return candidates.Any(a => proxies.Contains(a) || networks.Any(n => n.Contains(a)));
    }

    /// <summary>First in the pipeline: real client address and scheme from trusted proxies, then the base path.</summary>
    public static void UseReverseProxy(this WebApplication app)
    {
        var log = app.Logger;
        var (proxies, networks, invalid) = ParseTrusted(AppEnv.Get("TRUSTED_PROXIES"));
        foreach (var bad in invalid) log.LogWarning("KNA_TRUSTED_PROXIES: \"{Entry}\" is not an address or a network (e.g. 172.18.0.0/16): ignored.", bad);

        // Headers from an address that is not trusted are ignored: said once per address, with the fix.
        app.Use(async (ctx, next) =>
        {
            var remote = ctx.Connection.RemoteIpAddress;
            if (ctx.Request.Headers.ContainsKey("X-Forwarded-For") && !IsTrusted(remote, proxies, networks)
                && Warned.Count < 100 && Warned.TryAdd(remote?.ToString() ?? "?", 0))
                log.LogWarning("X-Forwarded-* headers from {Address} ignored: if it is your reverse proxy, set KNA_TRUSTED_PROXIES={Address} " +
                               "(otherwise every client looks like the proxy and HTTPS is not seen).", remote, remote);
            await next();
        });

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedPrefix,
            ForwardLimit = null,              // a chain of trusted proxies (e.g. Cloudflare → NPM → KnA) is walked to the first untrusted hop
            RequireHeaderSymmetry = false,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var p in proxies) options.KnownProxies.Add(p);
        foreach (var n in networks) options.KnownIPNetworks.Add(n);
        app.UseForwardedHeaders(options);

        if (NormalizeBasePath(AppEnv.Get("BASE_PATH")) is { } basePath)
        {
            app.UsePathBase(basePath);
            log.LogInformation("Base path: {BasePath}", basePath);
        }

        // "/kna" → "/kna/": the UI's relative URLs need the trailing slash.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.PathBase.HasValue && !ctx.Request.Path.HasValue && HttpMethods.IsGet(ctx.Request.Method))
            {
                ctx.Response.Redirect(ctx.Request.PathBase + "/" + ctx.Request.QueryString);
                return;
            }
            await next();
        });
    }

    /// <summary>The path cookies are limited to: the public base path (from KNA_BASE_PATH or the proxy), else "/".</summary>
    public static string CookiePath(HttpContext ctx) => ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value! : "/";
}
