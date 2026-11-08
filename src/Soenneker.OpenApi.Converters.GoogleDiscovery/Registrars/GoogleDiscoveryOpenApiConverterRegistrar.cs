using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.OpenApi.Converters.GoogleDiscovery.Abstract;

namespace Soenneker.OpenApi.Converters.GoogleDiscovery.Registrars;

/// <summary>
/// A converter for Google Discovery documents to OpenAPI specifications.
/// </summary>
public static class GoogleDiscoveryOpenApiConverterRegistrar
{
    /// <summary>
    /// Adds <see cref="IGoogleDiscoveryOpenApiConverter"/> as a singleton service. <para/>
    /// </summary>
    public static IServiceCollection AddGoogleDiscoveryOpenApiConverterAsSingleton(this IServiceCollection services)
    {
        services.TryAddSingleton<IGoogleDiscoveryOpenApiConverter, GoogleDiscoveryOpenApiConverter>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="IGoogleDiscoveryOpenApiConverter"/> as a scoped service. <para/>
    /// </summary>
    public static IServiceCollection AddGoogleDiscoveryOpenApiConverterAsScoped(this IServiceCollection services)
    {
        services.TryAddScoped<IGoogleDiscoveryOpenApiConverter, GoogleDiscoveryOpenApiConverter>();

        return services;
    }
}
