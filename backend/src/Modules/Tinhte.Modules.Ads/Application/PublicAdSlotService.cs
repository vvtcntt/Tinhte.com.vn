using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tinhte.Platform.Data;
using Tinhte.Platform.Flags;
using Tinhte.Platform.Sites;

namespace Tinhte.Modules.Ads.Application;

/// <summary>Schema AdBreakpoint: MOBILE &lt; 768px, TABLET 768–1023px, DESKTOP ≥ 1024px (UX-PUB-01).</summary>
public static class AdBreakpoints
{
    public const string Mobile = "MOBILE";
    public const string Tablet = "TABLET";
    public const string Desktop = "DESKTOP";

    /// <summary>Thứ tự trả về trong <c>creatives</c>: màn hình nhỏ trước.</summary>
    public static readonly IReadOnlyList<string> All = [Mobile, Tablet, Desktop];
}

/// <summary>Schema PublicAdCreative: một banner cho một breakpoint, kích thước thật của ảnh (FE giữ chỗ, không phóng to).</summary>
public sealed record PublicAdCreative(string Breakpoint, int Width, int Height, string CreativeUrl, string AltText, string? TargetUrl);

/// <summary>Schema PublicAdSlot (AIR-013): không có trạng thái/lịch/đặt chỗ nội bộ. Tối đa một banner mỗi breakpoint.</summary>
public sealed record PublicAdSlot(string Code, string Label, IReadOnlyList<PublicAdCreative> Creatives)
{
    public string Rel { get; } = "sponsored noopener";
}

/// <summary>Schema PublicAdSlotList.</summary>
public sealed record PublicAdSlotList(IReadOnlyList<PublicAdSlot> Items);

/// <summary>
/// getAdsPlacements (FR-ADS-001/002, WBS-133; DRAFT v0.3 đợt 3): slot của <b>site trong Host</b> (ADR-0001) cho các mã được hỏi.
/// Mỗi breakpoint chọn một banner từ đặt chỗ ACTIVE trong khung giờ của vị trí ACTIVE — ưu tiên cao nhất, rồi startsAt mới nhất,
/// rồi id (ổn định, không xoay vòng). Media phải READY + IMAGE + PUBLIC + quyền CLEARED còn hạn (fail-closed). Không có banner nào
/// → không trả slot (FE không vẽ khung trống). Cờ <c>ads.display_slots</c> đọc từ tt.feature_gates trong cùng câu truy vấn (cùng nguồn
/// với chốt chặn DB, như PublicArticleSql); OFF → danh sách rỗng.
/// </summary>
public sealed partial class PublicAdSlotService(ITinhteDatabase database, SiteRegistry registry, ILogger<PublicAdSlotService> logger)
{
    /// <summary>OpenAPI v0.3 <c>PlacementCodes.maxItems</c>.</summary>
    public const int MaxCodes = 10;

    private const string Sql = """
        SELECT DISTINCT ON (p.code, c.breakpoint)
               p.code, p.label, c.breakpoint, c.width, c.height, m.storage_key, c.alt_text, b.target_url
          FROM tt.ad_placements p
          JOIN tt.ad_bookings b ON b.placement_id = p.id
          JOIN tt.ad_booking_creatives c ON c.booking_id = b.id AND c.placement_id = p.id
          JOIN tt.media_assets m ON m.id = c.media_asset_id
         WHERE COALESCE((SELECT g.enabled FROM tt.feature_gates g WHERE g.key = @flag), false)
           AND p.site_key = @site AND p.code = ANY(@codes) AND p.status = 'ACTIVE'
           AND b.status = 'ACTIVE' AND (b.starts_at IS NULL OR b.starts_at <= now()) AND (b.ends_at IS NULL OR b.ends_at > now())
           AND m.status = 'READY' AND m.media_type = 'IMAGE' AND m.visibility = 'PUBLIC' AND m.storage_key IS NOT NULL
           AND m.rights_status = 'CLEARED' AND (m.rights_expires_at IS NULL OR m.rights_expires_at > now())
         ORDER BY p.code, c.breakpoint, b.priority DESC, b.starts_at DESC NULLS LAST, b.id
        """;

