using System.Net;
using Microsoft.AspNetCore.Builder;
using IPNetwork = System.Net.IPNetwork;
using FwdHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders;

namespace Encore.Requests.Api;

/// <summary>
/// ENCORE_TRUSTED_PROXIES as the forwarded-headers middleware needs it (design 3.6). An IPv4 entry is also kept in its
/// <c>::ffff:</c> mapped form, because a dual-mode socket reports an IPv4 peer that way.
/// </summary>
public sealed class ProxyTrust
{
    private readonly IReadOnlyList<IPNetwork> _networks;

    public ProxyTrust(IReadOnlyList<IPNetwork> configured)
    {
        var all = new List<IPNetwork>(configured);
        foreach (var network in configured)
        {
            if (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                all.Add(new IPNetwork(network.BaseAddress.MapToIPv6(), network.PrefixLength + 96));
            }
        }

        _networks = all;
    }

    public bool Any => _networks.Count > 0;

    public IReadOnlyList<IPNetwork> Networks => _networks;

    /// <summary>Is this connection peer one of the configured proxies?</summary>
    public bool IsTrusted(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        foreach (var network in _networks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return address.IsIPv4MappedToIPv6 && IsTrusted(address.MapToIPv4());
    }

    /// <summary>
    /// X-Forwarded-For, -Proto and -Host only; never -Prefix (the base is the fixed ENCORE_PATH_BASE). KnownProxies and
    /// KnownIPNetworks are cleared first (the defaults trust loopback) and filled only from the configuration.
    /// ForwardLimit 2: Cloudflare and NPM are the two hops that may speak.
    /// </summary>
    public ForwardedHeadersOptions ForwardedOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = FwdHeaders.XForwardedFor | FwdHeaders.XForwardedProto | FwdHeaders.XForwardedHost,
            ForwardLimit = 2,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var network in _networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        return options;
    }
}
