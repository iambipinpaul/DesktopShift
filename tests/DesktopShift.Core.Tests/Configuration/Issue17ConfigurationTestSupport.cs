using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopShift.Core.Configuration;
using DesktopShift.Infrastructure.Configuration;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopShift.Core.Tests.Configuration;

internal static class Issue17ConfigurationTestSupport
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static ServiceProvider CreateProvider(
        TestConfigurationDirectory storage,
        string userProfile = @"C:\Users\Alice",
        ConfigurationSchema? schema = null,
        TimeProvider? timeProvider = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfigurationStoragePath>(storage);
        services.AddSingleton<IUserProfilePath>(new StubUserProfilePath(userProfile));
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        if (schema is not null)
        {
            services.AddSingleton(schema);
        }

        services.AddDesktopShiftFoundation();
        services.AddDesktopShiftConfigurationExchange();
        return services.BuildServiceProvider();
    }

    public static string Serialize(ConfigurationDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);

    public static MemoryStream Stream(string value) =>
        new(System.Text.Encoding.UTF8.GetBytes(value));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal sealed class TestConfigurationDirectory :
    IConfigurationStoragePath,
    IDisposable
{
    public TestConfigurationDirectory()
    {
        DirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "DesktopShift.Tests",
            Guid.NewGuid().ToString("N"));
    }

    public string DirectoryPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}

internal sealed record StubUserProfilePath(string DirectoryPath) : IUserProfilePath;

internal sealed class StubConfigurationMigration(
    int fromVersion,
    int toVersion,
    string description,
    Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonObject> apply)
    : IConfigurationMigration
{
    public int FromVersion { get; } = fromVersion;

    public int ToVersion { get; } = toVersion;

    public string Description { get; } = description;

    public System.Text.Json.Nodes.JsonObject Apply(
        System.Text.Json.Nodes.JsonObject document) =>
        apply(document);
}

internal sealed class Issue17FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
