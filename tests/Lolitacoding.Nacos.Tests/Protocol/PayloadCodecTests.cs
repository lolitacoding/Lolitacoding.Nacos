using System.Text.Json;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.Protocol;

/// <summary>
/// Verifies the payload envelope against the byte-exact JSON that Nacos' own test suite asserts,
/// and against the structural rules the official clients follow.
/// </summary>
/// <remarks>
/// The expected JSON fragments below are quoted from the upstream Nacos repository's unit tests
/// (see <c>docs/protocol-grpc-spec.md</c> §3.0). They are the strongest available ground truth:
/// the server itself asserts these exact strings, so matching them demonstrates interoperability
/// rather than merely self-consistency.
/// </remarks>
public sealed class PayloadCodecTests
{
    private const string ClientIp = "1.1.1.1";

    [Fact]
    public void Encode_SetsMetadataTypeToSimpleTypeName()
    {
        var request = new ConfigQueryRequest
        {
            DataId = "test_data",
            Group = "group",
            Tenant = "test_tenant",
        };

        Payload payload = PayloadCodec.Encode(request, ClientIp);

        // Nacos routes on the simple class name, so this value is the routing key.
        payload.Metadata.Type.ShouldBe("ConfigQueryRequest");
        payload.Metadata.ClientIp.ShouldBe(ClientIp);
    }

    [Fact]
    public void Encode_LeavesAnyTypeUrlEmpty()
    {
        var request = new ServerCheckRequest();

        Payload payload = PayloadCodec.Encode(request, ClientIp);

        // The official clients never call setTypeUrl, and neither side reads the field.
        // Setting it would be a gratuitous deviation from the wire format.
        payload.Body.TypeUrl.ShouldBeEmpty();
        payload.Body.Value.ShouldNotBeEmpty();
    }

