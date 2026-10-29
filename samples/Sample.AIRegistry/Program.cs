using Lolitacoding.Nacos;
using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Demonstrates the AI registry from a host: the five resources Nacos serves, each read the way the
// server exposes it. Three of them are reads over the AI connection (MCP servers, prompts, A2A agent
// cards), and two are HTTP-only (skills and agent specifications), so a caller that only reads those
// never opens the connection.
//
// Nothing here publishes: a release, a skill upload and a card publish are things an operator or a
// deployment does, and the quick start shows each of them.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

string serverAddress = builder.Configuration["Nacos:ServerAddress"] ?? "http://127.0.0.1:8848";
string mcpServerName = builder.Configuration["Nacos:McpServer"] ?? "weather";
string promptKey = builder.Configuration["Nacos:Prompt"] ?? "greeting";
string skillName = builder.Configuration["Nacos:Skill"] ?? "release-notes";
string agentSpecName = builder.Configuration["Nacos:AgentSpec"] ?? "support-worker";
string agentName = builder.Configuration["Nacos:Agent"] ?? "support-agent";

builder.Services.AddNacos(options =>
{
    options.ServerAddresses = [serverAddress];
    options.Namespace = "public";

    // Nacos pushes no AI change, so a subscription is a poll. Five seconds keeps the demo readable.
    options.AI.CacheRefreshInterval = TimeSpan.FromSeconds(5);
});

using IHost host = builder.Build();

ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sample.AIRegistry");
INacosAIClient ai = host.Services.GetRequiredService<INacosAIClient>();

logger.LogInformation("Reading the AI registry on {Server}.", serverAddress);

// 1. An MCP server: what it is, which version is current, and where it is reachable.
try
{
    NacosMcpServerDetail server = await ai.Mcp.GetServerAsync(mcpServerName, ct: default);

    logger.LogInformation(
        "MCP {Name} {Version}: {ToolCount} tool(s), {EndpointCount} endpoint(s).",
        server.Name,
        server.VersionDetail?.Version,
        server.ToolSpec?.Tools?.Count ?? 0,
        server.BackendEndpoints?.Count ?? 0);
}
catch (NacosException ex)
{
    // A missing server is a normal state in a demo, not a failure of the client.
    logger.LogWarning("MCP server '{Name}' could not be read: {Reason}", mcpServerName, ex.Message);
}

// 2. A prompt: the template the server currently points at.
try
{
    NacosPrompt prompt = await ai.Prompt.GetPromptAsync(promptKey, ct: default);

    logger.LogInformation(
        "Prompt {Key} {Version}: {Template}", prompt.PromptKey, prompt.Version, prompt.Template);
}
catch (NacosException ex)
{
    logger.LogWarning("Prompt '{Key}' could not be read: {Reason}", promptKey, ex.Message);
}

// 3. A skill: a directory served as an archive, so the read hands back the bytes and the caller
//    decides what to do with them.
try
{
    NacosSkill skill = await ai.Skill.GetSkillAsync(skillName, ct: default);

    logger.LogInformation(
        "Skill {Name} {Version} ({Hash}): {Bytes} bytes.",
        skill.Name, skill.Version, skill.Md5, skill.Archive.Length);
}
catch (NacosException ex)
{
    logger.LogWarning("Skill '{Name}' could not be read: {Reason}", skillName, ex.Message);
}

// 4. An agent specification: a manifest plus the files a worker loads, with the version and the hash
//    in the response headers rather than in the body.
try
{
    NacosAgentSpec spec = await ai.AgentSpec.GetAgentSpecAsync(agentSpecName, ct: default);

    logger.LogInformation(
        "Agent spec {Name} {Version} ({Hash}): {ResourceCount} resource(s).",
        spec.Name, spec.Version, spec.Md5, spec.Resource?.Count ?? 0);
}
catch (NacosException ex)
{
    logger.LogWarning("Agent spec '{Name}' could not be read: {Reason}", agentSpecName, ex.Message);
}

// 5. An A2A agent card: what the agent is and where to call it. This one is served over the AI
//    connection, unlike the two above.
try
{
    NacosAgentCardDetail card = await ai.AgentCard.GetAgentCardAsync(agentName, ct: default);

    logger.LogInformation(
        "Agent card {Name} {Version} -> {Url} ({SkillCount} skill(s)).",
        card.Name, card.Version, card.Url, card.Skills?.Count ?? 0);
}
catch (NacosException ex)
{
    logger.LogWarning("Agent card '{Name}' could not be read: {Reason}", agentName, ex.Message);
}

// 6. Following an MCP server: the current detail first, then a line per change, and null when the
//    server stops being served. A subscription outlives a control-plane outage, so this loop is
//    meant to be left running.
logger.LogInformation("Following MCP server '{Name}'. Press Ctrl+C to stop.", mcpServerName);

using var stopping = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

try
{
    await foreach (NacosMcpServerDetail change in
        ai.Mcp.SubscribeServerAsync(mcpServerName, ct: stopping.Token))
    {
        logger.LogInformation(
            "MCP {Name} is now {Version} with {ToolCount} tool(s).",
            change.Name,
            change.VersionDetail?.Version,
            change.ToolSpec?.Tools?.Count ?? 0);
    }
}
catch (OperationCanceledException)
{
    logger.LogInformation("Stopped following '{Name}'.", mcpServerName);
}
