using System.Net.Security;
using System.Security.Authentication;

namespace Lolitacoding.Nacos;

/// <summary>
/// Configures how the client negotiates TLS with a Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// TLS is enabled per server address: the client uses it whenever an entry in
/// <see cref="NacosOptions.ServerAddresses"/> uses the <c>https</c> scheme. Nacos negotiates TLS
/// and plaintext on the same SDK gRPC port, so an <c>https://host:8848</c> address talks HTTP over
/// TLS on <c>8848</c> and gRPC over TLS on <c>9848</c>.
/// </para>
/// <para>
/// Every setting here is optional. Left empty, the client validates the server certificate with
/// the operating system trust store, which is the correct default for a production deployment.
/// The remaining options exist for private certificate authorities, mutual TLS and development
/// servers that use self-signed certificates.
/// </para>
/// </remarks>
public sealed class NacosTlsOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the server certificate is accepted without
    /// validation.
    /// </summary>
    /// <remarks>
    /// This disables a security control and exists only for development servers with self-signed
    /// certificates. It matches <c>trustAll</c> in the official Java client.
    /// </remarks>
    public bool AllowUntrustedCertificate { get; set; }

    /// <summary>
    /// Gets or sets the PEM-encoded certificate authority (or authorities) the client trusts when
    /// validating the server certificate.
    /// </summary>
    /// <remarks>
    /// Use this for a private CA. The value is the PEM text itself; use
    /// <see cref="ServerCertificateAuthorityFile"/> to point at a file instead.
    /// </remarks>
    public string? ServerCertificateAuthority { get; set; }

    /// <summary>
    /// Gets or sets the path to a PEM file holding the certificate authority (or authorities) the
    /// client trusts when validating the server certificate.
    /// </summary>
    public string? ServerCertificateAuthorityFile { get; set; }

    /// <summary>
    /// Gets or sets the PEM-encoded client certificate chain presented for mutual TLS.
    /// </summary>
    /// <remarks>
    /// Mutual TLS requires both a certificate and its private key, so
    /// <see cref="ClientCertificateKey"/> (or <see cref="ClientCertificateKeyFile"/>) must also be
    /// set.
    /// </remarks>
    public string? ClientCertificate { get; set; }

    /// <summary>
    /// Gets or sets the path to the PEM file holding the client certificate chain used for mutual
    /// TLS.
    /// </summary>
    public string? ClientCertificateFile { get; set; }

    /// <summary>
    /// Gets or sets the PEM-encoded private key that matches <see cref="ClientCertificate"/>.
    /// </summary>
    public string? ClientCertificateKey { get; set; }

    /// <summary>
    /// Gets or sets the path to the PEM file holding the private key that matches
    /// <see cref="ClientCertificateFile"/>.
    /// </summary>
    public string? ClientCertificateKeyFile { get; set; }

    /// <summary>
    /// Gets or sets the password that protects an encrypted client private key.
    /// </summary>
    public string? ClientCertificateKeyPassword { get; set; }

    /// <summary>
    /// Gets or sets the TLS protocol versions the client offers, or
    /// <see cref="SslProtocols.None"/> to let the operating system choose.
    /// </summary>
    /// <remarks>
    /// The default is deliberately <see cref="SslProtocols.None"/>: the operating system negotiates
    /// the strongest version both peers support, and pinning a version here would freeze the
    /// client to yesterday's recommendation.
    /// </remarks>
    public SslProtocols EnabledSslProtocols { get; set; } = SslProtocols.None;

    /// <summary>
    /// Gets the cipher suites the client offers, in preference order.
    /// </summary>
    /// <remarks>
    /// An empty list leaves cipher selection to the operating system, which is the recommended
    /// default. The values are the .NET <see cref="TlsCipherSuite"/> identifiers; they are not the
    /// OpenSSL names the Java client uses, so a list copied from a Java deployment must be
    /// translated. Windows ignores this setting and always uses its own cipher-suite policy, so it
    /// only takes effect on Linux and macOS.
    /// </remarks>
    public IList<TlsCipherSuite> CipherSuites { get; } = [];

    /// <summary>
    /// Gets a value indicating whether any TLS behaviour deviates from the platform default.
    /// </summary>
    internal bool HasCustomization =>
        AllowUntrustedCertificate
        || !string.IsNullOrEmpty(ServerCertificateAuthority)
        || !string.IsNullOrEmpty(ServerCertificateAuthorityFile)
        || !string.IsNullOrEmpty(ClientCertificate)
        || !string.IsNullOrEmpty(ClientCertificateFile)
        || EnabledSslProtocols != SslProtocols.None
        || CipherSuites.Count > 0;

    /// <summary>
    /// Validates the TLS options.
    /// </summary>
    /// <exception cref="NacosConfigurationException">A value is contradictory or incomplete.</exception>
    internal void Validate()
    {
        RequireExactlyOne(
            ServerCertificateAuthority, ServerCertificateAuthorityFile,
            nameof(ServerCertificateAuthority), nameof(ServerCertificateAuthorityFile));

        RequireExactlyOne(
            ClientCertificate, ClientCertificateFile,
            nameof(ClientCertificate), nameof(ClientCertificateFile));

        RequireExactlyOne(
            ClientCertificateKey, ClientCertificateKeyFile,
            nameof(ClientCertificateKey), nameof(ClientCertificateKeyFile));

        bool hasCertificate = !string.IsNullOrEmpty(ClientCertificate)
            || !string.IsNullOrEmpty(ClientCertificateFile);
        bool hasKey = !string.IsNullOrEmpty(ClientCertificateKey)
            || !string.IsNullOrEmpty(ClientCertificateKeyFile);

        if (hasCertificate != hasKey)
        {
            throw new NacosConfigurationException(
                "Mutual TLS needs both a client certificate and its private key; set "
                + "NacosTlsOptions.ClientCertificate/ClientCertificateFile and "
                + "ClientCertificateKey/ClientCertificateKeyFile together.");
        }

        if (!string.IsNullOrEmpty(ClientCertificateKeyPassword) && !hasKey)
        {
            throw new NacosConfigurationException(
                "NacosTlsOptions.ClientCertificateKeyPassword is set but no client private key "
                + "was supplied.");
        }
    }

    /// <summary>
    /// Rejects a pair of settings where both or neither were supplied.
    /// </summary>
    /// <param name="inline">The inline value.</param>
    /// <param name="file">The file value.</param>
    /// <param name="inlineName">The inline setting name.</param>
    /// <param name="fileName">The file setting name.</param>
    /// <exception cref="NacosConfigurationException">Both or neither were supplied.</exception>
    private static void RequireExactlyOne(string? inline, string? file, string inlineName, string fileName)
    {
        bool hasInline = !string.IsNullOrEmpty(inline);
        bool hasFile = !string.IsNullOrEmpty(file);

        if (hasInline && hasFile)
        {
            throw new NacosConfigurationException(
                string.Concat(
                    "NacosTlsOptions.", inlineName, " and ", fileName,
                    " are mutually exclusive; set exactly one."));
        }
    }
}
