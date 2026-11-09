using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tinhte.Platform.Flags;
using Tinhte.Platform.Http;
using Tinhte.Platform.Modules;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Sites;

namespace Tinhte.Platform;

public static class PlatformExtensions
{
    /// <summary>
    /// Đăng ký nền tảng + MVC controllers (ADR-0002). Controller của Tinhte.Api được MVC tự phát hiện;
    /// controller của module được thêm qua ApplicationPart từ assembly của từng module.
    /// </summary>
    public static IMvcBuilder AddTinhtePlatform(this IServiceCollection services, IConfiguration configuration, IEnumerable<IModule> modules)
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

        var mvc = services.AddControllers()
            .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = ProblemFactory.FromInvalidModelState);

        foreach (var module in modules)
        {
            module.AddServices(services, configuration);
            services.AddSingleton(module);
            mvc.AddApplicationPart(module.GetType().Assembly);
        }

        return mvc;
    }

    /// <summary>Pipeline: lỗi chưa xử lý → forwarded headers → phân giải site → chặn Origin → header bảo mật.</summary>
    public static WebApplication UseTinhtePlatform(this WebApplication app)
    {
        app.UseExceptionHandler(handler => handler.Run(WriteUnhandledProblemAsync));
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

    /// <summary>
    /// 500 không lộ chi tiết lỗi. Lưu ý hợp đồng: x-error-catalog v0.3 chưa có mã cho 500 →
    /// tạm dùng INTERNAL_ERROR, đề xuất bổ sung vào catalog (xem ADR-0002).
    /// </summary>
    private static Task WriteUnhandledProblemAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var problem = new TinhteProblem
        {
            Type = "urn:tinhte:error:internal_error",
            Title = "Internal server error",
            Status = StatusCodes.Status500InternalServerError,
            Code = "INTERNAL_ERROR",
            TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
        };
        return context.Response.WriteAsJsonAsync(problem, JsonSerializerOptions.Web, "application/problem+json");
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
