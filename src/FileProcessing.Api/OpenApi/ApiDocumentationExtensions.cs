using System.Reflection;
using FileProcessing.Api.Authentication;
using Microsoft.OpenApi;

namespace FileProcessing.Api.OpenApi;

/// <summary>
/// Swagger/OpenAPI wiring. The API key scheme is declared so the generated document is accurate
/// and Swagger UI can send the header, which makes the service explorable without a REST client.
/// </summary>
public static class ApiDocumentationExtensions
{
    private const string SecuritySchemeId = "ApiKey";

    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "File Processing API",
                Version = "v1",
                Description =
                    "Processes uploaded transaction files, tracks every file it has seen and reports "
                    + "on that activity. Every endpoint requires an API key in the "
                    + "X-Api-Key header.",
            });

            options.AddSecurityDefinition(SecuritySchemeId, new OpenApiSecurityScheme
            {
                Name = "X-Api-Key",
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Description =
                    "API key issued to the calling system. Scopes carried by the key decide which "
                    + "endpoints it can reach.",
            });

            // Applied to every operation: the fallback authorization policy means there are no
            // anonymous endpoints in the document, so declaring it per-operation would be noise.
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SecuritySchemeId, document)] = new List<string>(),
            });

            // Surfaces the XML docs written against the controllers and contracts in the UI.
            foreach (var assembly in new[] { Assembly.GetExecutingAssembly(), typeof(ApiScopes).Assembly })
            {
                var xml = Path.Combine(AppContext.BaseDirectory, $"{assembly.GetName().Name}.xml");
                if (File.Exists(xml))
                {
                    options.IncludeXmlComments(xml);
                }
            }
        });

        return services;
    }

    /// <summary>
    /// Serves the document and UI outside production. The schema of an API is not a secret, but
    /// there is no reason to hand a production attacker a complete endpoint inventory either.
    /// </summary>
    public static WebApplication UseApiDocumentation(this WebApplication app, IWebHostEnvironment environment)
    {
        if (environment.IsProduction())
        {
            return app;
        }

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "File Processing API v1");
            options.DocumentTitle = "File Processing API";
        });

        return app;
    }
}
