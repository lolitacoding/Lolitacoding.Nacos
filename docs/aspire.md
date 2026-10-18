# Aspire orchestration

[.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) orchestrates a distributed application
from an _AppHost_ project. These two packages let an AppHost run a Nacos server for local development
and hand its address to the services that need it.

| Package | Install it in | Use it for |
| --- | --- | --- |
| `Lolitacoding.Aspire.Hosting.Nacos` | The AppHost project | `AddNacos(...)` adds a Nacos server container to the application model. |
| `Lolitacoding.Aspire.Nacos` | Each service that talks to Nacos | `AddNacosClient(...)` reads the injected connection string. |

```bash
# In the AppHost project.
dotnet add package Lolitacoding.Aspire.Hosting.Nacos

# In each service project.
dotnet add package Lolitacoding.Aspire.Nacos
```

## 1. Add the server to the AppHost

```csharp
using Lolitacoding.Aspire.Hosting.Nacos;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Standalone Nacos with authentication disabled and data kept in a named volume.
IResourceBuilder<NacosResource> nacos = builder.AddNacos("nacos")
    .WithDataVolume();

builder.AddProject<Projects.MyApi>("my-api")
    .WithReference(nacos)   // injects ConnectionStrings__nacos
    .WaitFor(nacos);        // starts only once Nacos reports ready

builder.Build().Run();
```

The resource name (`"nacos"`) is also the connection string name. `WithReference` injects
`ConnectionStrings__nacos` into the referenced project, and `WaitFor` delays the project until the
readiness health check succeeds.

## 2. Consume the connection string

```csharp
using Lolitacoding.Aspire.Nacos;

builder.AddNacosClient(options => options.Optional = true);
```

`AddNacosClient` reads `ConnectionStrings:nacos`, applies it to `NacosOptions.ServerAddresses`, then
runs the optional delegate so it can override any value:

```csharp
builder.AddNacosClient(
    connectionName: "nacos",
    configure: options =>
    {
        options.Namespace = "public";
        options.Optional = true;
    });
```

Pass a different `connectionName` when the AppHost adds the resource under another name. The method
throws `InvalidOperationException` at startup when the connection string is missing, so a forgotten
`WithReference` fails immediately rather than at the first request.

`AddNacosClient` registers the shared client and every feature client, enough to read configuration and
discover services in code. It does not install Nacos as an `IConfiguration` source or register the
instance automatically — add those separately, reusing the same address:

```csharp
using Lolitacoding.Nacos.AspNetCore;
using Lolitacoding.Nacos.Extensions.Configuration;

string serverAddress = builder.Configuration.GetConnectionString("nacos")!;

builder.AddNacosClient();
builder.Configuration.AddNacosConfiguration(options =>
{
    options.ServerAddresses = [serverAddress];
    options.DataIds = ["my-api.json"];
    options.Optional = true;
});
builder.Services.AddNacosServiceRegistration(options => options.ServiceName = "my-api");
```

## 3. Configure the server

`AddNacos` returns an `IResourceBuilder<NacosResource>`, so the standard Aspire container methods
apply. This package adds Nacos-specific helpers on top:

| Method | Effect |
| --- | --- |
| `WithDataVolume(name)` | Persists `/home/nacos/data` in a named volume (auto-named when omitted). |
| `WithDataBindMount(source)` | Persists `/home/nacos/data` in a host directory. |
| `WithAuthentication()` | Sets `NACOS_AUTH_ENABLE=true`. No account exists yet: create the administrator once with `POST /nacos/v3/auth/user/admin?password=…` on the container's main port, as described in the [FAQ](faq.md#how-do-i-authenticate). |

```csharp
builder.AddNacos("nacos")
    .WithDataVolume("nacos-data")
    .WithAuthentication();
```

The image defaults to `docker.io/nacos/nacos-server:latest`. Pin a version with the standard Aspire
methods:

```csharp
builder.AddNacos("nacos").WithImageTag("v3.2.4");
```

The container image requires a signing key and a server identity from 3.2 on, whether or not
authentication is enabled, and exits without them. `AddNacos` therefore always sets development
values for `NACOS_AUTH_TOKEN`, `NACOS_AUTH_IDENTITY_KEY` and `NACOS_AUTH_IDENTITY_VALUE`; override
them with the standard `WithEnvironment` when the deployment is not a local one.

The distributed lock is the one module that needs more than the newest stable release: its renewal,
wait queue and reentrancy belong to the 3.3 lock service. See
[the FAQ](faq.md#which-nacos-versions-are-supported).

## 4. Ports

`AddNacos` publishes three fixed host ports so that a Nacos client can reach the server:

| Endpoint | Container port | Default host port |
| --- | --- | --- |
| `http` | 8848 | 8848 |
| `grpc` | 9848 | 9848 |
| `grpc-server` | 9849 | 9849 |

The ports are not random because a Nacos client derives the gRPC port from the HTTP address it was
given: it adds `1000` to the main port. If the HTTP endpoint were mapped to an arbitrary port, the
derived gRPC port would not match the published one and clients could not connect. `AddNacos`
therefore keeps the offsets in place and lets you move the whole set with `httpPort`:

```csharp
// 18848 / 19848 / 19849.
builder.AddNacos("nacos", httpPort: 18848);
```

Override `grpcPort` and `grpcServerPort` independently only if something else manages the client's
port derivation.

In Nacos 3.x the web console runs on its own port (`nacos.console.port`, default `8080`) inside the
container. `AddNacos` publishes only the server endpoints, so expose the console explicitly if you
need it:

```csharp
builder.AddNacos("nacos").WithHttpEndpoint(port: 8080, targetPort: 8080, name: "console");
```

## 5. Run the sample

`samples/Sample.Aspire.AppHost` orchestrates the server together with `samples/Sample.AspNetCore`:

```bash
dotnet run --project samples/Sample.Aspire.AppHost
```

The AppHost starts Nacos, waits for it to become ready, then starts the API and passes it the Nacos
address. See `Program.cs` in both projects for the full wiring.
