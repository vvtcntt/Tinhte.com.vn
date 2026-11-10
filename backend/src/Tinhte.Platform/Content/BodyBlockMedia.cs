using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Tinhte.Platform.Errors;

namespace Tinhte.Platform.Content;

/// <summary>
/// FR-CMS-009 cho khối image trong thân bài/video: mediaId phải là media đã tải lên qua upload intent, loại IMAGE, chưa hỏng/bị từ chối
/// (FAILED/QUARANTINED/REJECTED/RESTRICTED) và quyền chưa bị hạn chế/thu hồi/hết hạn (RESTRICTED/EXPIRED/REVOKED hoặc rights_expires_at
/// đã qua). UNVERIFIED vẫn gắn được vào bản nháp (giống media của video). Danh mục trạng thái lặp lại MediaStatuses.Unusable /
/// MediaRights.Blocked của module Video bằng SQL vì News không tham chiếu Video — sửa một bên thì sửa bên kia.
/// Khóa FOR SHARE các dòng media để quyền không bị đổi giữa lúc kiểm và lúc ghi revision.
/// </summary>
public static class BodyBlockMedia
{
    /// <summary>Ném 422 VALIDATION_FAILED, khóa <c>{field}[i].mediaId</c>, cho khối image đầu tiên không gắn được.</summary>
    public static async Task EnsureImagesAttachableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, JsonElement body, string field, CancellationToken ct)
    {
        var images = BodyBlocks.ImageMediaIds(body);
        if (images.Count == 0) return;

        var found = new Dictionary<Guid, (string? Type, string Status, string Rights, bool Expired)>();
        await using (var command = new NpgsqlCommand("""
            SELECT m.id, m.media_type, m.status, m.rights_status, m.rights_expires_at IS NOT NULL AND m.rights_expires_at <= now()
              FROM tt.media_assets m
             WHERE m.id = ANY(@ids) AND m.storage_key IS NOT NULL
               FOR SHARE OF m
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = images.Select(i => i.MediaId).Distinct().ToArray() });
            await using var r = await command.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                found[r.GetGuid(0)] = (r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4));
            }
        }

        foreach (var (index, mediaId) in images)
        {
            var message = !found.TryGetValue(mediaId, out var m) ? "Unknown media."
                : m.Type != "IMAGE" ? "Only an IMAGE media asset can be used in an image block."
                : m.Status is "FAILED" or "QUARANTINED" or "REJECTED" or "RESTRICTED" ? $"The media is {m.Status} and cannot be used; upload the image again."
                : m.Expired || m.Rights is "RESTRICTED" or "EXPIRED" or "REVOKED" ? $"The media rights are {(m.Expired ? "EXPIRED" : m.Rights)}; replace this image."
                : null;
            if (message is not null)
            {
                var path = $"{field}[{index}].mediaId";
                throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", message, new Dictionary<string, string[]> { [path] = [message] });
            }
        }
    }
}
