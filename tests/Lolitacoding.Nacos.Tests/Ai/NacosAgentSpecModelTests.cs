using System.Text.Json;
using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Protocol;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies the JSON contract the agent-specification endpoint answers with.
/// </summary>
/// <remarks>
/// This resource is the only AI one whose payload is JSON inside the API's result envelope, so the
/// contract is worth pinning: the envelope's members, the specification's fields, and the resource
/// map whose keys are identifiers rather than paths. The metadata is free-form, so it must survive as
/// JSON instead of being projected onto a model that would drop a member a later server adds.
/// </remarks>
public sealed class NacosAgentSpecModelTests
{
    [Fact]
    public void Deserialize_MapsTheEnvelopeAndTheSpecification()
    {
        NacosAgentSpecResult? result = Parse(AgentSpecJson.Build());

        result.ShouldNotBeNull();
        result.Code.ShouldBe(0);
        result.Message.ShouldBe("success");

        NacosAgentSpec spec = result.Data.ShouldNotBeNull();

        spec.NamespaceId.ShouldBe("public");
        spec.Name.ShouldBe("worker");
        spec.Description.ShouldBe("A worker");
        spec.BizTags.ShouldBe("""["probe"]""");
        spec.Content.ShouldBe(AgentSpecJson.DefaultContent);

        // The version and the hash are not in the payload; the endpoint reports them in the headers,
        // so a deserialized specification leaves them unset.
        spec.Version.ShouldBeNull();
        spec.Md5.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_MapsAResourceByIdentifierAndKeepsItsMetadata()
    {
        NacosAgentSpec spec = Parse(AgentSpecJson.Build()).Data.ShouldNotBeNull();

        NacosAgentSpecResource resource = spec.Resource.ShouldNotBeNull()["config_SOUL__md"];

        resource.Name.ShouldBe("SOUL.md");
        resource.Type.ShouldBe("config");
        resource.Content.ShouldBe("You are a worker.");
        resource.ResourceIdentifier.ShouldBe("config::SOUL.md");

        // The metadata is reported as the server sent it rather than projected onto a model, so a
        // member this client does not know about still reaches the caller.
        resource.Metadata.ShouldNotBeNull();
        resource.Metadata.Value.TryGetProperty("uniformId", out JsonElement uniformId).ShouldBeTrue();
        uniformId.GetInt32().ShouldBe(1);
    }

    [Fact]
    public void Deserialize_ToleratesAMissingResourceMap()
    {
        NacosAgentSpec spec = Parse(AgentSpecJson.Build(includeResource: false)).Data.ShouldNotBeNull();

        spec.Name.ShouldBe("worker");
        spec.Resource.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_IgnoresMembersThisClientDoesNotModel()
    {
        // A newer server may add fields; the contract is that they do not break the read.
        string specification = string.Concat(
            """{"name":"worker","content":"{}","addedLater":{"nested":true}}""");

        NacosAgentSpec spec = Parse(specification).Data.ShouldNotBeNull();

        spec.Name.ShouldBe("worker");
        spec.Content.ShouldBe("{}");
        spec.Resource.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_ReportsAFailedEnvelope()
    {
        NacosAgentSpecResult? result = JsonSerializer.Deserialize(
            """{"code":20004,"message":"resource not found","data":null}""",
            NacosJsonContext.Default.NacosAgentSpecResult);

        result.ShouldNotBeNull();
        result.Code.ShouldBe(20004);
        result.Message.ShouldBe("resource not found");
        result.Data.ShouldBeNull();
    }

    [Fact]
    public void Serialize_RoundTripsTheSpecification()
    {
        NacosAgentSpec original = Parse(AgentSpecJson.Build()).Data.ShouldNotBeNull();

        // A subscription compares two reads by the serialized form, so the same state must serialize
        // the same way twice and must not lose the resource it was given.
        string first = JsonSerializer.Serialize(original, NacosJsonContext.Default.NacosAgentSpec);
        string second = JsonSerializer.Serialize(original, NacosJsonContext.Default.NacosAgentSpec);

        first.ShouldBe(second);

        NacosAgentSpec? roundTripped = JsonSerializer.Deserialize(
            first, NacosJsonContext.Default.NacosAgentSpec);

        roundTripped.ShouldNotBeNull();
        roundTripped.Resource.ShouldNotBeNull().Count.ShouldBe(1);
        roundTripped.Resource["config_SOUL__md"].Content.ShouldBe("You are a worker.");
    }

    /// <summary>
    /// Deserializes one specification out of an envelope.
    /// </summary>
    /// <param name="specification">The specification as JSON.</param>
    /// <returns>The envelope.</returns>
    private static NacosAgentSpecResult Parse(string specification) =>
        JsonSerializer.Deserialize(
            string.Concat("{\"code\":0,\"message\":\"success\",\"data\":", specification, "}"),
            NacosJsonContext.Default.NacosAgentSpecResult).ShouldNotBeNull();
}
