using System.Security.Cryptography;
using System.Text;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// Computes the MD5 hashes Nacos uses to detect configuration changes.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm is fixed by the protocol rather than chosen here: Nacos computes the lowercase
/// hexadecimal MD5 of the UTF-8 encoded content, and both the client and the server must agree
/// byte for byte or change detection silently stops working. The Java client does the same through
/// <c>MD5Utils.md5Hex(content, "UTF-8")</c>.
/// </para>
/// <para>
/// MD5 is used here strictly as a change-detection checksum over non-secret configuration
/// content, which is the only role the protocol gives it. It provides no security property and
/// must not be relied on as a signature.
/// </para>
/// </remarks>
internal static class NacosMd5
{
    /// <summary>
    /// Computes the lowercase hexadecimal MD5 of a configuration value.
    /// </summary>
    /// <param name="content">The configuration content.</param>
    /// <returns>The 32-character lowercase hexadecimal hash.</returns>
    /// <remarks>
    /// The algorithm is not a choice: Nacos computes and exchanges MD5 hashes, so using anything
    /// else would make change detection incompatible with every server. The suppression below is
    /// therefore narrow and deliberate — this method hashes configuration content for change
    /// detection and nothing else, and its result is never treated as a security guarantee.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "MD5 is mandated by the Nacos change-detection protocol; the hash is a checksum over configuration content and provides no security property.")]
    public static string Compute(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        byte[] bytes = Encoding.UTF8.GetBytes(content);
        byte[] hash = MD5.HashData(bytes);

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Compares two hashes for equality, tolerating a null or empty value on either side.
    /// </summary>
    /// <param name="left">The first hash.</param>
    /// <param name="right">The second hash.</param>
    /// <returns><see langword="true"/> when both hashes are present and equal.</returns>
    /// <remarks>
    /// Hash comparison is case-insensitive because a server implementation is not obliged to
    /// produce lowercase hexadecimal even though the reference server does.
    /// </remarks>
    public static bool AreEqual(string? left, string? right) =>
        !string.IsNullOrEmpty(left)
        && !string.IsNullOrEmpty(right)
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
