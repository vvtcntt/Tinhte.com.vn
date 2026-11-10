using System.ComponentModel.DataAnnotations;

namespace Tinhte.Modules.Ai.Application;

/// <summary>Schema AiSuggestionRequest: contentId là id bài hoặc video; task TAG | SUMMARY.</summary>
public sealed record AiSuggestionRequest(
    [Required] Guid? ContentId,
    [Required, RegularExpression(AiTasks.Pattern)] string? Task);

/// <summary>Schema AiSuggestion. Không ghi gì vào nội dung; người biên tập chọn/sửa rồi tự lưu (FR-AI-001).</summary>
public sealed record AiSuggestion(IReadOnlyList<string> Suggestions, bool RequiresHumanApproval);

/// <summary>Một dòng của AuditLogList (getAdminAiAudit).</summary>
public sealed record AiAuditItem(DateTimeOffset At, Guid ActorId, string Action, string ObjectId);

public static class AiTasks
{
    public const string Tag = "TAG";
    public const string Summary = "SUMMARY";
    public const string Pattern = "^(TAG|SUMMARY)$";

    /// <summary>Giá trị <c>action</c> trong nhật ký (khớp CHECK ck_ai_requests_action của 0353 và mô tả getAdminAiAudit).</summary>
    public static string AuditAction(string task) => task == Tag ? "ai.suggest_tags" : "ai.suggest_summary";
}
