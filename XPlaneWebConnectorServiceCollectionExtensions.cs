using Microsoft.Extensions.Configuration;
using nocscienceat.XPlaneWebConnector;
using nocscienceat.XPlaneWebConnector.Interfaces;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering <see cref="XPlaneWebConnector"/> in an
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class XPlaneWebConnectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IXPlaneWebConnector"/> and its dependencies.
    /// Reads <see cref="XPlaneWebConnectorSettings"/> from the given configuration section
    /// (default <c>"XPlane"</c>), configures a named <see cref="HttpClient"/> with a
    /// <see cref="SocketsHttpHandler"/>, and registers the connector as a singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The root <see cref="IConfiguration"/>.</param>
    /// <param name="sectionName">
    /// Configuration section name to bind <see cref="XPlaneWebConnectorSettings"/> from.
    /// Default: <c>"XPlane"</c>.
    /// </param>
    /// <param name="maxConnectionsPerServer">
    /// Maximum concurrent connections the <see cref="SocketsHttpHandler"/> opens per server.
    /// Default: <c>30</c>.
    /// </param>
    /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddXPlaneWebConnector(this IServiceCollection services,
        IConfiguration configuration,  string sectionName = "XPlane", int maxConnectionsPerServer = 30)
    {
        var settings = configuration.GetSection(sectionName).Get<XPlaneWebConnectorSettings>() ?? new XPlaneWebConnectorSettings();

        services.AddHttpClient(settings.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            MaxConnectionsPerServer = maxConnectionsPerServer
        });

        services.AddSingleton<IXPlaneWebConnectorSettings>(settings);
        services.AddSingleton<IXPlaneWebConnector, XPlaneWebConnector>();

        return services;
    }
}
