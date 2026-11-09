using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Tinhte.Api.Tests.Support;

/// <summary>
/// IdP OIDC giả trong bộ nhớ (chỉ cho test): discovery, JWKS, token endpoint có kiểm PKCE (S256) và client secret.
/// Gắn vào handler OIDC qua <c>BackchannelHttpHandler</c>, nên luồng đăng nhập chạy thật từ đầu tới cuối
/// (state, nonce, PKCE, chữ ký id_token, at_hash đều được handler chuẩn của ASP.NET Core kiểm).
/// </summary>
public sealed class FakeOidcProvider : HttpMessageHandler
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "tinhte-cms-test";
    public const string ClientSecret = "test-only-secret";

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "test-key-1" };
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new();

    public sealed record PendingCode(string Subject, string Nonce, string CodeChallenge, string[] Amr, string? Name);

    /// <summary>Mô phỏng người dùng đăng nhập thành công tại IdP: trả mã code cho callback.</summary>
    public string Authorize(string subject, string nonce, string codeChallenge, string[] amr, string? name = null)
    {
        var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new PendingCode(subject, nonce, codeChallenge, amr, name);
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            "/.well-known/openid-configuration" => Json(new Dictionary<string, object>
            {
                ["issuer"] = Issuer,
                ["authorization_endpoint"] = Issuer + "/authorize",
                ["token_endpoint"] = Issuer + "/token",
                ["jwks_uri"] = Issuer + "/jwks",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
            }),
            "/jwks" => Json(new { keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(_key) } }),
            "/token" => await TokenAsync(request, cancellationToken),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        if (form.GetValueOrDefault("client_id") != ClientId || form.GetValueOrDefault("client_secret") != ClientSecret
            || form.GetValueOrDefault("grant_type") != "authorization_code"
            || !_codes.TryRemove(form.GetValueOrDefault("code") ?? "", out var pending))
        {
            return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
        }

        var verifier = form.GetValueOrDefault("code_verifier") ?? "";
        if (Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != pending.CodeChallenge)
        {
            return Json(new { error = "invalid_grant", error_description = "PKCE verification failed" }, HttpStatusCode.BadRequest);
        }

        var accessToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = pending.Subject,
            ["nonce"] = pending.Nonce,
            ["amr"] = pending.Amr,
            ["auth_time"] = new DateTimeOffset(now).ToUnixTimeSeconds(),
            ["at_hash"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))[..16]),
        };
        if (pending.Name is not null) claims["name"] = pending.Name;

        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
        return Json(new Dictionary<string, object> { ["access_token"] = accessToken, ["token_type"] = "Bearer", ["expires_in"] = 300, ["id_token"] = idToken });
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
