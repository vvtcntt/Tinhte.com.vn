using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tinhte.Platform.Flags;
using Tinhte.Platform.Http;
using Tinhte.Platform.Modules;
using Tinhte.Platform.Sites;

namespace Tinhte.Platform;

public static class PlatformExtensions
{
    public const string ApiPrefix = "/api/v1";

    public static IServiceCollection AddTinhtePlatform(this IServiceCollection services, IConfiguration configuration, IEnumerable<IModule> modules)
    {
        // Registry tạo lười (sau khi host build) để test/override cấu hình "Sites:*" có hiệu lực.
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var env = sp.GetRequiredService<IHostEnvironment>();
            return SiteRegistry.Load(ResolveRegistryPath(config, env), config["Sites:Environment"] ?? DefaultSiteEnvironment(env));
        });
        services.AddSingleton<IFeatureFlagReader, ConfigurationFeatureFlagReader>();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Chỉ bật khi edge/proxy tin cậy giữ Host gốc trong X-Forwarded-Host (ví dụ Next.js dev rewrite).
            if (configuration.GetValue<bool>("ForwardedHeaders:TrustForwardedHost"))
            {
                options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
            }

            foreach (var proxy in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }
        });

        foreach (var module in modules)
        {
            module.AddServices(services, configuration);
            services.AddSingleton(module);
        }

        return services;
    }

    public static WebApplication UseTinhtePlatform(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<SiteResolutionMiddleware>();
        app.UseMiddleware<OriginGuardMiddleware>();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        });
        return app;
    }

    public static IEndpointRouteBuilder MapTinhteModules(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(ApiPrefix);
        foreach (var module in app.ServiceProvider.GetServices<IModule>())
        {
            module.MapEndpoints(api);
        }

        return app;
    }

    private static string DefaultSiteEnvironment(IHostEnvironment env) =>
        env.IsDevelopment() ? "local" : env.IsStaging() ? "staging" : "production";

    private static string ResolveRegistryPath(IConfiguration configuration, IHostEnvironment env)
    {
        var configured = configuration["Sites:RegistryPath"] ?? Path.Combine("config", "sites", "sites.json");
        if (Path.IsPathRooted(configured)) return configured;
        foreach (var start in new[] { AppContext.BaseDirectory, env.ContentRootPath })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, configured);
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new FileNotFoundException($"Site registry '{configured}' not found (set Sites:RegistryPath).");
    }
}
