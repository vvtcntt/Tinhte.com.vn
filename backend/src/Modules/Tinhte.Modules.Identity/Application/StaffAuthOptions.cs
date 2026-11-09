namespace Tinhte.Modules.Identity.Application;

/// <summary>
/// Cấu hình "Auth:Staff" (ADR-0004). IdP chưa chọn → mọi IdP chuẩn OIDC (authorization code + PKCE) đều dùng được.
/// ClientSecret lấy từ secret manager / biến môi trường <c>Auth__Staff__ClientSecret</c>, không ghi vào appsettings.
/// </summary>
public sealed class StaffAuthOptions
{
    public const string Section = "Auth:Staff";

    /// <summary>URL issuer của IdP (có /.well-known/openid-configuration). Trống = chưa bật đăng nhập staff.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>Gửi <c>acr_values</c> để IdP yêu cầu MFA (giá trị tùy IdP). Trống = không gửi.</summary>
    public string? AcrValues { get; set; }

    /// <summary>Giá trị <c>amr</c> (RFC 8176) coi là đã qua MFA.</summary>
    public string[] MfaAmrValues { get; set; } = ["mfa", "otp", "hwk", "swk", "fido", "sms"];

    /// <summary>Giá trị <c>acr</c> coi là đã qua MFA (tùy IdP).</summary>
    public string[] MfaAcrValues { get; set; } = [];

    /// <summary>Không hoạt động quá thời gian này → phiên hết hạn (FR-ID-006 timeout).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Hạn tuyệt đối của phiên kể từ lúc đăng nhập.</summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(12);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}

public static class StaffAuthDefaults
{
    public const string SessionScheme = "staff-session";
    public const string OidcScheme = "staff-oidc";
    public const string CallbackPath = "/api/v1/auth/staff/oidc/callback";

    /// <summary>Tên cookie suy ra từ <c>session: staff</c> trong Site Registry (ADR-0001 D4) — khớp securitySchemes.cmsSession.</summary>
    public const string SessionCookie = "__Host-tt_cms_session";

    /// <summary>Token CSRF (double-submit) — JS của CMS đọc và gửi lại trong header X-CSRF-Token.</summary>
    public const string CsrfCookie = "__Host-tt_cms_csrf";

    public const string CorrelationCookie = "__Host-tt_cms_oidc_corr.";
    public const string NonceCookie = "__Host-tt_cms_oidc_nonce.";
}