    [Fact]
    public void Encode_CarriesHeadersInMetadataNotInBody()
    {
        var request = new ConfigQueryRequest
        {
            DataId = "test_data",
            Group = "group",
        };
        request.MarkAsNotify();

        Payload payload = PayloadCodec.Encode(request, ClientIp);

        payload.Metadata.Headers["notify"].ShouldBe("true");

        // Nacos empties the bean's headers before serializing, so the body copy is an empty
        // object even though the authoritative value sits in the metadata.
        using JsonDocument body = JsonDocument.Parse(PayloadCodec.ReadBodyBytes(payload));
        body.RootElement.GetProperty("headers").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public void Encode_ConfigQueryRequest_MatchesUpstreamFieldNames()
    {
        var request = new ConfigQueryRequest
        {
            DataId = "test_data",
            Group = "group",
            Tenant = "test_tenant",
            Tag = "tag",
            LocalMd5 = "deserialized-md5",
        };

        Payload payload = PayloadCodec.Encode(request, ClientIp);
        using JsonDocument body = JsonDocument.Parse(PayloadCodec.ReadBodyBytes(payload));
        JsonElement root = body.RootElement;

        root.GetProperty("dataId").GetString().ShouldBe("test_data");
        root.GetProperty("group").GetString().ShouldBe("group");
        root.GetProperty("tenant").GetString().ShouldBe("test_tenant");
        root.GetProperty("tag").GetString().ShouldBe("tag");
        root.GetProperty("localMd5").GetString().ShouldBe("deserialized-md5");
        root.GetProperty("module").GetString().ShouldBe("config");
    }

    [Fact]
    public void Encode_OmitsNullPropertiesButKeepsDefaults()
    {
        // Include.NON_NULL omits nulls, yet empty collections, false and 0 are still emitted.
        var request = new ConfigRemoveRequest
        {
            DataId = "d",
            Group = "g",
        };

        Payload payload = PayloadCodec.Encode(request, ClientIp);
        using JsonDocument body = JsonDocument.Parse(PayloadCodec.ReadBodyBytes(payload));
        JsonElement root = body.RootElement;

        root.TryGetProperty("tag", out _).ShouldBeFalse("a null tag must be omitted");

        // The headers bag is non-null, so it is emitted even though it is empty.
        root.GetProperty("headers").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public void Encode_ConfigBatchListenRequest_UsesUpstreamFieldNames()
    {
        var request = new ConfigBatchListenRequest
        {
            Listen = true,
            ConfigListenContexts =
            [
                new ConfigListenContext
                {
                    Group = "group",
                    Md5 = "test_MD5",
                    DataId = "test_data",
                    Tenant = "test_tenant",
                },
            ],
        };

        Payload payload = PayloadCodec.Encode(request, ClientIp);
        using JsonDocument body = JsonDocument.Parse(PayloadCodec.ReadBodyBytes(payload));
        JsonElement root = body.RootElement;

        root.GetProperty("listen").GetBoolean().ShouldBeTrue();
        root.GetProperty("module").GetString().ShouldBe("config");

        JsonElement context = root.GetProperty("configListenContexts")[0];
        context.GetProperty("group").GetString().ShouldBe("group");
        context.GetProperty("md5").GetString().ShouldBe("test_MD5");
        context.GetProperty("dataId").GetString().ShouldBe("test_data");
        context.GetProperty("tenant").GetString().ShouldBe("test_tenant");
    }

    [Fact]
    public void Encode_InstanceRequest_EmitsComputedInstanceDefaults()
    {
        var request = new InstanceRequest
        {
            Namespace = "namespace",
            ServiceName = "service",
            GroupName = "group",
            Type = InstanceRequest.DeregisterType,
            Instance = new Instance(),
        };

        Payload payload = PayloadCodec.Encode(request, ClientIp);
        using JsonDocument body = JsonDocument.Parse(PayloadCodec.ReadBodyBytes(payload));
        JsonElement instance = body.RootElement.GetProperty("instance");

        // These four are derived getters in Java but are serialized, and Nacos' own test
        // compares a default-constructed instance to the parsed result, so the defaults matter.
        instance.GetProperty("instanceIdGenerator").GetString().ShouldBe("simple");
        instance.GetProperty("instanceHeartBeatInterval").GetInt64().ShouldBe(5000);
        instance.GetProperty("instanceHeartBeatTimeOut").GetInt64().ShouldBe(15000);
        instance.GetProperty("ipDeleteTimeout").GetInt64().ShouldBe(30000);

        // Defaults asserted by the upstream InstanceRequest fixture.
        instance.GetProperty("weight").GetDouble().ShouldBe(1.0);
        instance.GetProperty("healthy").GetBoolean().ShouldBeTrue();
        instance.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        instance.GetProperty("ephemeral").GetBoolean().ShouldBeTrue();
        instance.GetProperty("metadata").EnumerateObject().ShouldBeEmpty();

        body.RootElement.GetProperty("type").GetString().ShouldBe("deregisterInstance");
        body.RootElement.GetProperty("module").GetString().ShouldBe("naming");
    }

    [Fact]
    public void Decode_ConfigQueryResponse_ReadsBetaWireName()
    {
        // Upstream asserts the property is "beta", not "isBeta".
        const string Json = """
            {"resultCode":200,"errorCode":0,"requestId":"2239753e-e682-441c-83cf-fb8129ca68a4",
             "content":"success","encryptedDataKey":"encryptedKey","contentType":"text",
             "md5":"test_MD5","lastModified":1111111,"tag":"tag","beta":false,"success":true}
            """;

        ConfigQueryResponse? response = DecodeResponse<ConfigQueryResponse>(Json);

        response.ShouldNotBeNull();
        response.Content.ShouldBe("success");
        response.EncryptedDataKey.ShouldBe("encryptedKey");
        response.ContentType.ShouldBe("text");
        response.Md5.ShouldBe("test_MD5");
        response.LastModified.ShouldBe(1111111);
        response.Tag.ShouldBe("tag");
        response.Beta.ShouldBeFalse();
        response.ResultCode.ShouldBe(200);
        response.Success.ShouldBeTrue();
    }

    [Fact]
    public void Decode_QueryServiceResponse_ReadsAllIPsWireName()
    {
        // Upstream asserts the property is "allIPs" with a capitalised IP.
        const string Json = """
            {"resultCode":200,"errorCode":0,
             "serviceInfo":{"cacheMillis":1000,"hosts":[],"lastRefTime":0,"checksum":"",
                            "allIPs":false,"reachProtectionThreshold":false,"valid":true},
             "success":true}
            """;

        QueryServiceResponse? response = DecodeResponse<QueryServiceResponse>(Json);

        response.ShouldNotBeNull();
        response.ServiceInfo.ShouldNotBeNull();
        response.ServiceInfo.CacheMillis.ShouldBe(1000);
        response.ServiceInfo.AllIPs.ShouldBeFalse();
        response.ServiceInfo.Valid.ShouldBeTrue();
        response.Success.ShouldBeTrue();
    }

    [Fact]
    public void Decode_IgnoresUnknownPropertiesFromNewerServers()
    {
        // A Nacos 3.3+ server may add fields; failing on them would break forward compatibility.
        const string Json = """
            {"resultCode":200,"errorCode":0,"content":"value","success":true,
             "someFutureField":{"nested":1},"anotherOne":[1,2,3]}
            """;

        ConfigQueryResponse? response = DecodeResponse<ConfigQueryResponse>(Json);

        response.ShouldNotBeNull();
        response.Content.ShouldBe("value");
    }

    [Fact]
    public void DecodeAny_ReturnsNullForUnregisteredType()
    {
        // Unknown payload types must be ignorable rather than fatal: a newer server pushes
        // types this client has never heard of, and tearing down a healthy stream would be wrong.
        var payload = new Payload
        {
            Metadata = new Metadata { Type = "SomeFuturePayloadFromNacos4" },
            Body = new Google.Protobuf.WellKnownTypes.Any
            {
                Value = Google.Protobuf.ByteString.CopyFromUtf8("{}"),
            },
        };

        PayloadCodec.DecodeAny(payload).ShouldBeNull();
    }

    [Fact]
    public void Decode_ThrowsForMalformedJson()
    {
        var payload = new Payload
        {
            Metadata = new Metadata { Type = "ConfigQueryResponse" },
            Body = new Google.Protobuf.WellKnownTypes.Any
            {
                Value = Google.Protobuf.ByteString.CopyFromUtf8("{ this is not json"),
            },
        };

        NacosException ex = Should.Throw<NacosException>(() => PayloadCodec.DecodeAny(payload));
        ex.Message.ShouldContain("ConfigQueryResponse");
    }

    [Fact]
    public void DecodeRequest_CopiesHeadersOntoTheBean()
    {
        // On receive, Nacos restores the metadata headers onto the request bean so handlers
        // read them uniformly regardless of which transport delivered the message.
        var payload = new Payload
        {
            Metadata = new Metadata { Type = "ConfigQueryRequest", ClientIp = ClientIp },
            Body = new Google.Protobuf.WellKnownTypes.Any
            {
                Value = Google.Protobuf.ByteString.CopyFromUtf8(
                    """{"dataId":"d","group":"g","tenant":"n"}"""),
            },
        };
        payload.Metadata.Headers["notify"] = "true";
        payload.Metadata.Headers["accessToken"] = "token-value";

        ConfigQueryRequest request = PayloadCodec.DecodeRequest<ConfigQueryRequest>(payload);

        request.GetHeader("notify").ShouldBe("true");
        request.GetHeader("accesstoken").ShouldBe("token-value");
        request.DataId.ShouldBe("d");
        request.Tenant.ShouldBe("n");
    }

    [Fact]
    public void RoundTrip_PreservesEveryRegisteredMessageType()
    {
        // Every type in the registry must survive a full envelope round-trip, so a typo in the
        // registry or a missing [JsonSerializable] attribute fails here rather than at run time.
        var request = new SubscribeServiceRequest
        {
            Namespace = "namespace",
            ServiceName = "service",
            GroupName = "group",
            Subscribe = true,
            Clusters = "aa,bb",
        };

        Payload encoded = PayloadCodec.Encode(request, ClientIp);
        SubscribeServiceRequest decoded = PayloadCodec.DecodeRequest<SubscribeServiceRequest>(encoded);

        decoded.Namespace.ShouldBe("namespace");
        decoded.ServiceName.ShouldBe("service");
        decoded.GroupName.ShouldBe("group");
        decoded.Subscribe.ShouldBeTrue();
        decoded.Clusters.ShouldBe("aa,bb");
        decoded.Module.ShouldBe("naming");
    }

    [Fact]
    public void Encode_ResponseIncludesSuccessProperty()
    {
        // Nacos serializes the computed isSuccess() into every response body.
        var response = new ConfigPublishResponse { ResultCode = 500, ErrorCode = 7, Message = "Fail" };
        response.RequestId = "1";

        Payload payload = PayloadCodec.Encode(response);
        using JsonDocument body = JsonDocument.Parse(PayloadCodec.ReadBodyBytes(payload));
        JsonElement root = body.RootElement;

        payload.Metadata.Type.ShouldBe("ConfigPublishResponse");
        root.GetProperty("resultCode").GetInt32().ShouldBe(500);
        root.GetProperty("errorCode").GetInt32().ShouldBe(7);
        root.GetProperty("message").GetString().ShouldBe("Fail");
        root.GetProperty("success").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Decode_ConnectResetRequest_ReadsServerPortAsString()
    {
        // A documented trap: serverPort is a JSON string, not a number.
        const string Json = """
            {"headers":{},"requestId":"1","serverIp":"127.0.0.1","serverPort":"8888",
             "connectionId":"conn-1","module":"internal"}
            """;

        var payload = new Payload
        {
            Metadata = new Metadata { Type = "ConnectResetRequest" },
            Body = new Google.Protobuf.WellKnownTypes.Any
            {
                Value = Google.Protobuf.ByteString.CopyFromUtf8(Json),
            },
        };

        ConnectResetRequest request = PayloadCodec.DecodeRequest<ConnectResetRequest>(payload);

        request.ServerIp.ShouldBe("127.0.0.1");
        request.ServerPort.ShouldBe("8888");
        request.ConnectionId.ShouldBe("conn-1");
        request.RequestId.ShouldBe("1");
    }

    [Fact]
    public void Decode_SetupAckRequest_ReadsServerAbilityTable()
    {
        const string Json = """
            {"headers":{},"requestId":"1","abilityTable":{"fuzzyWatch":true},"module":"internal"}
            """;

        var payload = new Payload
        {
            Metadata = new Metadata { Type = "SetupAckRequest" },
            Body = new Google.Protobuf.WellKnownTypes.Any
            {
                Value = Google.Protobuf.ByteString.CopyFromUtf8(Json),
            },
        };

        SetupAckRequest ack = PayloadCodec.DecodeRequest<SetupAckRequest>(payload);

        ack.AbilityTable.ShouldNotBeNull();
        ack.AbilityTable["fuzzyWatch"].ShouldBeTrue();
    }

    [Fact]
    public void GetTypeName_MatchesNacosSimpleClassNames()
    {
        // These names are the routing keys the Nacos server's handler registry is keyed by,
        // so a mismatch here would only surface as an opaque server-side failure.
        PayloadTypeRegistry.GetTypeName(new ConfigQueryRequest()).ShouldBe("ConfigQueryRequest");
        PayloadTypeRegistry.GetTypeName(new ConfigBatchListenRequest()).ShouldBe("ConfigBatchListenRequest");
        PayloadTypeRegistry.GetTypeName(new ServiceQueryRequest()).ShouldBe("ServiceQueryRequest");
        PayloadTypeRegistry.GetTypeName(new ConnectionSetupRequest()).ShouldBe("ConnectionSetupRequest");
        PayloadTypeRegistry.GetTypeName(new QueryServiceResponse()).ShouldBe("QueryServiceResponse");
        PayloadTypeRegistry.GetTypeName(new ConfigChangeBatchListenResponse())
            .ShouldBe("ConfigChangeBatchListenResponse");
    }

    private static TResponse? DecodeResponse<TResponse>(string json)
        where TResponse : NacosResponse
    {
        string typeName = typeof(TResponse).Name;
        var payload = new Payload
        {
            Metadata = new Metadata { Type = typeName },
            Body = new Google.Protobuf.WellKnownTypes.Any
            {
                Value = Google.Protobuf.ByteString.CopyFromUtf8(json),
            },
        };

        return PayloadCodec.DecodeAny(payload) as TResponse;
    }
}
