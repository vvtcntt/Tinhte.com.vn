using System.Security.Claims;
using System.Text.RegularExpressions;
using Npgsql;
using Tinhte.Modules.Ads.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;
using Tinhte.Platform.Sites;

namespace Tinhte.Modules.Ads.Application;

/// <summary>
/// Quản trị vị trí + đặt chỗ quảng cáo (FR-ADS-002, P0-ADS; OpenAPI v0.3 mục 16/19, migration 0350 — DRAFT chờ TL/PO/BA).
/// Vị trí thuộc một site public-web (ADR-0001, bất biến sau khi tạo); banner nằm ở đặt chỗ, mỗi breakpoint tối đa một banner và kích
/// thước phải là kích thước vị trí cho phép. Ghi luôn trong <see cref="ITinhteDatabase.InTransactionAsync{T}"/> với actor; If-Match so
/// trong transaction (khóa dòng) và UPDATE có <c>WHERE row_version = @expected</c>. Ràng buộc DB (0350) là lớp chặn cuối: lỗi CHECK/khóa
/// ngoại được Platform đổi thành VALIDATION_FAILED; service kiểm trước để trả lỗi có tên trường.
/// Cấu hình chạy được khi cờ <c>ads.display_slots</c> OFF — cờ chỉ chặn phần phục vụ công khai.
/// </summary>
public sealed partial class AdAdminService(ITinhteDatabase database, SiteRegistry registry)
{
    // Người duyệt: vào APPROVED luôn ghi người đang duyệt; vào ACTIVE giữ người đã duyệt (ghi người mở nếu dữ liệu cũ thiếu);
    // về DRAFT xóa (phải duyệt lại); PAUSED/EXPIRED giữ để tra cứu.
    private const string ApprovalSet = """
        approved_by = CASE WHEN @status = 'DRAFT' THEN NULL
                           WHEN @approving AND (@status = 'APPROVED' OR approved_by IS NULL OR approved_at IS NULL) THEN @actor
                           ELSE approved_by END,
        approved_at = CASE WHEN @status = 'DRAFT' THEN NULL
                           WHEN @approving AND (@status = 'APPROVED' OR approved_by IS NULL OR approved_at IS NULL) THEN now()
                           ELSE approved_at END
        """;

    // ---------------------------------------------------------------- site + quyền

    /// <summary>SiteKeyFilter: mã sai mẫu, không có trong Registry hoặc không phải public-web → 400 BAD_REQUEST.</summary>
    public string? ParseSiteFilter(string? siteKey)
    {
        if (siteKey is null) return null;
        if (!SiteKeyRegex().IsMatch(siteKey) || registry.Find(siteKey) is not { Kind: "public-web" })
        {
            throw new DomainProblemException(ErrorCodes.BadRequest, "Invalid siteKey", $"'{siteKey}' is not a public-web site of the Site Registry.");
        }

        return siteKey;
    }

    /// <summary>targetUrl hợp lệ: URL tuyệt đối https, không khoảng trắng (pattern ^https:// + CHECK ck_ad_bookings_target_https); sai → UNSAFE_URL.</summary>
    public static bool IsSafeTargetUrl(string url) => BookingFields.IsHttps(url);

    /// <summary>Vào APPROVED/ACTIVE cần <c>ads.approve</c> (MFA) — quyết định phụ thuộc dữ liệu nên kiểm ở đây, không ở attribute.</summary>
    public static void RequireTransitionPermission(ClaimsPrincipal user, string from, string to)
    {
        if (AdStatuses.NeedsApproval(from, to)) user.RequirePermission(Permissions.AdsApprove);
    }

    // ---------------------------------------------------------------- vị trí

