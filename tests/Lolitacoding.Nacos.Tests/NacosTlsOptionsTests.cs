using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Lolitacoding.Nacos.Internal;

namespace Lolitacoding.Nacos.Tests;

/// <summary>
/// Verifies how <see cref="NacosTlsOptions"/> is validated and translated into SSL settings.
/// </summary>
/// <remarks>
/// The SSL settings are asserted through the resulting <see cref="SocketsHttpHandler"/> because
/// that is exactly what the gRPC channel and the login client consume, so the test pins the
/// contract both transports depend on.
/// </remarks>
public sealed class NacosTlsOptionsTests
{
    [Fact]
    public void CreateHandler_ReturnsNullForDefaultOptions()
    {
        NacosTlsHandler.CreateHandler(new NacosTlsOptions()).ShouldBeNull();
        NacosTlsHandler.CreateHandler(null).ShouldBeNull();
    }

    [Fact]
    public void CreateHandler_TrustsEverythingWhenUntrustedCertificatesAreAllowed()
    {
        NacosTlsOptions tls = new() { AllowUntrustedCertificate = true };

        SocketsHttpHandler handler = NacosTlsHandler.CreateHandler(tls).ShouldNotBeNull();

        RemoteCertificateValidationCallback? callback = handler.SslOptions.RemoteCertificateValidationCallback;
        callback.ShouldNotBeNull();
        callback!(new object(), null, null, SslPolicyErrors.RemoteCertificateNameMismatch).ShouldBeTrue();
    }

    [Fact]
    public void CreateHandler_AppliesTheConfiguredProtocols()
    {
        NacosTlsOptions tls = new() { EnabledSslProtocols = SslProtocols.Tls12 };

        SocketsHttpHandler handler = NacosTlsHandler.CreateHandler(tls).ShouldNotBeNull();

        handler.SslOptions.EnabledSslProtocols.ShouldBe(SslProtocols.Tls12);
    }

    [Fact]
    public void CreateHandler_TrustsTheSuppliedCertificateAuthority()
    {
        using X509Certificate2 authority = CreateSelfSigned("CN=Test Authority").certificate;
        string authorityPem = authority.ExportCertificatePem();

        NacosTlsOptions tls = new() { ServerCertificateAuthority = authorityPem };

        SocketsHttpHandler handler = NacosTlsHandler.CreateHandler(tls).ShouldNotBeNull();

        X509ChainPolicy? policy = handler.SslOptions.CertificateChainPolicy;
        policy.ShouldNotBeNull();
        policy!.TrustMode.ShouldBe(X509ChainTrustMode.CustomRootTrust);
        policy.CustomTrustStore.Count.ShouldBe(1);
    }

    [Fact]
    public void CreateHandler_PresentsTheClientCertificateForMutualTls()
    {
        (X509Certificate2 certificate, string keyPem) = CreateSelfSigned("CN=Test Client");
        using X509Certificate2 client = certificate;

        NacosTlsOptions tls = new()
        {
            ClientCertificate = certificate.ExportCertificatePem(),
            ClientCertificateKey = keyPem,
        };

        SocketsHttpHandler handler = NacosTlsHandler.CreateHandler(tls).ShouldNotBeNull();

        handler.SslOptions.ClientCertificates.ShouldNotBeNull();
        handler.SslOptions.ClientCertificates!.Count.ShouldBe(1);
    }

    [Fact]
    public void Validate_RejectsBothAnInlineAndAFileAuthority()
    {
        NacosTlsOptions tls = new()
        {
            ServerCertificateAuthority = "-----BEGIN CERTIFICATE-----",
            ServerCertificateAuthorityFile = "ca.pem",
        };

        Should.Throw<NacosConfigurationException>(tls.Validate);
    }

    [Fact]
    public void Validate_RequiresTheClientKeyAlongsideTheClientCertificate()
    {
        NacosTlsOptions tls = new() { ClientCertificate = "-----BEGIN CERTIFICATE-----" };

        Should.Throw<NacosConfigurationException>(tls.Validate);
    }

    [Fact]
    public void Validate_RejectsAKeyPasswordWithoutAKey()
    {
        NacosTlsOptions tls = new() { ClientCertificateKeyPassword = "secret" };

        Should.Throw<NacosConfigurationException>(tls.Validate);
    }

    [Fact]
    public void NacosOptions_Validate_DescendsIntoTheTlsOptions()
    {
        NacosOptions options = new();
        options.Tls.ClientCertificate = "-----BEGIN CERTIFICATE-----";

        Should.Throw<NacosConfigurationException>(options.Validate);
    }

    /// <summary>Creates a throwaway self-signed certificate and its PKCS#8 private key.</summary>
    /// <param name="subject">The certificate subject.</param>
    /// <returns>The certificate and the PEM-encoded private key.</returns>
    private static (X509Certificate2 certificate, string keyPem) CreateSelfSigned(string subject)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        return (certificate, rsa.ExportPkcs8PrivateKeyPem());
    }
}
