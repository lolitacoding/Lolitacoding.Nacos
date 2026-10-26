using Lolitacoding.Nacos.Config;

namespace Lolitacoding.Nacos.Tests.Config;

/// <summary>
/// A reversible stand-in for an encryption plugin.
/// </summary>
/// <remarks>
/// The transform is deliberately readable rather than cryptographic: tests assert the filter
/// chain's contract — when a provider runs, which one is selected, what is stored next to the
/// content — not the strength of any cipher.
/// </remarks>
internal sealed class FakeEncryptionProvider : INacosConfigEncryptionProvider
{
    private const string CipherPrefix = "cipher(";
    private const string WrappedPrefix = "wrapped(";

    /// <summary>Gets or sets the algorithm name this provider answers to.</summary>
    public string AlgorithmName { get; init; } = "AES";

    /// <summary>Gets or sets a label that distinguishes two providers in assertions.</summary>
    public string Marker { get; init; } = string.Empty;

    /// <summary>Gets every call this provider received, in order.</summary>
    public List<string> Calls { get; } = [];

    public string GenerateSecretKey()
    {
        Calls.Add("generate");
        return "secret";
    }

    public string Encrypt(string secretKey, string content)
    {
        Calls.Add($"encrypt:{secretKey}");
        return string.Concat(CipherPrefix, content, ")");
    }

    public string Decrypt(string secretKey, string content)
    {
        Calls.Add($"decrypt:{secretKey}");
        return Unwrap(content, CipherPrefix);
    }

    public string EncryptSecretKey(string secretKey)
    {
        Calls.Add($"wrap:{secretKey}");
        return string.Concat(WrappedPrefix, secretKey, ")");
    }

    public string DecryptSecretKey(string encryptedSecretKey)
    {
        Calls.Add($"unwrap:{encryptedSecretKey}");
        return Unwrap(encryptedSecretKey, WrappedPrefix);
    }

    private static string Unwrap(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith(')')
            ? value[prefix.Length..^1]
            : value;
}
