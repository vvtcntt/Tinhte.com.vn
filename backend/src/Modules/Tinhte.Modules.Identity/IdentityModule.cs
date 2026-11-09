using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Tinhte.Modules.Identity.Application;
using Tinhte.Modules.Identity.Infrastructure;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Identity;

/// <summary>
/// Module identity (FR-ID): đăng nhập staff qua OIDC + phiên phía server (WBS-017), RBAC dùng ma trận quyền của Platform (WBS-018).
/// Member auth (memberSession) thuộc WBS-059, sau release gate cộng đồng.
/// </summary>
public sealed class IdentityModule : IModule
{
    public string Name => "identity";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(StaffAuthOptions.Section);
        services.AddOptions<StaffAuthOptions>()
            .Bind(section)
            // Ngoài Development phải cấu hình IdP: lỗi ngay khi khởi động thay vì để CMS không đăng nhập được.
            .Validate<IHostEnvironment>((o, env) => env.IsDevelopment() || o.IsConfigured,
                $"{StaffAuthOptions.Section}:Authority and ClientId are required outside Development.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<StaffAccessStore>();
        services.AddSingleton<StaffSignInService>();
        services.AddSingleton<AdminUserStore>();
        services.AddSingleton<AdminUserService>();
        services.AddScoped<StaffOidcEvents>();

        var auth = services.AddAuthentication(o => o.DefaultScheme = StaffAuthDefaults.SessionScheme)
            .AddScheme<AuthenticationSchemeOptions, StaffSessionHandler>(StaffAuthDefaults.SessionScheme, _ => { });

        // Handler OIDC xử lý callback ở MỌI request → chỉ đăng ký khi đã cấu hình (thiếu ClientId sẽ làm handler lỗi khi khởi tạo).
        var staff = section.Get<StaffAuthOptions>() ?? new StaffAuthOptions();
        if (!staff.IsConfigured) return;

        auth.AddOpenIdConnect(StaffAuthDefaults.OidcScheme, o =>
        {
            o.Authority = staff.Authority;
            o.ClientId = staff.ClientId;
            o.ClientSecret = staff.ClientSecret;
            o.RequireHttpsMetadata = staff.RequireHttpsMetadata;
            o.ResponseType = "code";
            o.ResponseMode = "query";
            o.UsePkce = true;
            o.CallbackPath = StaffAuthDefaults.CallbackPath;
            o.Scope.Clear();
            foreach (var scope in staff.Scopes) o.Scope.Add(scope);
            o.MapInboundClaims = false;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.DisableTelemetry = true;
            o.SignInScheme = StaffAuthDefaults.SessionScheme; // không dùng: TicketReceived tự tạo phiên và HandleResponse
            o.TokenValidationParameters.NameClaimType = "name";
            o.EventsType = typeof(StaffOidcEvents);
            foreach (var cookie in new[] { o.CorrelationCookie, o.NonceCookie })
            {
                cookie.Path = "/";                               // bắt buộc với tiền tố __Host-
                cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.SameSite = SameSiteMode.Lax;
                cookie.HttpOnly = true;
                cookie.IsEssential = true;
            }

            o.CorrelationCookie.Name = StaffAuthDefaults.CorrelationCookie;
            o.NonceCookie.Name = StaffAuthDefaults.NonceCookie;
        });
    }
}
