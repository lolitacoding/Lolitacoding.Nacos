using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Lolitacoding.Nacos.AspNetCore;

/// <summary>
/// Works out the address an instance should advertise.
/// </summary>
/// <remarks>
/// This is separated from the hosted service so the inference rules — which are the part most
/// likely to be wrong in an unusual deployment — can be exercised without starting a host.
/// </remarks>
internal static class NacosEndpointResolver
{
    /// <summary>
    /// Hosts that mean "every interface" rather than a routable address.
    /// </summary>
    private static readonly string[] WildcardHosts = ["0.0.0.0", "::", "[::]", "*", "+", "localhost"];

    /// <summary>
    /// Resolves the IP address and port to register.
    /// </summary>
    /// <param name="options">The registration options, whose explicit values win.</param>
    /// <param name="addresses">
    /// The addresses the server is bound to, as reported by the hosting layer.
    /// </param>
    /// <returns>The address and port to advertise.</returns>
    /// <exception cref="NacosConfigurationException">
    /// No usable address or port could be determined.
    /// </exception>
    /// <remarks>
    /// An explicit value always wins over an inferred one, so an operator can correct a wrong
    /// inference without disabling the feature.
    /// </remarks>
    public static (string Ip, int Port) Resolve(
        NacosServiceRegistrationOptions options,
        IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(addresses);

        string? ip = Normalize(options.Ip);
        int? port = options.Port;

        if (ip is null || port is null)
        {
            foreach (string address in addresses)
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
                {
                    continue;
                }

                ip ??= Normalize(uri.Host);

                if (port is null && uri.Port is > 0 and <= 65535)
                {
                    port = uri.Port;
                }

                if (ip is not null && port is not null)
                {
                    break;
                }
            }
        }

        // A wildcard bind advertises nothing routable, so a concrete interface address is chosen
        // instead; without it, the registered address would be unusable to every caller.
        ip ??= TryFindLocalAddress();

        if (ip is null)
        {
            throw new NacosConfigurationException(
                "Could not determine the IP address to register with Nacos; set NacosServiceRegistrationOptions.Ip explicitly.");
        }

        if (port is null)
        {
            throw new NacosConfigurationException(
                "Could not determine the port to register with Nacos; set NacosServiceRegistrationOptions.Port explicitly.");
        }

        return (ip, port.Value);
    }

    /// <summary>
    /// Normalizes a candidate address, discarding wildcard and loopback-only values.
    /// </summary>
    /// <param name="value">The candidate.</param>
    /// <returns>The usable address, or <see langword="null"/>.</returns>
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();

        // Build an IPv6 host in its bracketed form, which a bound address uses but a Nacos
        // instance address should not.
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1];
        }

        foreach (string wildcard in WildcardHosts)
        {
            if (string.Equals(trimmed, wildcard, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Finds the first usable non-loopback IPv4 address on the machine.
    /// </summary>
    /// <returns>The address, or <see langword="null"/> when none could be found.</returns>
    internal static string? TryFindLocalAddress()
    {
        try
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up
                    || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(address.Address))
                    {
                        return address.Address.ToString();
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // A restricted sandbox may deny interface enumeration; the caller falls back to an
            // explicit address or reports a clear configuration error.
        }

        return null;
    }
}
