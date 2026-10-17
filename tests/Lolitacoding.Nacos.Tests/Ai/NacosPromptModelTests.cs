using System.Text;
using System.Text.Json.Nodes;
using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies the wire form of the prompt models.
/// </summary>
/// <remarks>
/// The prompt payload is small, but two of its members are load-bearing. The hash is what makes a
/// refresh conditional, so it has to travel both ways; and the client-facing prompt is a projection
/// of the stored version, so the fields the server serves are the whole contract — a member the
/// projection drops cannot be recovered by this client.
/// </remarks>
public sealed class NacosPromptModelTests
{
    [Fact]
    public void QueryPromptRequest_TravelsTheCamelCaseSelectorNamesTheServerReads()
    {
        JsonNode body = JsonNode.Parse(Encode(new QueryPromptRequest
        {
            NamespaceId = "public",
            PromptKey = "greeting",
            Version = "1.0.0",
            Label = "stable",
            Md5 = "abc",
        }))!;

        body["namespaceId"]!.GetValue<string>().ShouldBe("public");
        body["promptKey"]!.GetValue<string>().ShouldBe("greeting");
        body["version"]!.GetValue<string>().ShouldBe("1.0.0");
        body["label"]!.GetValue<string>().ShouldBe("stable");
        body["md5"]!.GetValue<string>().ShouldBe("abc");
    }

    [Fact]
    public void QueryPromptRequest_OmitsTheSelectorsTheCallerDidNotName()
    {
        JsonNode body = JsonNode.Parse(Encode(new QueryPromptRequest
        {
            NamespaceId = "public",
            PromptKey = "greeting",
        }))!;

        // Nacos serialises with Include.NON_NULL, so an unset selector is absent rather than null —
        // which is what lets the server fall back to the latest version instead of resolving "null".
        body["version"].ShouldBeNull();
        body["label"].ShouldBeNull();

        // An unset hash asks for the content unconditionally, so its absence is the request to send.
        body["md5"].ShouldBeNull();
    }

    [Fact]
    public void QueryPromptResponse_TravelsThePromptUnderTheNameTheServerUses()
    {
        JsonNode info = PromptInfo(Encode(new QueryPromptResponse
        {
            PromptInfo = new NacosPrompt
            {
                PromptKey = "greeting",
                Version = "1.0.0",
                Template = "Hello {{name}}",
                Md5 = "abc",
            },
        }));

        info["promptKey"]!.GetValue<string>().ShouldBe("greeting");
        info["version"]!.GetValue<string>().ShouldBe("1.0.0");
        info["template"]!.GetValue<string>().ShouldBe("Hello {{name}}");
        info["md5"]!.GetValue<string>().ShouldBe("abc");
    }

    [Fact]
    public void NacosPromptVariable_TravelsTheMemberNamesTheRegistryStores()
    {
        JsonNode variable = PromptInfo(Encode(new QueryPromptResponse
        {
            PromptInfo = new NacosPrompt
            {
                PromptKey = "greeting",
                Version = "1.0.0",
                Variables =
                [
                    new NacosPromptVariable
                    {
                        Name = "name",
                        DefaultValue = "Nacos",
                        Description = "Who to greet",
                    },
                ],
            },
        }))["variables"]![0]!;

        variable["name"]!.GetValue<string>().ShouldBe("name");
        variable["defaultValue"]!.GetValue<string>().ShouldBe("Nacos");
        variable["description"]!.GetValue<string>().ShouldBe("Who to greet");
    }

    [Fact]
    public void NacosPromptVariable_OmitsARequiredVariableDefault()
    {
        JsonNode variable = PromptInfo(Encode(new QueryPromptResponse
        {
            PromptInfo = new NacosPrompt
            {
                PromptKey = "greeting",
                Version = "1.0.0",
                Variables = [new NacosPromptVariable { Name = "name" }],
            },
        }))["variables"]![0]!;

        // A variable with no default is one a caller has to supply, so an absent default is the
        // signal and must not be confused with a default that happens to be empty.
        variable["defaultValue"].ShouldBeNull();
        variable["description"].ShouldBeNull();
    }

    [Fact]
    public void NacosPrompt_OmitsTheVariablesOfAPromptStoredWithoutMetadata()
    {
        JsonNode info = PromptInfo(Encode(new QueryPromptResponse
        {
            PromptInfo = new NacosPrompt
            {
                PromptKey = "legacy",
                Version = "1.0.0",
                Template = "Hello {{name}}",
            },
        }));

        // Older prompts carry placeholders but no variable metadata, so an absent list is a state
        // the caller has to tolerate rather than a malformed reply.
        info["variables"].ShouldBeNull();
    }

    [Fact]
    public void NacosPrompt_RoundTripsThroughTheWire()
    {
        var prompt = new NacosPrompt
        {
            PromptKey = "greeting",
            Version = "2.1.0",
            Template = "Hello {{name}}, welcome to {{place}}",
            Md5 = "0f1e2d",
            Variables =
            [
                new NacosPromptVariable { Name = "name", DefaultValue = "Nacos" },
                new NacosPromptVariable { Name = "place", Description = "Where they arrived" },
            ],
        };

        QueryPromptResponse decoded = PayloadCodec.Decode<QueryPromptResponse>(
            PayloadCodec.Encode(new QueryPromptResponse { PromptInfo = prompt }))!;

        decoded.PromptInfo.ShouldNotBeNull();
        NacosPrompt result = decoded.PromptInfo!;

        result.ShouldSatisfyAllConditions(
            value => value.PromptKey.ShouldBe("greeting"),
            value => value.Version.ShouldBe("2.1.0"),
            value => value.Template.ShouldBe("Hello {{name}}, welcome to {{place}}"),
            value => value.Md5.ShouldBe("0f1e2d"),
            value => value.Variables!.Count.ShouldBe(2),
            value => value.Variables![0].DefaultValue.ShouldBe("Nacos"),
            value => value.Variables![1].Description.ShouldBe("Where they arrived"),
            value => value.Variables![1].DefaultValue.ShouldBeNull());
    }

    [Fact]
    public void QueryPromptMessages_RouteByTheSimpleTypeNamesTheServerDispatchesOn()
    {
        // Nacos routes on metadata.type, which is the simple Java class name. These are new payloads,
        // so nothing else would catch a name that does not match the server's handler.
        PayloadTypeRegistry.GetTypeName(new QueryPromptRequest()).ShouldBe("QueryPromptRequest");
        PayloadTypeRegistry.GetTypeName(new QueryPromptResponse()).ShouldBe("QueryPromptResponse");
        PayloadTypeRegistry.Resolve("QueryPromptRequest").ShouldNotBeNull();
        PayloadTypeRegistry.Resolve("QueryPromptResponse").ShouldNotBeNull();
    }

    private static string Encode(NacosRequest request) =>
        Encoding.UTF8.GetString(PayloadCodec.ReadBodyBytes(PayloadCodec.Encode(request, null)));

    private static string Encode(NacosResponse response) =>
        Encoding.UTF8.GetString(PayloadCodec.ReadBodyBytes(PayloadCodec.Encode(response)));

    private static JsonNode PromptInfo(string json) => JsonNode.Parse(json)!["promptInfo"]!;
}