    public async Task<AdPlacementList> ListPlacementsAsync(string? siteKey, CancellationToken ct)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        return new AdPlacementList(await ReadPlacementsAsync(c, null, siteKey, null, ct));
    }

    public async Task<AdPlacement> GetPlacementAsync(Guid id, CancellationToken ct)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        return (await ReadPlacementsAsync(c, null, null, id, ct)).FirstOrDefault() ?? throw PlacementNotFound(id);
    }

    public Task<AdPlacement> CreatePlacementAsync(Guid actor, AdPlacementCreate request, CancellationToken ct)
    {
        var site = RequirePlacementSite(request.SiteKey!);
        var label = RequireText("label", request.Label);
        var sizes = NormalizeSizes(request.Sizes!);
        return database.InTransactionAsync(actor, async (c, tx) =>
        {
            await EnsureCodeFreeAsync(c, tx, site.Key, request.Code!, null, ct);
            Guid id;
            await using (var insert = AdSql.Command(c, tx, """
                INSERT INTO tt.ad_placements (site_key, code, label, status, created_by, updated_by)
                VALUES (@site, @code, @label, 'DRAFT', @actor, @actor)
                RETURNING id
                """).WithText("site", site.Key).WithText("code", request.Code).WithText("label", label).WithUuid("actor", actor))
            {
                id = (Guid)(await insert.ExecuteScalarAsync(ct))!;
            }

            await ReplaceSizesAsync(c, tx, id, sizes, ct);
            return await ReadPlacementAsync(c, tx, id, ct);
        }, ct);
    }

    public Task<AdPlacement> UpdatePlacementAsync(Guid actor, Guid id, long expected, AdPlacementUpdate request, CancellationToken ct)
    {
        var label = RequireText("label", request.Label);
        var sizes = NormalizeSizes(request.Sizes!);
        var to = request.Status!;
        return database.InTransactionAsync(actor, async (c, tx) =>
        {
            string site, from;
            await using (var locked = AdSql.Command(c, tx, "SELECT site_key, status, row_version FROM tt.ad_placements WHERE id = @id FOR UPDATE").WithUuid("id", id))
            await using (var r = await locked.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) throw PlacementNotFound(id);
                site = r.GetString(0);
                from = r.GetString(1);
                ETags.EnsureMatches(r.GetInt64(2), expected);
            }

            RequireTransition(from, to);
            await EnsureCodeFreeAsync(c, tx, site, request.Code!, id, ct);

            // Kích thước còn banner dùng không gỡ được (khóa ngoại fk_ad_booking_creatives_size) — báo rõ thay vì VALIDATION_FAILED chung.
            var removed = (await UsedSizesAsync(c, tx, id, ct)).Where(u => !sizes.Contains(u)).ToList();
            if (removed.Count > 0)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "Size in use",
                    $"Bookings of this placement still use {string.Join(", ", removed.Select(s => s.ToString()))}. Change those bookings first.");
            }

            await using (var update = AdSql.Command(c, tx, $"""
                UPDATE tt.ad_placements
                   SET code = @code, label = @label, status = @status, updated_by = @actor,
                       {ApprovalSet}
                 WHERE id = @id AND row_version = @expected
                """).WithText("code", request.Code).WithText("label", label).WithText("status", to).WithUuid("actor", actor)
                .WithBool("approving", AdStatuses.NeedsApproval(from, to)).WithUuid("id", id).WithLong("expected", expected))
            {
                if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            }

            await ReplaceSizesAsync(c, tx, id, sizes, ct);
            return await ReadPlacementAsync(c, tx, id, ct);
        }, ct);
    }

    // ---------------------------------------------------------------- đặt chỗ

    public async Task<AdBookingList> ListBookingsAsync(Guid placementId, CancellationToken ct)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        await using (var exists = AdSql.Command(c, null, "SELECT EXISTS (SELECT 1 FROM tt.ad_placements WHERE id = @id)").WithUuid("id", placementId))
        {
            if (await exists.ExecuteScalarAsync(ct) is not true) throw PlacementNotFound(placementId);
        }

        return new AdBookingList(await ReadBookingsAsync(c, null, placementId, null, ct));
    }

    public async Task<AdBooking> GetBookingAsync(Guid id, CancellationToken ct)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        return (await ReadBookingsAsync(c, null, null, id, ct)).FirstOrDefault() ?? throw BookingNotFound(id);
    }

    public Task<AdBooking> CreateBookingAsync(Guid actor, Guid placementId, AdBookingCreate request, CancellationToken ct) =>
        database.InTransactionAsync(actor, async (c, tx) =>
        {
            // FOR SHARE: kích thước của vị trí không bị đổi giữa lúc kiểm và lúc ghi banner.
            var placementStatus = await LockPlacementAsync(c, tx, placementId, ct) ?? throw PlacementNotFound(placementId);
            if (placementStatus == AdStatuses.Expired)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "Placement expired", "An EXPIRED placement cannot be booked.");
            }

            var fields = BookingFields.From(request.AdvertiserName, request.TargetUrl, request.StartsAt, request.EndsAt, request.Creatives!);
            await ValidateCreativesAsync(c, tx, placementId, fields.Creatives, ct);

            Guid id;
            await using (var insert = AdSql.Command(c, tx, """
                INSERT INTO tt.ad_bookings (placement_id, status, priority, advertiser_name, target_url, starts_at, ends_at, created_by, updated_by)
                VALUES (@placement, 'DRAFT', @priority, @advertiser, @target, @starts, @ends, @actor, @actor)
                RETURNING id
                """).WithUuid("placement", placementId).WithInt("priority", request.Priority ?? 0).WithText("advertiser", fields.AdvertiserName)
                .WithText("target", fields.TargetUrl).WithTime("starts", fields.StartsAt).WithTime("ends", fields.EndsAt).WithUuid("actor", actor))
            {
                id = (Guid)(await insert.ExecuteScalarAsync(ct))!;
            }

            await InsertCreativesAsync(c, tx, id, placementId, fields.Creatives, ct);
            return await ReadBookingAsync(c, tx, id, ct);
        }, ct);

    public Task<AdBooking> UpdateBookingAsync(Guid actor, Guid id, long expected, AdBookingUpdate request, CancellationToken ct) =>
        database.InTransactionAsync(actor, async (c, tx) =>
        {
            Guid placementId;
            string from;
            string? advertiser, target;
            DateTimeOffset? starts, ends;
            await using (var locked = AdSql.Command(c, tx, """
                SELECT placement_id, status, row_version, advertiser_name, target_url, starts_at, ends_at
                  FROM tt.ad_bookings WHERE id = @id FOR UPDATE
                """).WithUuid("id", id))
            await using (var r = await locked.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) throw BookingNotFound(id);
                placementId = r.GetGuid(0);
                from = r.GetString(1);
                ETags.EnsureMatches(r.GetInt64(2), expected);
                advertiser = AdSql.NullableString(r, 3);
                target = AdSql.NullableString(r, 4);
                starts = AdSql.NullableTime(r, 5);
                ends = AdSql.NullableTime(r, 6);
            }

            var to = request.Status!;
            RequireTransition(from, to);
            var fields = BookingFields.From(request.AdvertiserName, request.TargetUrl, request.StartsAt, request.EndsAt, request.Creatives!);
            await LockPlacementAsync(c, tx, placementId, ct);

            var current = (await ReadCreativesAsync(c, tx, [id], ct)).GetValueOrDefault(id) ?? [];
            var creativesChanged = !BookingFields.SameCreatives(current, fields.Creatives);
            var contentChanged = creativesChanged || advertiser != fields.AdvertiserName || target != fields.TargetUrl
                || !BookingFields.SameTime(starts, fields.StartsAt) || !BookingFields.SameTime(ends, fields.EndsAt);
            if (contentChanged && from != AdStatuses.Draft)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "Booking locked",
                    $"Creatives, targetUrl, advertiserName and the schedule change only while the booking is DRAFT (now {from}). Set status DRAFT first; it must then be approved again.");
            }

            if (to is AdStatuses.Approved or AdStatuses.Active && fields.TargetUrl is null)
            {
                throw Invalid("targetUrl", $"targetUrl is required for status {to}.");
            }

            if (creativesChanged) await ValidateCreativesAsync(c, tx, placementId, fields.Creatives, ct);

            await using (var update = AdSql.Command(c, tx, $"""
                UPDATE tt.ad_bookings
                   SET status = @status, priority = @priority, advertiser_name = @advertiser, target_url = @target,
                       starts_at = @starts, ends_at = @ends, updated_by = @actor,
                       {ApprovalSet}
                 WHERE id = @id AND row_version = @expected
                """).WithText("status", to).WithInt("priority", request.Priority!.Value).WithText("advertiser", fields.AdvertiserName)
                .WithText("target", fields.TargetUrl).WithTime("starts", fields.StartsAt).WithTime("ends", fields.EndsAt).WithUuid("actor", actor)
                .WithBool("approving", AdStatuses.NeedsApproval(from, to)).WithUuid("id", id).WithLong("expected", expected))
            {
                if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            }

            if (creativesChanged)
            {
                await using (var delete = AdSql.Command(c, tx, "DELETE FROM tt.ad_booking_creatives WHERE booking_id = @id").WithUuid("id", id))
                {
                    await delete.ExecuteNonQueryAsync(ct);
                }

                await InsertCreativesAsync(c, tx, id, placementId, fields.Creatives, ct);
            }

            return await ReadBookingAsync(c, tx, id, ct);
        }, ct);

    // ---------------------------------------------------------------- kiểm tra

    private SiteDefinition RequirePlacementSite(string siteKey) =>
        registry.Find(siteKey) is { Kind: "public-web", Status: "active" or "planned" } site
            ? site
            : throw Invalid("siteKey", $"'{siteKey}' is not an active or planned public-web site of the Site Registry.");

    private static void RequireTransition(string from, string to)
    {
        if (!AdStatuses.CanTransition(from, to))
        {
            throw new DomainProblemException(ErrorCodes.StateConflict, "Status transition not allowed", $"Cannot go from {from} to {to}.");
        }
    }

    private static string RequireText(string field, string? value) =>
        value?.Trim() is { Length: > 0 } text ? text : throw Invalid(field, $"{field} must not be blank.");

    /// <summary>uniqueItems của sizes: trùng (breakpoint, width, height) → VALIDATION_FAILED.</summary>
    private static List<SizeKey> NormalizeSizes(IReadOnlyList<AdSize> sizes)
    {
        var keys = sizes.Select(s => new SizeKey(s.Breakpoint!, s.Width, s.Height)).ToList();
        var duplicate = keys.GroupBy(k => k).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw Invalid("sizes", $"Duplicate size {duplicate.Key}.");
        return keys;
    }

    private static async Task EnsureCodeFreeAsync(NpgsqlConnection c, NpgsqlTransaction tx, string siteKey, string code, Guid? exceptId, CancellationToken ct)
    {
        await using var command = AdSql.Command(c, tx, """
            SELECT EXISTS (SELECT 1 FROM tt.ad_placements WHERE site_key = @site AND code = @code AND (@id::uuid IS NULL OR id <> @id))
            """).WithText("site", siteKey).WithText("code", code).WithUuid("id", exceptId);
        if (await command.ExecuteScalarAsync(ct) is true)
        {
            throw Invalid("code", $"Code '{code}' is already used on site '{siteKey}'.");
        }
    }

    /// <summary>Kích thước phải thuộc vị trí ở breakpoint đó; media phải tồn tại và là ảnh (phục vụ hay chưa thì creativeUrl cho biết).</summary>
    private static async Task ValidateCreativesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid placementId, IReadOnlyList<CreativeInput> creatives, CancellationToken ct)
    {
        var allowed = new HashSet<SizeKey>();
        await using (var sizes = AdSql.Command(c, tx, "SELECT breakpoint, width, height FROM tt.ad_placement_sizes WHERE placement_id = @id").WithUuid("id", placementId))
        await using (var r = await sizes.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) allowed.Add(new SizeKey(r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        }

        var mediaTypes = new Dictionary<Guid, string>();
        await using (var media = AdSql.Command(c, tx, "SELECT id, media_type FROM tt.media_assets WHERE id = ANY(@ids)")
            .WithArray("ids", creatives.Select(x => x.MediaAssetId).Distinct().ToArray()))
        await using (var r = await media.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) mediaTypes[r.GetGuid(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
        }

        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < creatives.Count; i++)
        {
            var x = creatives[i];
            if (!allowed.Contains(x.Size))
            {
                var options = allowed.Where(a => a.Breakpoint == x.Size.Breakpoint).Select(a => $"{a.Width}×{a.Height}").ToList();
                errors[$"creatives[{i}]"] = [options.Count == 0
                    ? $"This placement has no size for {x.Size.Breakpoint}."
                    : $"{x.Size.Width}×{x.Size.Height} is not allowed for {x.Size.Breakpoint} (allowed: {string.Join(", ", options)})."];
            }

            if (!mediaTypes.TryGetValue(x.MediaAssetId, out var type))
            {
                errors[$"creatives[{i}].mediaAssetId"] = [$"Media {x.MediaAssetId} does not exist."];
            }
            else if (type != "IMAGE")
            {
                errors[$"creatives[{i}].mediaAssetId"] = [$"Media {x.MediaAssetId} is {(type.Length == 0 ? "of unknown type" : type)}; banners must be IMAGE media."];
            }
        }

        if (errors.Count > 0)
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "Some creatives are not valid for this placement.", errors);
        }
    }

    // ---------------------------------------------------------------- ghi

    private static async Task ReplaceSizesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid placementId, IReadOnlyList<SizeKey> sizes, CancellationToken ct)
    {
        var bps = sizes.Select(s => s.Breakpoint).ToArray();
        var ws = sizes.Select(s => s.Width).ToArray();
        var hs = sizes.Select(s => s.Height).ToArray();
        await using (var delete = AdSql.Command(c, tx, """
            DELETE FROM tt.ad_placement_sizes s
             WHERE s.placement_id = @id
               AND NOT EXISTS (SELECT 1 FROM unnest(@bps::text[], @ws::int[], @hs::int[]) AS n(bp, w, h)
                                WHERE n.bp = s.breakpoint AND n.w = s.width AND n.h = s.height)
            """).WithUuid("id", placementId).WithArray("bps", bps).WithArray("ws", ws).WithArray("hs", hs))
        {
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using var insert = AdSql.Command(c, tx, """
            INSERT INTO tt.ad_placement_sizes (placement_id, breakpoint, width, height)
            SELECT @id, n.bp, n.w, n.h FROM unnest(@bps::text[], @ws::int[], @hs::int[]) AS n(bp, w, h)
            ON CONFLICT DO NOTHING
            """).WithUuid("id", placementId).WithArray("bps", bps).WithArray("ws", ws).WithArray("hs", hs);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertCreativesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid bookingId, Guid placementId, IReadOnlyList<CreativeInput> creatives, CancellationToken ct)
    {
        await using var insert = AdSql.Command(c, tx, """
            INSERT INTO tt.ad_booking_creatives (booking_id, placement_id, breakpoint, width, height, media_asset_id, alt_text)
            SELECT @booking, @placement, n.bp, n.w, n.h, n.media, n.alt
              FROM unnest(@bps::text[], @ws::int[], @hs::int[], @media::uuid[], @alt::text[]) AS n(bp, w, h, media, alt)
            """).WithUuid("booking", bookingId).WithUuid("placement", placementId)
            .WithArray("bps", creatives.Select(x => x.Size.Breakpoint).ToArray())
            .WithArray("ws", creatives.Select(x => x.Size.Width).ToArray())
            .WithArray("hs", creatives.Select(x => x.Size.Height).ToArray())
            .WithArray("media", creatives.Select(x => x.MediaAssetId).ToArray())
            .WithArray("alt", creatives.Select(x => x.AltText).ToArray());
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> LockPlacementAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid placementId, CancellationToken ct)
    {
        await using var command = AdSql.Command(c, tx, "SELECT status FROM tt.ad_placements WHERE id = @id FOR SHARE").WithUuid("id", placementId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<List<SizeKey>> UsedSizesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid placementId, CancellationToken ct)
    {
        var used = new List<SizeKey>();
        await using var command = AdSql.Command(c, tx, "SELECT DISTINCT breakpoint, width, height FROM tt.ad_booking_creatives WHERE placement_id = @id")
            .WithUuid("id", placementId);
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) used.Add(new SizeKey(r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        return used;
    }

    // ---------------------------------------------------------------- đọc

    private async Task<AdPlacement> ReadPlacementAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct) =>
        (await ReadPlacementsAsync(c, tx, null, id, ct)).Single();

    private static async Task<List<AdPlacement>> ReadPlacementsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string? siteKey, Guid? id, CancellationToken ct)
    {
        var rows = new List<(Guid Id, string Site, string Code, string Label, string Status, Guid? By, DateTimeOffset? At, long Version)>();
        await using (var command = AdSql.Command(c, tx, """
            SELECT id, site_key, code, label, status, approved_by, approved_at, row_version
              FROM tt.ad_placements
             WHERE (@site::text IS NULL OR site_key = @site) AND (@id::uuid IS NULL OR id = @id)
             ORDER BY site_key, code, id
            """).WithText("site", siteKey).WithUuid("id", id))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                rows.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), AdSql.NullableGuid(r, 5),
                    AdSql.NullableTime(r, 6), r.GetInt64(7)));
            }
        }

        var sizes = new Dictionary<Guid, List<AdSize>>();
        if (rows.Count > 0)
        {
            await using var command = AdSql.Command(c, tx, "SELECT placement_id, breakpoint, width, height FROM tt.ad_placement_sizes WHERE placement_id = ANY(@ids)")
                .WithArray("ids", rows.Select(x => x.Id).ToArray());
            await using var r = await command.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!sizes.TryGetValue(r.GetGuid(0), out var list)) sizes[r.GetGuid(0)] = list = [];
                list.Add(new AdSize(r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
            }
        }

        return rows.Select(x => new AdPlacement(x.Id, x.Site, x.Code, x.Label, x.Status,
            (sizes.GetValueOrDefault(x.Id) ?? []).OrderBy(s => AdBreakpoints.IndexOf(s.Breakpoint)).ThenBy(s => s.Width).ThenBy(s => s.Height).ToList(),
            x.By, x.At, x.Version)).ToList();
    }

    private async Task<AdBooking> ReadBookingAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct) =>
        (await ReadBookingsAsync(c, tx, null, id, ct)).Single();

    private async Task<List<AdBooking>> ReadBookingsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid? placementId, Guid? id, CancellationToken ct)
    {
        var rows = new List<AdBooking>();
        await using (var command = AdSql.Command(c, tx, """
            SELECT id, placement_id, status, priority, advertiser_name, target_url, starts_at, ends_at, approved_by, approved_at, row_version
              FROM tt.ad_bookings
             WHERE (@placement::uuid IS NULL OR placement_id = @placement) AND (@id::uuid IS NULL OR id = @id)
             ORDER BY created_at DESC, id DESC
            """).WithUuid("placement", placementId).WithUuid("id", id))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                rows.Add(new AdBooking(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt16(3), AdSql.NullableString(r, 4), AdSql.NullableString(r, 5),
                    AdSql.NullableTime(r, 6), AdSql.NullableTime(r, 7), [], AdSql.NullableGuid(r, 8), AdSql.NullableTime(r, 9), r.GetInt64(10)));
            }
        }

        if (rows.Count == 0) return rows;
        var creatives = await ReadCreativesAsync(c, tx, rows.Select(b => b.Id).ToArray(), ct);
        return rows.Select(b => b with { Creatives = creatives.GetValueOrDefault(b.Id) ?? [] }).ToList();
    }

    /// <summary>creativeUrl chỉ khi media phục vụ được (READY + IMAGE + PUBLIC + quyền CLEARED còn hạn) — cùng điều kiện với GET /ads/placements.</summary>
    private async Task<Dictionary<Guid, List<AdCreative>>> ReadCreativesAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid[] bookingIds, CancellationToken ct)
    {
        var mediaOrigin = AdCreativeUrls.MediaOrigin(registry);
        var result = new Dictionary<Guid, List<AdCreative>>();
        await using (var command = AdSql.Command(c, tx, """
            SELECT c.booking_id, c.breakpoint, c.width, c.height, c.media_asset_id, c.alt_text,
                   CASE WHEN m.status = 'READY' AND m.media_type = 'IMAGE' AND m.visibility = 'PUBLIC' AND m.rights_status = 'CLEARED'
                             AND (m.rights_expires_at IS NULL OR m.rights_expires_at > now()) THEN m.storage_key END
              FROM tt.ad_booking_creatives c
              JOIN tt.media_assets m ON m.id = c.media_asset_id
             WHERE c.booking_id = ANY(@ids)
            """).WithArray("ids", bookingIds))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var storageKey = AdSql.NullableString(r, 6);
                if (!result.TryGetValue(r.GetGuid(0), out var list)) result[r.GetGuid(0)] = list = [];
                list.Add(new AdCreative(r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetGuid(4), r.GetString(5),
                    mediaOrigin is not null && storageKey is not null ? AdCreativeUrls.For(mediaOrigin, storageKey) : null));
            }
        }

        foreach (var list in result.Values) list.Sort((a, b) => AdBreakpoints.IndexOf(a.Breakpoint).CompareTo(AdBreakpoints.IndexOf(b.Breakpoint)));
        return result;
    }

    // ---------------------------------------------------------------- lỗi

    private static DomainProblemException PlacementNotFound(Guid id) => new(ErrorCodes.NotFound, "Placement not found", $"Ad placement {id} does not exist.");

    private static DomainProblemException BookingNotFound(Guid id) => new(ErrorCodes.NotFound, "Booking not found", $"Ad booking {id} does not exist.");

    internal static DomainProblemException Invalid(string field, string message) =>
        new(ErrorCodes.ValidationFailed, "Validation failed", message, new Dictionary<string, string[]> { [field] = [message] });

    [GeneratedRegex(AdPatterns.SiteKey)]
    private static partial Regex SiteKeyRegex();
}

