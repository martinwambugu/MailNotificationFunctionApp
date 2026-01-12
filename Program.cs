using Azure.Monitor.OpenTelemetry.Exporter;
using MailNotificationFunctionApp.Infrastructure;
using MailNotificationFunctionApp.Interfaces;
using MailNotificationFunctionApp.Middleware;
using MailNotificationFunctionApp.Models;
using MailNotificationFunctionApp.Services;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker.Extensions.OpenApi.Extensions;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System;
using System.IO;

// -----------------------------------------------------  
// Load Configuration  
// -----------------------------------------------------  
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

string telemetryBackend = configuration["TELEMETRY_BACKEND"]?.ToLowerInvariant() ?? "azure";

// -----------------------------------------------------  
// Build Host  
// -----------------------------------------------------  
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication((context, builder) =>
    {
        // -----------------------------------------------------  
        // CORS CONFIGURATION ✅  
        // -----------------------------------------------------  
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy
                    .AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });
        });

        // -----------------------------------------------------  
        // Logging Pipeline with OpenTelemetry  
        // -----------------------------------------------------  
        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.ParseStateValues = true;
                options.IncludeFormattedMessage = true;

                if (telemetryBackend == "onprem")
                {
                    options.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri(configuration["OTLP_ENDPOINT"] ?? "http://localhost:4317");
                    });
                }
                else if (telemetryBackend == "azure")
                {
                    options.AddAzureMonitorLogExporter(o =>
                    {
                        o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
                    });
                }
            });
        });

        // -----------------------------------------------------  
        // Metrics & Tracing with OpenTelemetry  
        // -----------------------------------------------------  
        var otel = builder.Services.AddOpenTelemetry();

        otel.WithMetrics(metrics =>
        {
            metrics
                .AddRuntimeInstrumentation()
                .AddHttpClientInstrumentation()
                .AddAspNetCoreInstrumentation();

            if (telemetryBackend == "onprem" &&
                bool.TryParse(configuration["PROMETHEUS_ENABLED"], out var prometheusEnabled) &&
                prometheusEnabled)
            {
                metrics.AddPrometheusHttpListener(options =>
                {
                    options.UriPrefixes = new string[] { "http://+:9464/" };
                });
            }
            else if (telemetryBackend == "azure")
            {
                metrics.AddAzureMonitorMetricExporter(o =>
                {
                    o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
                });
            }
        });

        otel.WithTracing(traces =>
        {
            traces
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .SetSampler(new AlwaysOnSampler());

            if (telemetryBackend == "onprem")
            {
                traces.AddOtlpExporter(otlp =>
                {
                    otlp.Endpoint = new Uri(configuration["OTLP_ENDPOINT"] ?? "http://localhost:4317");
                });
            }
            else if (telemetryBackend == "azure")
            {
                traces.AddAzureMonitorTraceExporter(o =>
                {
                    o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
                });
            }
        });

        // -----------------------------------------------------  
        // Telemetry Abstraction (ICustomTelemetry)  
        // -----------------------------------------------------  
        if (telemetryBackend == "azure")
        {
            builder.Services.AddApplicationInsightsTelemetryWorkerService();
            builder.Services.AddSingleton<ICustomTelemetry, AzureAppInsightsTelemetry>();
        }
        else
        {
            builder.Services.AddSingleton<ICustomTelemetry, NoOpTelemetry>();
        }

        // ✅ FIXED: Register RabbitMQ configuration using context.Configuration  
        builder.Services.Configure<RabbitMqConfiguration>(
            context.Configuration.GetSection("RabbitMq"));

        // ✅ Register RabbitMQ publisher as singleton (connection pooling)  
        builder.Services.AddSingleton<IMessageQueuePublisher, RabbitMqPublisher>();

        // -----------------------------------------------------  
        // Infrastructure Layer Registrations  
        // -----------------------------------------------------  
        builder.Services.AddScoped<IDbConnectionFactory, DbConnectionFactory>();

        // -----------------------------------------------------  
        // Domain Service Registrations  
        // -----------------------------------------------------  
        builder.Services.AddScoped<IEmailNotificationRepository, EmailNotificationRepository>();
        builder.Services.AddScoped<INotificationHandler, NotificationHandler>();
        builder.Services.AddScoped<NotificationRetryService>();

        // Add HttpClientFactory  
        builder.Services.AddHttpClient();

        // -----------------------------------------------------  
        // Middleware Pipeline  
        // -----------------------------------------------------  
        builder.Services.AddCustomMiddlewares(configuration);
        builder.UseCustomMiddlewares(configuration);

        // -----------------------------------------------------  
        // OpenAPI / Swagger  
        // -----------------------------------------------------  
        builder.Services.AddSingleton<IOpenApiConfigurationOptions, OpenApiConfig>();
    })
    .ConfigureServices(services =>
    {
        services.AddCors();
        services.AddHttpClient();
    })
    .ConfigureOpenApi()
    .Build();

// -----------------------------------------------------  
// Run Host  
// -----------------------------------------------------  
host.Run();