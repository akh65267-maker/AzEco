using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Ecommerce.ServiceDefaults;

/// <summary>
/// We do not invent a correlation id. The W3C <c>traceparent</c> already propagates
/// through APIM, HTTP, and (once we put it there) Service Bus messages, and .NET
/// populates <see cref="Activity.Current"/> from it automatically.
///
/// All this middleware does is make that id *visible* to humans: echo it on every
/// response so a support ticket saying "error id 4bf92f..." is one KQL filter away
/// from the full distributed trace.
///
/// Inventing a second id (X-Correlation-Id) means two ids that disagree the moment
/// someone forgets to forward one.
/// </summary>
public static class CorrelationMiddleware
{
    public const string HeaderName = "x-request-id";

    public static IApplicationBuilder UseEcommerceCorrelation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            // Write on starting: the trace id must be on the response even when the
            // pipeline throws, which is exactly when someone needs it.
            context.Response.OnStarting(() =>
            {
                var traceId = Activity.Current?.TraceId.ToString();
                if (!string.IsNullOrEmpty(traceId))
                {
                    context.Response.Headers[HeaderName] = traceId;
                }

                return Task.CompletedTask;
            });

            await next().ConfigureAwait(false);
        });
    }
}
