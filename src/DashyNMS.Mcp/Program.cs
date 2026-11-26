using DashyNMS.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The environment only, not the host's whole configuration: that also reads
// appsettings.json from the working directory and the command line, so a
// file someone left there could point the server at another LibreNMS.
var options = McpOptions.TryLoad(Environment.GetEnvironmentVariable, out var errors);
if (options is null)
{
    foreach (var error in errors)
    {
        Console.Error.WriteLine(error);
    }

    return 2;
}

foreach (var warning in options.Warnings)
{
    Console.Error.WriteLine("Warning: " + warning);
}

#if MCP_HTTP
if (options.Transport == McpTransport.Http)
{
    return await DashyNMS.Mcp.Http.HttpServer.RunAsync(options);
}
#endif

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddDashyNmsLogging();
builder.Services.AddDashyNmsServer(options).WithStdioServerTransport();

using var host = builder.Build();
if (await ServerSetup.ConnectAsync(host.Services, options) is { } failure)
{
    Console.Error.WriteLine(failure);
    return 1;
}

await host.RunAsync();
return 0;
