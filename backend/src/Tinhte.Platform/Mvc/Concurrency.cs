using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Tinhte.Platform.Errors;

namespace Tinhte.Platform.Mvc;

/// <summary>
/// Optimistic concurrency theo hợp đồng (components.parameters.IfMatch, headers.ETag; db/README §6):
/// ETag = <c>W/"&lt;row_version&gt;"</c>; thiếu If-Match → 428 PRECONDITION_REQUIRED; không khớp → 412 REVISION_CONFLICT.
/// </summary>
public static class ETags
{
    public static string ForRowVersion(long rowVersion) => $"W/\"{rowVersion.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>Nhận <c>W/"7"</c> hoặc <c>"7"</c>. Không nhận <c>*</c>, danh sách nhiều ETag hay giá trị khác.</summary>
    public static bool TryParseRowVersion(string? value, out long rowVersion)
    {
        rowVersion = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (v.StartsWith("W/", StringComparison.Ordinal)) v = v[2..];
        return v.Length >= 3 && v[0] == '"' && v[^1] == '"'
            && long.TryParse(v.AsSpan(1, v.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out rowVersion);
    }

    /// <summary>
    /// Đọc If-Match. Gọi SAU khi đã nạp tài nguyên (thứ tự đánh giá lỗi của catalog: 404 trước 428/412).
    /// </summary>
    public static long RequireIfMatch(HttpContext context)
    {
        var header = context.Request.Headers[HeaderNames.IfMatch];
        if (header.Count == 0 || string.IsNullOrWhiteSpace(header.ToString()))
        {
            throw new DomainProblemException(ErrorCodes.PreconditionRequired, "If-Match required", "Send If-Match with the latest ETag of this resource.");
        }

        if (header.Count != 1 || !TryParseRowVersion(header[0], out var expected))
        {
            throw new DomainProblemException(ErrorCodes.RevisionConflict, "Revision conflict", "If-Match must be a single ETag returned by this API.");
        }

        return expected;
    }

    /// <summary>So row_version hiện tại với If-Match; lệch → 412. UPDATE vẫn phải có <c>WHERE row_version = @expected</c>.</summary>
    public static void EnsureMatches(long currentRowVersion, long expectedRowVersion)
    {
        if (currentRowVersion != expectedRowVersion)
        {
            throw RevisionConflict();
        }
    }

    /// <summary>Dùng khi UPDATE ... WHERE row_version = @expected trả 0 dòng dù tài nguyên còn tồn tại.</summary>
    public static DomainProblemException RevisionConflict() =>
        new(ErrorCodes.RevisionConflict, "Revision conflict", "The resource was modified by someone else. Reload it and retry.");

    public static void Set(HttpResponse response, long rowVersion) => response.Headers.ETag = ForRowVersion(rowVersion);
}