/// <summary>Kích thước (breakpoint, width, height) — khóa của tt.ad_placement_sizes.</summary>
internal readonly record struct SizeKey(string Breakpoint, int Width, int Height)
{
    public override string ToString() => $"{Breakpoint} {Width}×{Height}";
}

internal sealed record CreativeInput(SizeKey Size, Guid MediaAssetId, string AltText);

/// <summary>Trường nội dung của đặt chỗ đã chuẩn hóa (trim, rỗng → null) và kiểm phần không cần DB.</summary>
internal sealed partial record BookingFields(string? AdvertiserName, string? TargetUrl, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, IReadOnlyList<CreativeInput> Creatives)
{
    public static BookingFields From(string? advertiserName, string? targetUrl, DateTimeOffset? startsAt, DateTimeOffset? endsAt, IReadOnlyList<AdCreative> creatives)
    {
        var target = string.IsNullOrWhiteSpace(targetUrl) ? null : targetUrl.Trim();
        if (target is not null && !IsHttps(target))
        {
            // Chặn javascript:, data:, http: — khớp pattern ^https:// của hợp đồng và CHECK ck_ad_bookings_target_https.
            throw new DomainProblemException(ErrorCodes.UnsafeUrl, "Unsafe URL", "targetUrl must be an absolute https URL.",
                new Dictionary<string, string[]> { ["targetUrl"] = ["targetUrl must be an absolute https URL."] });
        }

        if (startsAt is { } s && endsAt is { } e && e <= s) throw AdAdminService.Invalid("endsAt", "endsAt must be after startsAt.");

        var duplicate = creatives.GroupBy(x => x.Breakpoint).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw AdAdminService.Invalid("creatives", $"At most one creative per breakpoint ({duplicate.Key} appears {duplicate.Count()} times).");

        var normalized = new List<CreativeInput>();
        for (var i = 0; i < creatives.Count; i++)
        {
            var x = creatives[i];
            var alt = x.AltText?.Trim();
            if (string.IsNullOrEmpty(alt)) throw AdAdminService.Invalid($"creatives[{i}].altText", "altText must not be blank.");
            normalized.Add(new CreativeInput(new SizeKey(x.Breakpoint!, x.Width, x.Height), x.MediaAssetId!.Value, alt));
        }

        return new BookingFields(string.IsNullOrWhiteSpace(advertiserName) ? null : advertiserName.Trim(), target, startsAt, endsAt, normalized);
    }

    public static bool IsHttps(string url) =>
        HttpsPattern().IsMatch(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0;

    /// <summary>So banner hiện có với yêu cầu theo breakpoint (thứ tự gửi lên không quan trọng).</summary>
    public static bool SameCreatives(IReadOnlyList<AdCreative> current, IReadOnlyList<CreativeInput> requested) =>
        current.Count == requested.Count
        && current.All(c => requested.Any(r => r.Size == new SizeKey(c.Breakpoint!, c.Width, c.Height) && r.MediaAssetId == c.MediaAssetId && r.AltText == c.AltText));

    /// <summary>PostgreSQL lưu tới micro giây; client có thể gửi thêm chữ số.</summary>
    public static bool SameTime(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b is null : b is not null && Math.Abs((a.Value - b.Value).Ticks) < 10;

    [GeneratedRegex(@"^https://\S+$")]
    private static partial Regex HttpsPattern();
}
