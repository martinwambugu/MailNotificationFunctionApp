using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Abstractions;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi.Models;
using System;
using System.Collections.Generic;

namespace MailNotificationFunctionApp.Infrastructure
{
    /// <summary>  
    /// Custom OpenAPI configuration for Azure Functions Worker v2.  
    /// Implements IOpenApiConfigurationOptions for dynamic metadata.  
    /// </summary>  
    public class OpenApiConfig : IOpenApiConfigurationOptions
    {
        private readonly IConfiguration _configuration;

        public OpenApiConfig(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            // Pull values from configuration  
            var apiTitle = configuration["OpenApi:Title"] ?? "FA Function for LOS API";
            var apiVersion = configuration["OpenApi:Version"] ?? "1.0.0";
            var apiDescription = configuration["OpenApi:Description"] ?? "API documentation for FA Function";
            var contactName = configuration["OpenApi:ContactName"] ?? "Creodata Solutions Ltd";
            var contactEmail = configuration["OpenApi:ContactEmail"] ?? "support@creodata.com";

            Info = new OpenApiInfo()
            {
                Title = apiTitle,
                Version = apiVersion,
                Description = apiDescription,
                Contact = new OpenApiContact()
                {
                    Name = contactName,
                    Email = contactEmail
                }
            };

#if DEBUG
            Servers = new List<OpenApiServer> 
            {
                new OpenApiServer { Url = "http://localhost:7149", Description = "Local Development" },
            };
#else
             Servers = new List<OpenApiServer> 
             {  
                new OpenApiServer { Url = "https://MailNotificationFunctionApp.azurewebsites.net", Description = "Azure Deployment" }
             };  
#endif

            // ✅ Ensure SecurityDocumentFilter is registered so security schemes are injected  
            DocumentFilters = new List<IDocumentFilter>
            {
                new SecurityDocumentFilter()
            };
        }

        public OpenApiInfo Info { get; set; }
        public OpenApiVersionType OpenApiVersion { get; set; } = OpenApiVersionType.V3;
        public bool IncludeRequestingHostName { get; set; } = true;
        public List<OpenApiServer> Servers { get; set; }

#if DEBUG
        public bool ForceHttp { get; set; } = true;
        public bool ForceHttps { get; set; } = false;
#else
    public bool ForceHttp { get; set; } = false;  
    public bool ForceHttps { get; set; } = true;  
#endif

        public List<IDocumentFilter> DocumentFilters { get; set; } = new List<IDocumentFilter>();

        /// <summary>  
        /// Post-process the generated OpenAPI document to inject security schemes.  
        /// This ensures the "Authorize" button appears in Swagger UI.  
        /// </summary>  
        public void PostProcess(OpenApiDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, OpenApiSecurityScheme>();

            // API Key Scheme  
            var apiKeyScheme = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = "x-api-key",
                In = ParameterLocation.Header,
                Description = "API Key authentication",
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKeyAuth"
                }
            };
            document.Components.SecuritySchemes["ApiKeyAuth"] = apiKeyScheme;

            // Bearer Scheme  
            var bearerScheme = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT Bearer authentication",
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "BearerAuth"
                }
            };
            document.Components.SecuritySchemes["BearerAuth"] = bearerScheme;

            // Root-level security requirements (applies to all operations)  
            document.SecurityRequirements ??= new List<OpenApiSecurityRequirement>();

            document.SecurityRequirements.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKeyAuth" }
                }] = new List<string>()
            });

            document.SecurityRequirements.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "BearerAuth" }
                }] = new List<string>()
            });
        }
    }
}