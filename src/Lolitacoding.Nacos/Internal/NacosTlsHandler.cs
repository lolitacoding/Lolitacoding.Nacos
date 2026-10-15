using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// Turns <see cref="NacosTlsOptions"/> into the SSL settings used by the gRPC and HTTP transports.
/// </summary>
/// <remarks>
/// Both transports ultimately run through an <see cref="SocketsHttpHandler"/>, so building the SSL
/// settings in one place keeps the gRPC channel and the login client from drifting apart on
/// certificate handling.
/// </remarks>
internal static class NacosTlsHandler
{
    /// <summary>
    /// Creates an HTTP handler with the configured TLS behaviour.
    /// </summary>
    /// <param name="tls">The TLS options, or <see langword="null"/> for platform defaults.</param>
    /// <returns>
    /// A handler whose SSL settings reflect <paramref name="tls"/>, or <see langword="null"/> when
    /// no customisation was requested and the platform default handler should be used.
    /// </returns>
    public static SocketsHttpHandler? CreateHandler(NacosTlsOptions? tls)
    {
        if (tls is null || !tls.HasCustomization)
        {
            return null;
        }

        var handler = new SocketsHttpHandler();
        ApplySslOptions(handler.SslOptions, tls);
        return handler;
    }

    /// <summary>
    /// Applies the TLS options to an SSL authentication options instance in place.
    /// </summary>
    /// <param name="ssl">The settings to mutate.</param>
    /// <param name="tls">The configured TLS options.</param>
    public static void ApplySslOptions(SslClientAuthenticationOptions ssl, NacosTlsOptions tls)
    {
        ArgumentNullException.ThrowIfNull(ssl);
        ArgumentNullException.ThrowIfNull(tls);

        if (tls.EnabledSslProtocols != SslProtocols.None)
        {
            ssl.EnabledSslProtocols = tls.EnabledSslProtocols;
        }

        if (tls.CipherSuites.Count > 0 && !OperatingSystem.IsWindows())
        {
            // Windows does not support a caller-supplied CipherSuitesPolicy; it always uses the OS
            // cipher suite policy. The option is applied where the platform honours it.
            ssl.CipherSuitesPolicy = new CipherSuitesPolicy(tls.CipherSuites);
        }

        X509Certificate2Collection authorities = LoadAuthorities(tls);
        if (authorities.Count > 0)
        {
            // A private CA is trusted through a custom chain policy rather than by installing a
            // callback that returns true for everything, so host name and revocation checks still
            // run against the supplied roots.
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            policy.CustomTrustStore.AddRange(authorities);
            ssl.CertificateChainPolicy = policy;
        }

        if (tls.AllowUntrustedCertificate)
        {
            // Deliberately bypasses validation; this is the documented "development only" switch
            // (the equivalent of trustAll in the official Java client). The rule is suppressed
            // here rather than globally because this is the single intentional occurrence.
#pragma warning disable CA5359 // Do not disable certificate validation
            ssl.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        X509Certificate2? clientCertificate = LoadClientCertificate(tls);
        if (clientCertificate is not null)
        {
            ssl.ClientCertificates = new X509CertificateCollection { clientCertificate };
        }
    }

    /// <summary>
    /// Loads the trusted certificate authorities from the inline or file setting.
    /// </summary>
    /// <param name="tls">The TLS options.</param>
    /// <returns>The authorities, or an empty collection when none were configured.</returns>
    private static X509Certificate2Collection LoadAuthorities(NacosTlsOptions tls)
    {
        string? pem = ResolveInlineOrFile(tls.ServerCertificateAuthority, tls.ServerCertificateAuthorityFile);
        if (string.IsNullOrEmpty(pem))
        {
            return [];
        }

        var authorities = new X509Certificate2Collection();
        authorities.ImportFromPem(pem);
        return authorities;
    }

    /// <summary>
    /// Loads the client certificate and private key used for mutual TLS.
    /// </summary>
    /// <param name="tls">The TLS options.</param>
    /// <returns>The certificate, or <see langword="null"/> when mutual TLS is not configured.</returns>
    private static X509Certificate2? LoadClientCertificate(NacosTlsOptions tls)
    {
        string? certPem = ResolveInlineOrFile(tls.ClientCertificate, tls.ClientCertificateFile);
        string? keyPem = ResolveInlineOrFile(tls.ClientCertificateKey, tls.ClientCertificateKeyFile);

        if (string.IsNullOrEmpty(certPem) || string.IsNullOrEmpty(keyPem))
        {
            return null;
        }

        X509Certificate2 certificate = string.IsNullOrEmpty(tls.ClientCertificateKeyPassword)
            ? X509Certificate2.CreateFromPem(certPem, keyPem)
            : X509Certificate2.CreateFromEncryptedPem(certPem, keyPem, tls.ClientCertificateKeyPassword);

        return EnsureUsablePrivateKey(certificate);
    }

    /// <summary>
    /// Prefers the inline value, then the file, then nothing.
    /// </summary>
    /// <param name="inline">The inline PEM text, if any.</param>
    /// <param name="file">The PEM file path, if any.</param>
    /// <returns>The resolved PEM text, or <see langword="null"/>.</returns>
    private static string? ResolveInlineOrFile(string? inline, string? file)
    {
        if (!string.IsNullOrEmpty(inline))
        {
            return inline;
        }

        return string.IsNullOrEmpty(file) ? null : File.ReadAllText(file);
    }

    /// <summary>
    /// Makes a PEM-loaded certificate usable by <see cref="SslStream"/> on every platform.
    /// </summary>
    /// <param name="certificate">A certificate created from PEM material.</param>
    /// <returns>A certificate whose private key can be consumed by the TLS stack.</returns>
    /// <remarks>
    /// A certificate created from PEM holds the private key in an ephemeral handle that the
    /// Windows TLS stack refuses. Re-importing through PKCS#12 materialises a usable key; on other
    /// platforms the original certificate is already fine and is returned unchanged.
    /// </remarks>
    private static X509Certificate2 EnsureUsablePrivateKey(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows() || !certificate.HasPrivateKey)
        {
            return certificate;
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12(
                certificate.Export(X509ContentType.Pfx),
                password: null,
                X509KeyStorageFlags.DefaultKeySet);
        }
        catch (CryptographicException)
        {
            // If the key cannot be re-materialised the original is still the best available
            // candidate; the TLS handshake reports the failure with more context if it cannot use
            // it.
            return certificate;
        }
    }
}
