using System.Text.Json;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Waits until a Nacos server reports that it can serve traffic.
/// </summary>
/// <remarks>
/// Readiness is reported by the v3 admin core API on the main server port. The endpoint answers with
/// a success status code as soon as it can report on itself, which is before every module is serving:
/// a starting 3.0 server answers <c>{"code":30000,"message":"naming not in readiness"}</c> for the
/// first seconds of its life. Waiting on the status code alone therefore lets a test through while
/// naming is still unavailable, and the registration tests fail for a reason that has nothing to do
/// with the client. The body is what says whether the server is ready, so that is what is checked.
/// </remarks>
internal static class NacosReadiness
{
    /// <summary>The readiness endpoint's path on the main server port.</summary>
    public const string Path = "/nacos/v3/admin/core/state/readiness";

    /// <summary>How long to wait for a server to report readiness before giving up.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>How long to wait between two readiness probes.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Waits until the server at the given address reports that it is ready.
    /// </summary>
    /// <param name="baseAddress">The server's HTTP base address.</param>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>A task that completes once the server is ready.</returns>
    /// <exception cref="TimeoutException">The server did not report readiness in time.</exception>
    public static async Task WaitAsync(string baseAddress, CancellationToken ct = default)
    {
        string url = string.Concat(baseAddress, Path);
        long deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;

        using var http = new HttpClient();

        while (true)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(url, ct).ConfigureAwait(false);

                if (await IsReadyAsync(response).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The server has not opened its port yet, so a refused connection is expected here.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // The probe itself timed out rather than the caller cancelling the wait.
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(
                    string.Concat(
                        "The Nacos server did not report readiness at '", url, "' within ",
                        Timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                        " seconds."));
            }

            await Task.Delay(Interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports whether a readiness response says the server can serve traffic.
    /// </summary>
    /// <param name="response">The response to inspect.</param>
    /// <returns><see langword="true"/> when the server is ready.</returns>
    public static async Task<bool> IsReadyAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return IsReady(body);
    }

    /// <summary>
    /// Reports whether a readiness response body says the server can serve traffic.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <returns><see langword="true"/> when the server is ready.</returns>
    public static bool IsReady(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            return root.TryGetProperty("code", out JsonElement code)
                && code.ValueKind == JsonValueKind.Number
                && code.GetInt32() == 0
                && root.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.String
                && string.Equals(data.GetString(), "ok", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
