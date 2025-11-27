using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Abstractions;
using Microsoft.OpenApi.Models;
using System;

namespace MailNotificationFunctionApp.Infrastructure
{
    public class SecurityDocumentFilter : IDocumentFilter
    {
        public void Apply(IHttpRequestDataObject req, OpenApiDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            // Inject your security definitions  
            SwaggerAuthDefinitions.AddSecurityDefinitions(document);
        }
    }
}