    /// <summary>
    /// <c>placementCodes</c> (style form, explode false: <c>a,b</c>; chấp nhận cả lặp tham số). Bỏ trùng, giữ thứ tự.
    /// null = sai hợp đồng (rỗng, mã sai mẫu, quá <see cref="MaxCodes"/> mã) → 400.
    /// </summary>
    public static IReadOnlyList<string>? ParseCodes(IEnumerable<string?>? values)
    {
        var codes = new List<string>();
        foreach (var value in values ?? [])
        {
            foreach (var code in (value ?? string.Empty).Split(','))
            {
                if (!CodePattern().IsMatch(code)) return null;
                if (!codes.Contains(code, StringComparer.Ordinal)) codes.Add(code);
            }
        }

        return codes.Count is > 0 and <= MaxCodes ? codes : null;
    }

    public async Task<PublicAdSlotList> GetAsync(string siteKey, IReadOnlyList<string> codes, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        var media = AdCreativeUrls.MediaOrigin(registry);
        if (media is null)
        {
            // Không có site media trong Registry của môi trường → không dựng được URL ảnh → không phục vụ (fail-closed).
            logger.LogWarning("No media site in the Site Registry for {Environment}; ad slots are not served.", registry.EnvironmentName);
            return new PublicAdSlotList([]);
        }

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("flag", FeatureFlagKeys.AdsDisplaySlots);
        command.Parameters.AddWithValue("site", siteKey);
        command.Parameters.AddWithValue("codes", codes.ToArray());

        var found = new Dictionary<string, (string Label, List<PublicAdCreative> Creatives)>(StringComparer.Ordinal);
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var code = r.GetString(0);
                if (!found.TryGetValue(code, out var slot)) found[code] = slot = (r.GetString(1), []);
                slot.Creatives.Add(new PublicAdCreative(
                    Breakpoint: r.GetString(2),
                    Width: r.GetInt32(3),
                    Height: r.GetInt32(4),
                    CreativeUrl: AdCreativeUrls.For(media, r.GetString(5)),
                    AltText: r.GetString(6),
                    TargetUrl: r.IsDBNull(7) ? null : r.GetString(7)));
            }
        }

        var items = new List<PublicAdSlot>();
        foreach (var code in codes)
        {
            if (!found.TryGetValue(code, out var slot)) continue;
            var ordered = slot.Creatives.OrderBy(c => IndexOf(c.Breakpoint)).ToList();
            items.Add(new PublicAdSlot(code, string.IsNullOrWhiteSpace(slot.Label) ? "Quảng cáo" : slot.Label, ordered));
        }

        return new PublicAdSlotList(items);
    }

    private static int IndexOf(string breakpoint)
    {
        for (var i = 0; i < AdBreakpoints.All.Count; i++)
        {
            if (AdBreakpoints.All[i] == breakpoint) return i;
        }

        return int.MaxValue;
    }

    [GeneratedRegex("^[a-z0-9_-]{2,80}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// URL công khai của ảnh banner = origin của site <c>kind: media</c> trong Site Registry + <c>storage_key</c>
/// (không viết cứng host — ADR-0001). DRAFT: đường dẫn công khai của media/CDN chưa chốt (WBS media) — đổi ở đây khi có.
/// </summary>
public static class AdCreativeUrls
{
    public static string? MediaOrigin(SiteRegistry registry) =>
        registry.ResolvableSites.FirstOrDefault(s => s.Kind == "media") is { } site ? registry.OriginFor(site) : null;

    public static string For(string mediaOrigin, string storageKey) =>
        mediaOrigin + "/" + string.Join('/', storageKey.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
