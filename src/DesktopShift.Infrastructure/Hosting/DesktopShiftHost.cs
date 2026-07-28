using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Infrastructure.Hosting;

public static class DesktopShiftHost
{
    public static HostApplicationBuilder CreateBuilder(string[]? args = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args ?? []);
        builder.Services.AddDesktopShiftFoundation();
        return builder;
    }

    public static IHost Create(
        Action<IServiceCollection>? configureServices = null,
        string[]? args = null)
    {
        HostApplicationBuilder builder = CreateBuilder(args);
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}
