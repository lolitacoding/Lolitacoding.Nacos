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

// Aspire 17 adds a C# project by path; the generic AddProject<TProject> is obsolete.
builder.AddDotnetProject("my-api", "../MyApi/MyApi.csproj")
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

builder.AddNacosClient(configure: options => options.Optional = true);
```

`AddNacosClient` reads `ConnectionStrings:nacos`, applies it to `NacosOptions.ServerAddresses`, then
runs the optional delegate so it can override any value. It also reads the `NACOS_PORT` and
`NACOS_GRPCPORT` connection properties that the hosting resource injects and sets
`NacosOptions.GrpcPortOffset` to the difference between them, because Aspire publishes each
container port on its own host port. When those properties are absent the default offset of `1000`
is kept, so a plain connection string still works.

For example:

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

`AddNacos` does not pin host ports. Each endpoint is published on a free port that Aspire picks,
and the dashboard shows the address that was assigned. The ports inside the container are fixed:

| Endpoint | Container port | What it is |
| --- | --- | --- |
| `http` | 8848 | Server API; what a Nacos client connects to. |
| `grpc` | 9848 | Client gRPC (main port + 1000 inside the container). |
| `grpc-server` | 9849 | Server-to-server gRPC (main port + 1001). |
| `console` | 8080 | Web console. |

A Nacos client normally derives the gRPC port by adding `1000` to the main port, which would be
wrong once the two are published independently. The resource therefore also injects `NACOS_PORT`
and `NACOS_GRPCPORT`, and `AddNacosClient` sets `NacosOptions.GrpcPortOffset` to the difference
between them. Nothing else is needed when both packages are used together.

A client that reads only the connection string — a non-.NET process, or one that does not use
`Lolitacoding.Aspire.Nacos` — has to be told the offset. Either configure
`NacosOptions.GrpcPortOffset` explicitly, or pin the ports at a known distance. Every port
argument and `With*Port` helper pins exactly the endpoint it names, so pinning all three is
explicit:

```csharp
// 18848 / 19848 / 19849; the console still gets a free port.
builder.AddNacos(
    "nacos",
    httpPort: 18848,
    grpcPort: 19848,
    grpcServerPort: 19849);
```

Each port can also be set after `AddNacos`:

```csharp
builder.AddNacos("nacos")
    .WithHttpPort(18848)
    .WithGrpcPort(19848)
    .WithGrpcServerPort(19849)
    .WithConsolePort(18080);
```

`WithHttpPort` changes only the main port; the gRPC port stays wherever Aspire published it, so a
client that derives it must be given the offset. Use `WithGrpcPort` as well when the two need to be
a known distance apart.

The console is a separate application in Nacos 3.x, configured by `nacos.console.port` and listening
on `8080`; in 2.x it was part of the server and served under the main port's context path. Its host
port is independent of the API, so the dashboard's `console` link is the one that opens the UI. The
`http` link points at the API root, which answers 404 by design, so use it for API calls only.

## 5. Run the sample

`samples/Sample.Aspire.AppHost` orchestrates the server together with `samples/Sample.AspNetCore`:

```bash
dotnet run --project samples/Sample.Aspire.AppHost
```

The AppHost starts Nacos, waits for it to become ready, then starts the API and passes it the Nacos
address. Open the console from the dashboard's `console` link to inspect configurations and
services; the `http` link is the API root, which answers 404 by design. See `Program.cs` in both
projects for the full wiring.
