using DesktopNMS.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DashyNMS.Agent.Tests;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void Agent_registers_core_clients_as_singletons()
    {
        // The host supplies logging; Core's clients take an ILogger<T>.
        using var provider = new ServiceCollection().AddLogging().AddDashyNmsAgent().BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<LibreNmsClient>(), provider.GetRequiredService<ILibreNmsClient>());
        Assert.Same(provider.GetRequiredService<LibreNmsTransport>(), provider.GetRequiredService<ILibreNmsTransport>());
        Assert.NotNull(provider.GetRequiredService<IUnimusApi>());
        Assert.NotNull(provider.GetRequiredService<IGraylogApi>());
    }

    [Fact]
    public void Agent_does_not_register_desktop_settings_store()
    {
        // The settings store writes under ApplicationData; a server must not.
        var services = new ServiceCollection().AddDashyNmsAgent();

        Assert.DoesNotContain(services, d => d.ServiceType.Name == "ISettingsStore");
    }
}
