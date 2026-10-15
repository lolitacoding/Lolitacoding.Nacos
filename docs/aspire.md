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

`AddNacosClient` only registers the core clients. To also use Nacos as a configuration source or to
register the instance automatically, reuse the same address:

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
| `WithAuthentication()` | Sets `NACOS_AUTH_ENABLE=true`. The server's built-in `nacos`/`nacos` credentials still work. |

```csharp
builder.AddNacos("nacos")
    .WithDataVolume("nacos-data")
    .WithAuthentication();
```

The image defaults to `docker.io/nacos/nacos-server:latest`. Pin a version with the standard Aspire
methods:

```csharp
builder.AddNacos("nacos").WithImageTag("v2.5.1");
```

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

The Nacos console is served by the HTTP endpoint under `/nacos`, so it is available at
`http://localhost:8848/nacos` with the defaults.

## 5. Run the sample

`samples/Sample.Aspire.AppHost` orchestrates the server together with `samples/Sample.AspNetCore`:

```bash
dotnet run --project samples/Sample.Aspire.AppHost
```

The AppHost starts Nacos, waits for it to become ready, then starts the API and passes it the Nacos
address. See `Program.cs` in both projects for the full wiring.
