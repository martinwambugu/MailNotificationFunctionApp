using Microsoft.OpenApi.Models;
using System;
using System.Collections.Generic;

namespace MailNotificationFunctionApp.Infrastructure
{
    public static class SwaggerAuthDefinitions
    {
        /// <summary>  
        /// Adds authentication schemes to the OpenAPI document based on your needs.  
        /// Supports both API key and Azure AD JWT Bearer authentication.  
        /// </summary>  
        public static void AddSecurityDefinitions(OpenApiDocument document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            // Add API Key auth if not already present  
            if (!document.Components.SecuritySchemes.ContainsKey("ApiKeyAuth"))
            {
                var apiKeyScheme = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    Name = "x-api-key",
                    In = ParameterLocation.Header,
                    Description = "API Key authentication using the `x-api-key` header",
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "ApiKeyAuth"
                    }
                };
                document.Components.SecuritySchemes["ApiKeyAuth"] = apiKeyScheme;
            }

            // Add Bearer auth if not already present  
            if (!document.Components.SecuritySchemes.ContainsKey("BearerAuth"))
            {
                var bearerScheme = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Azure AD JWT Bearer authentication",
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "BearerAuth"
                    }
                };
                document.Components.SecuritySchemes["BearerAuth"] = bearerScheme;
            }

            // Ensure security requirements reference both schemes  
            var hasApiKeyReq = document.SecurityRequirements
                .Any(req => req.Keys.Any(k => k.Reference?.Id == "ApiKeyAuth"));
            if (!hasApiKeyReq)
            {
                document.SecurityRequirements.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKeyAuth" }
                    }] = new List<string>()
                });
            }

            var hasBearerReq = document.SecurityRequirements
                .Any(req => req.Keys.Any(k => k.Reference?.Id == "BearerAuth"));
            if (!hasBearerReq)
            {
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
}