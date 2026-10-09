using CareFlow.Parser.Application.Abstractions;
using CareFlow.Parser.Application.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CareFlow.Parser.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddParserApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ParserOptions>()
            .Bind(configuration.GetSection(ParserOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFileParser, X12FileParser>();
        services.AddScoped<ParseFileHandler>();
        return services;
    }
}
