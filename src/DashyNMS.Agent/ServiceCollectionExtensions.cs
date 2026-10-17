using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Core's LibreNMS, Unimus and Graylog clients for the tools.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>AddDesktopNmsCore()</c>: that also registers the
    /// settings store, notification state and update checks, which write
    /// under the user's ApplicationData (AppPaths) and mean nothing to a
    /// server. Connections are built from environment variables instead, so
    /// no token protector is needed either.
    /// </remarks>
    public static IServiceCollection AddDashyNmsAgent(this IServiceCollection services)
    {
        services.AddSingleton<ServerFailover>();
        services.AddSingleton<LibreNmsTransport>();
        services.AddSingleton<ILibreNmsTransport>(sp => sp.GetRequiredService<LibreNmsTransport>());
        services.AddSingleton<LibreNmsClient>();
        services.AddSingleton<ILibreNmsClient>(sp => sp.GetRequiredService<LibreNmsClient>());

        // Optional integrations: their tools say "not configured" until the
        // matching environment variables are set.
        services.AddSingleton<UnimusApi>();
        services.AddSingleton<IUnimusApi>(sp => sp.GetRequiredService<UnimusApi>());
        services.AddSingleton<GraylogApi>();
        services.AddSingleton<IGraylogApi>(sp => sp.GetRequiredService<GraylogApi>());

        return services;
    }
}
