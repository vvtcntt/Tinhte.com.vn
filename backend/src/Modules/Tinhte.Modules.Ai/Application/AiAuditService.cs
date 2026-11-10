using Tinhte.Modules.Ai.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Flags;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.Ai.Application;

/// <summary>getAdminAiAudit (FR-AI-004): mỗi lần gửi nội dung ra nhà cung cấp AI là một dòng, mới nhất trước, cursor (createdAt, id).</summary>
public sealed class AiAuditService(ITinhteDatabase database, ICursorCodec cursors)
{
    public sealed record Filter(Guid? ActorId, DateTimeOffset? From, DateTimeOffset? To);

    public async Task<CursorPage<AiAuditItem>> ListAsync(Filter filter, PageRequest page, string scope, CancellationToken ct)
    {
        if (filter.From is { } from && filter.To is { } to && from >= to)
        {
            throw new DomainProblemException(ErrorCodes.BadRequest, "Invalid range", "from must be earlier than to.");
        }

        var after = page.Cursor is null ? null : cursors.Decode<AiSql.AuditPosition>(scope, page.Cursor);
        await using var connection = await database.OpenConnectionAsync(ct);
        if (!await AiSql.FlagEnabledAsync(connection, FeatureFlagKeys.AiAssistance, ct))
        {
            throw new DomainProblemException(ErrorCodes.FeatureDisabled, "Feature disabled", $"{FeatureFlagKeys.AiAssistance} is OFF.");
        }

        var rows = await AiSql.ListAuditAsync(connection, filter.ActorId, filter.From, filter.To, after, page.Limit + 1, ct);
        var result = rows.ToPage(page.Limit, cursors, scope, r => new AiSql.AuditPosition(r.Item.At, r.Id));
        return new CursorPage<AiAuditItem>(result.Items.Select(r => r.Item).ToList(), result.NextCursor);
    }
}
