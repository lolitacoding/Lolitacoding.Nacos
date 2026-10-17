using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Shared helpers for talking to a running Nacos server over HTTP.
/// </summary>
/// <remarks>
/// These helpers deliberately use raw <see cref="HttpClient"/> calls rather than the client under
/// test. An integration test that used the client to set up its own preconditions could not tell
/// a client bug from a server one, so setup and verification go through the OpenAPI directly
/// while only the behaviour under test uses the client.
/// </remarks>
internal static class NacosHttp
{
    /// <summary>The admin config endpoint of the v3 HTTP API.</summary>
    private const string ConfigPath = "/nacos/v3/admin/cs/config";

    /// <summary>The admin prompt endpoint of the v3 HTTP API.</summary>
    private const string PromptPath = "/nacos/v3/admin/ai/prompt";

    /// <summary>The admin skill endpoint of the v3 HTTP API.</summary>
    private const string SkillPath = "/nacos/v3/admin/ai/skills";

    /// <summary>The result code the v3 API reports for a resource that does not exist.</summary>
    private const int ResourceNotFoundCode = 20004;

    /// <summary>
    /// Publishes or replaces a configuration.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="dataId">The configuration identifier.</param>
    /// <param name="group">The configuration group.</param>
    /// <param name="content">The configuration content.</param>
    /// <param name="accessToken">The access token, when the server requires authentication.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// The v3 admin API, which is the part of the HTTP surface that can write a configuration: the
    /// v3 client-facing config API only reads, and the v1 write API the 2.x servers exposed is gone.
    /// The endpoint takes the group as <c>groupName</c> and answers with a result envelope. It is an
    /// administrative call, so it needs an access token even on a server whose authentication plugin
    /// is switched off — where there is then no account to log in with, and no way to seed a
    /// configuration over HTTP at all.
    /// </remarks>
    public static async Task PublishConfigAsync(
        HttpClient http,
        string baseAddress,
        string dataId,
        string group,
        string content,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("dataId", dataId),
            new KeyValuePair<string, string>("groupName", group),
            new KeyValuePair<string, string>("content", content),
            new KeyValuePair<string, string>("type", "text"),
        ]);

        using HttpResponseMessage response = await SendAsync(
            http, HttpMethod.Post, string.Concat(baseAddress, ConfigPath), accessToken, form, ct);

        response.EnsureSuccessStatusCode();
        EnsureSucceeded(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Reads a configuration.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="dataId">The configuration identifier.</param>
    /// <param name="group">The configuration group.</param>
    /// <param name="accessToken">The access token, when the server requires authentication.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The configuration content, or <see langword="null"/> when absent.</returns>
    public static async Task<string?> GetConfigAsync(
        HttpClient http,
        string baseAddress,
        string dataId,
        string group,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        string url = string.Concat(
            baseAddress, ConfigPath,
            "?dataId=", Uri.EscapeDataString(dataId),
            "&groupName=", Uri.EscapeDataString(group));

        using HttpResponseMessage response = await SendAsync(
            http, HttpMethod.Get, url, accessToken, content: null, ct);

        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(ct);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("code", out JsonElement code) && code.GetInt32() == ResourceNotFoundCode)
        {
            // An absent configuration is a result code rather than a 404, as it is for the client API.
            return null;
        }

        EnsureSucceeded(body);

        return root.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
    }

    /// <summary>
    /// Creates or replaces one version of a prompt.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version to publish.</param>
    /// <param name="template">The template text.</param>
    /// <param name="variables">The variable metadata as JSON, or <see langword="null"/> for none.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// A prompt is published in two steps: a draft carries the content, and force-publishing the
    /// draft is what makes the version the one a read resolves. There is no way to store a prompt
    /// that is immediately readable, which is why the fixture drives both calls, and the raw config
    /// API cannot stand in for them: a configuration in the prompt group is only a migration source,
    /// not something a prompt read will answer from.
    /// </remarks>
    public static async Task PublishPromptAsync(
        HttpClient http,
        string baseAddress,
        string promptKey,
        string version,
        string template,
        string? variables = null,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("promptKey", promptKey),
            new("targetVersion", version),
            new("template", template),
            new("commitMsg", "Seeded by the integration suite."),
            new("description", "A prompt seeded by the integration suite."),
            new("bizTags", "integration"),
        };

        if (variables is not null)
        {
            fields.Add(new KeyValuePair<string, string>("variables", variables));
        }

        using (var draft = new FormUrlEncodedContent(fields))
        {
            using HttpResponseMessage drafted = await SendAsync(
                http,
                HttpMethod.Post,
                string.Concat(baseAddress, PromptPath, "/draft"),
                accessToken,
                draft,
                ct);

            drafted.EnsureSuccessStatusCode();
            EnsureSucceeded(await drafted.Content.ReadAsStringAsync(ct));
        }

        using var publish = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("promptKey", promptKey),
            new KeyValuePair<string, string>("version", version),
        ]);

        using HttpResponseMessage published = await SendAsync(
            http,
            HttpMethod.Post,
            string.Concat(baseAddress, PromptPath, "/force-publish"),
            accessToken,
            publish,
            ct);

        published.EnsureSuccessStatusCode();
        EnsureSucceeded(await published.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Binds the runtime labels of a prompt to versions.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="labels">The labels as a JSON object of label to version.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// A label is a movable pointer into the version list, which is what a caller reads when it wants
    /// "whatever is currently stable" rather than a version it has to be told. The reserved
    /// <c>latest</c> label is not set this way; the server moves it when a version is published.
    /// </remarks>
    public static async Task BindPromptLabelsAsync(
        HttpClient http,
        string baseAddress,
        string promptKey,
        string labels,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("promptKey", promptKey),
            new KeyValuePair<string, string>("labels", labels),
        ]);

        using HttpResponseMessage response = await SendAsync(
            http,
            HttpMethod.Put,
            string.Concat(baseAddress, PromptPath, "/labels"),
            accessToken,
            form,
            ct);

        response.EnsureSuccessStatusCode();
        EnsureSucceeded(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Deletes a prompt, which is what withdraws it from every read.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// Every version and every label goes with the prompt, so a read afterwards is answered the way a
    /// read of a key that was never published is. That is what makes this the way to put a subscription
    /// into the withdrawn state it has to report.
    /// </remarks>
    public static async Task DeletePromptAsync(
        HttpClient http,
        string baseAddress,
        string promptKey,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        string url = string.Concat(
            baseAddress, PromptPath, "?promptKey=", Uri.EscapeDataString(promptKey));

        using HttpResponseMessage response = await SendAsync(
            http, HttpMethod.Delete, url, accessToken, content: null, ct);

        response.EnsureSuccessStatusCode();
        EnsureSucceeded(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Builds the markdown of a skill, with the frontmatter the registry requires.
    /// </summary>
    /// <param name="name">The skill name, which the frontmatter repeats.</param>
    /// <param name="version">The version the frontmatter states.</param>
    /// <param name="body">The instructions below the frontmatter.</param>
    /// <returns>The markdown.</returns>
    /// <remarks>
    /// The name and the description are required in the frontmatter as well as in the card: the
    /// registry validates both, and rejects a document whose body is empty once the frontmatter is
    /// removed.
    /// </remarks>
    public static string SkillMarkdown(string name, string version, string body) =>
        string.Concat(
            "---\nname: ", name,
            "\ndescription: A skill seeded by the integration suite.\nversion: ", version,
            "\n---\n\n", body, "\n");

    /// <summary>
    /// Publishes one version of a skill.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="skillName">The skill name.</param>
    /// <param name="version">The version to publish.</param>
    /// <param name="markdown">The skill's <c>SKILL.md</c>.</param>
    /// <param name="basedOnVersion">
    /// The version to fork from, or <see langword="null"/> when the skill is being created.
    /// </param>
    /// <param name="resource">
    /// One resource file to include in the skill, or <see langword="null"/> for none. The file lands
    /// under <c>{skillName}/{name}</c> in the archive.
    /// </param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// <para>
    /// A skill is published in up to three steps, and the first one differs by whether the skill
    /// exists. A new skill is created from its card; a later version has to be forked from an existing
    /// one, and the fork's content is then replaced on the draft, because the create call refuses a
    /// card for an existing skill. Publishing the draft is what makes the version readable.
    /// </para>
    /// <para>
    /// The fixture drives the admin API rather than the client, because there is no client-side way to
    /// publish a skill — the client surface is the read path under test.
    /// </para>
    /// </remarks>
    public static async Task PublishSkillAsync(
        HttpClient http,
        string baseAddress,
        string skillName,
        string version,
        string markdown,
        string? basedOnVersion = null,
        (string Name, string Content)? resource = null,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        string card = SkillCard(skillName, markdown, resource);

        if (basedOnVersion is null)
        {
            using var create = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("skillCard", card),
                new KeyValuePair<string, string>("targetVersion", version),
                new KeyValuePair<string, string>("commitMsg", "Seeded by the integration suite."),
            ]);

            await SendSkillAsync(
                http, HttpMethod.Post, string.Concat(baseAddress, SkillPath, "/draft"),
                accessToken, create, ct);
        }
        else
        {
            using var fork = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("skillName", skillName),
                new KeyValuePair<string, string>("basedOnVersion", basedOnVersion),
                new KeyValuePair<string, string>("targetVersion", version),
                new KeyValuePair<string, string>("commitMsg", "Seeded by the integration suite."),
            ]);

            await SendSkillAsync(
                http, HttpMethod.Post, string.Concat(baseAddress, SkillPath, "/draft"),
                accessToken, fork, ct);

            using var edit = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("skillCard", card),
                new KeyValuePair<string, string>("version", version),
            ]);

            await SendSkillAsync(
                http, HttpMethod.Put, string.Concat(baseAddress, SkillPath, "/draft"),
                accessToken, edit, ct);
        }

        using var publish = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("skillName", skillName),
            new KeyValuePair<string, string>("version", version),
        ]);

        await SendSkillAsync(
            http, HttpMethod.Post, string.Concat(baseAddress, SkillPath, "/force-publish"),
            accessToken, publish, ct);
    }

    /// <summary>
    /// Binds the runtime labels of a skill to versions.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="skillName">The skill name.</param>
    /// <param name="labels">The labels as a JSON object of label to version.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// The labels are sent as a whole map rather than one at a time, so a test that binds a label
    /// writes the reserved <c>latest</c> label back as well. Leaving it out is accepted and drops it,
    /// which turns a later read without a selector into a missing version rather than a server error.
    /// </remarks>
    public static async Task BindSkillLabelsAsync(
        HttpClient http,
        string baseAddress,
        string skillName,
        string labels,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("skillName", skillName),
            new KeyValuePair<string, string>("labels", labels),
        ]);

        await SendSkillAsync(
            http, HttpMethod.Put, string.Concat(baseAddress, SkillPath, "/labels"),
            accessToken, form, ct);
    }

    /// <summary>
    /// Takes one version of a skill offline.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="skillName">The skill name.</param>
    /// <param name="version">The version to take offline.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// An offline version is no longer indexed, so a read that resolved to it is answered the way an
    /// unknown version is. Taking the only version offline is therefore what puts a subscription into
    /// the withdrawn state it has to report.
    /// </remarks>
    public static async Task OfflineSkillAsync(
        HttpClient http,
        string baseAddress,
        string skillName,
        string version,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("skillName", skillName),
            new KeyValuePair<string, string>("version", version),
        ]);

        await SendSkillAsync(
            http, HttpMethod.Post, string.Concat(baseAddress, SkillPath, "/offline"),
            accessToken, form, ct);
    }

    /// <summary>
    /// Attempts a login and reports whether the server accepted the credentials.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="userName">The user name to present.</param>
    /// <param name="password">The password to present.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The access token, or <see langword="null"/> when the server rejected the login.</returns>
    public static async Task<string?> TryLoginAsync(
        HttpClient http,
        string baseAddress,
        string userName,
        string password,
        CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("username", userName),
            new KeyValuePair<string, string>("password", password),
        ]);

        var response = await http.PostAsync(
            string.Concat(baseAddress, "/nacos/v3/auth/user/login"), form, ct);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(ct);

            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("accessToken", out JsonElement token)
                ? token.GetString()
                : null;
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        HttpMethod method,
        string url,
        string? accessToken,
        HttpContent? content,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };

        if (!string.IsNullOrEmpty(accessToken))
        {
            // The server accepts a bearer Authorization header and an accessToken parameter;
            // the header is the form a real client uses when it has a token rather than a login.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await http.SendAsync(request, ct);
    }

    /// <summary>
    /// Sends one skill admin request and fails the fixture when the envelope reports a failure.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The URL.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="content">The form.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The task.</returns>
    private static async Task SendSkillAsync(
        HttpClient http,
        HttpMethod method,
        string url,
        string? accessToken,
        HttpContent content,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(http, method, url, accessToken, content, ct);

        response.EnsureSuccessStatusCode();
        EnsureSucceeded(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Builds the card a draft is created or edited from.
    /// </summary>
    /// <param name="skillName">The skill name.</param>
    /// <param name="markdown">The skill's <c>SKILL.md</c>.</param>
    /// <param name="resource">One resource file, or <see langword="null"/> for none.</param>
    /// <returns>The card as JSON.</returns>
    /// <remarks>
    /// A resource is keyed by its identifier and carries the path it is served under, so the archive
    /// the client downloads has the file at <c>{skillName}/{name}</c> rather than at a flat path.
    /// </remarks>
    private static string SkillCard(
        string skillName,
        string markdown,
        (string Name, string Content)? resource)
    {
        var card = new Dictionary<string, object?>
        {
            ["name"] = skillName,
            ["description"] = "A skill seeded by the integration suite.",
            ["skillMd"] = markdown,
        };

        if (resource is { } file)
        {
            card["resource"] = new Dictionary<string, object?>
            {
                [file.Name] = new Dictionary<string, object?>
                {
                    ["name"] = file.Name,
                    ["content"] = file.Content,
                },
            };
        }

        return JsonSerializer.Serialize(card);
    }

    /// <summary>
    /// Throws when a v3 API response reports a failure in its result envelope.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <remarks>
    /// The v3 APIs answer with success status codes and report a failure in the envelope, so a
    /// <c>code</c> other than zero is what says the call did not do what it was asked to.
    /// </remarks>
    private static void EnsureSucceeded(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("code", out JsonElement code) && code.GetInt32() != 0)
        {
            throw new InvalidOperationException(string.Concat("The Nacos API reported a failure: ", body));
        }
    }

    /// <summary>
    /// Builds a configuration body large enough to be unambiguous in an assertion.
    /// </summary>
    /// <param name="marker">A unique marker to embed.</param>
    /// <returns>The configuration content.</returns>
    public static string ConfigContent(string marker) => string.Concat(
        "marker=", marker, "\n", new string('x', 64));

    /// <summary>Creates a deterministic, unique configuration identifier.</summary>
    /// <param name="prefix">A prefix identifying the test.</param>
    /// <returns>The identifier.</returns>
    public static string UniqueId(string prefix) => string.Concat(
        prefix, "-", Guid.NewGuid().ToString("N")[..12]);

    /// <summary>Encodes a string as UTF-8 bytes for a raw HTTP body.</summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>The encoded bytes.</returns>
    public static ByteArrayContent Utf8(string value) => new(Encoding.UTF8.GetBytes(value));
}
