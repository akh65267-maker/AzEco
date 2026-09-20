using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ecommerce.ServiceDefaults;

/// <summary>
/// One OpenTelemetry pipeline, two possible destinations.
///
/// Local  : OTLP -> Aspire Dashboard (docker compose). Same instrumentation, free, offline.
/// Azure  : Azure Monitor exporter -> Application Insights -> Log Analytics workspace.
///
/// The application code never knows which. That is the entire point of OpenTelemetry:
/// instrumentation is vendor-neutral, only the exporter is vendor-specific.
/// </summary>
public static class ObservabilityExtensions
{
    public static IHostApplicationBuilder AddEcommerceObservability(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var serviceName = builder.Environment.ApplicationName;
        var serviceVersion = typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        // Resource attributes are attached to EVERY signal. They are how you answer
        // "which service, which version, which instance" without adding it to each log line.
        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceVersion: serviceVersion, serviceInstanceId: Environment.MachineName)
            .AddAttributes(
            [
                new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName),
            ]);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resource);

            // Without these two, a structured log arrives as a rendered string and you
            // lose the ability to filter on the individual properties in KQL.
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        // Health probes fire every few seconds. Tracing them buries real
                        // traffic and, at $2.30/GB ingested, costs actual money.
                        o.Filter = context => !IsInfrastructureEndpoint(context.Request.Path);
                        o.RecordException = true;
                    })
                    .AddHttpClientInstrumentation(o => o.RecordException = true)
                    .AddSource(EcommerceTelemetry.Namespace)
                    // Npgsql emits its own ActivitySource; EF/Npgsql DB spans appear as
                    // children of the request span once this is registered.
                    .AddSource("Npgsql")
                    // Azure SDK (Service Bus, Blob, Key Vault) spans, including the
                    // producer/consumer links that make the messaging trace continuous.
                    .AddSource("Azure.*");
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(EcommerceTelemetry.Namespace)
                    .AddMeter("Npgsql");
            });

        AddExporters(builder);

        return builder;
    }

    private static void AddExporters(IHostApplicationBuilder builder)
    {
        // Azure Monitor wins when its connection string is present — that is how the
        // Azure environments are configured. Absent it, we fall back to OTLP for local.
        var appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(appInsights))
        {
            // The distro wires the Azure Monitor exporter into the existing providers;
            // it does not replace the instrumentation configured above.
            builder.Services.AddOpenTelemetry().UseAzureMonitor(o =>
            {
                o.ConnectionString = appInsights;

                // Sampling: keep everything outside production, thin it out in production.
                // Azure Monitor's rate-limited sampler keeps all telemetry for a sampled
                // trace together — you never get a request without its dependencies.
                o.SamplingRatio = builder.Environment.IsProduction() ? 0.2f : 1.0f;
            });
            return;
        }

        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            builder.Services.AddOpenTelemetry()
                .WithTracing(t => t.AddOtlpExporter())
                .WithMetrics(m => m.AddOtlpExporter());
            builder.Logging.AddOpenTelemetry(l => l.AddOtlpExporter());
        }

        // Neither configured (e.g. a unit test host): instrumentation still runs,
        // it just goes nowhere. That is intentional — no exporter should ever be
        // a startup hard-failure.
    }

    internal static bool IsInfrastructureEndpoint(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/alive", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);
}
