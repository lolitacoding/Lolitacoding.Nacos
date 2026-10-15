using Lolitacoding.Nacos;
using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Config;

// Minimal Native AOT proof for the core client. Everything here is reflection-free: the options
// are set in code rather than bound from configuration, and the only JSON parsing happens inside
// the library through its source-generated contracts.
//
// Publish with: dotnet publish samples/Sample.Aot -c Release -r win-x64
//
// The server is optional, so this runs to completion even with no Nacos server listening; it
// simply reports that the value is unavailable.

string address = args.Length > 0 ? args[0] : "http://127.0.0.1:8848";

var options = new NacosOptions
{
    ServerAddresses = [address],
    Namespace = NacosConstants.DefaultNamespace,
    AppName = "sample-aot",
    // Tolerate an unavailable server so the sample can be run without Nacos.
    Optional = true,
};

await using NacosClient client = NacosClient.Create(options);
await client.ConnectAsync();

NacosConfigResult result = await client.Config.GetConfigAsync(
    new NacosConfigKey("sample.json", NacosConstants.DefaultGroup));

Console.WriteLine(result.Exists
    ? $"Read '{result.Key}': {result.Content}"
    : $"'{result.Key}' is not available (it may not exist, or no server was reachable).");
