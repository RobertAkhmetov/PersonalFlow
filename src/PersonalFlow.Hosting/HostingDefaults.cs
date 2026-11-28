using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PersonalFlow.Hosting;

public static class HostingDefaults
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
                context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.TimestampFormat = "O");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 5 * 1024 * 1024);

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            if (builder.Configuration["Proxy:Address"] is { Length: > 0 } proxy)
                options.KnownProxies.Add(IPAddress.Parse(proxy));
        });
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
    }
}
