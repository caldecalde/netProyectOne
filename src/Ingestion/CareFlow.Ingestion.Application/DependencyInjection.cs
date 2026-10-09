using CareFlow.Ingestion.Application.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CareFlow.Ingestion.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddIngestionApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SubmitFileHandler>();
        services.AddScoped<GetFileHandler>();
        return services;
    }
}
