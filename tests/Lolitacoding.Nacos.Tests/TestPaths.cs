namespace Lolitacoding.Nacos.Tests;

/// <summary>
/// Provides scratch directories for tests that touch the file system.
/// </summary>
/// <remarks>
/// <para>
/// Scratch data is created under the test output directory rather than the system temporary
/// directory. The system temporary directory is not reliably writable for a test host — it can be
/// restricted by policy or by a sandbox around the process — and a test that depends on it fails
/// for reasons unrelated to the code under test.
/// </para>
/// <para>
/// The output directory is also cleaned by the normal build, so a crashed test cannot leave
/// scratch data behind indefinitely.
/// </para>
/// </remarks>
internal static class TestPaths
{
    /// <summary>
    /// Creates a unique empty scratch directory.
    /// </summary>
    /// <param name="purpose">A short label identifying the caller, used in the path.</param>
    /// <returns>The full path of the created directory.</returns>
    public static string CreateScratchDirectory(string purpose)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "test-scratch",
            purpose,
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);

        return path;
    }

    /// <summary>
    /// Deletes a scratch directory created by <see cref="CreateScratchDirectory"/>.
    /// </summary>
    /// <param name="path">The directory to delete.</param>
    /// <remarks>
    /// Cleanup is best-effort: a failure to delete scratch data must never fail a test whose
    /// assertions already passed.
    /// </remarks>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leftover scratch data is harmless and is cleared by the next clean build.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
