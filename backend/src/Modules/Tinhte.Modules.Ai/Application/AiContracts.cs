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

    /// <summary>Giá trị <c>action</c> trong nhật ký (khớp CHECK ck_ai_requests_action của 0353/0354 và mô tả getAdminAiAudit).</summary>
    public static string AuditAction(string task) => task == Tag ? "ai.suggest_tags" : "ai.suggest_summary";
}

/// <summary>
/// Schema AiRewriteRequest (postAdminAiRewrite): đoạn văn người biên tập đã chọn (văn bản thuần 1–6000 ký tự), bài/video chứa nó
/// (cho nhật ký; không có → 404), chế độ viết lại và ghi chú tùy chọn (≤ 300 ký tự).
/// </summary>
public sealed record AiRewriteRequest(
    [Required] Guid? ContentId,
    [Required(AllowEmptyStrings = false), StringLength(AiRewriteModes.MaxTextLength, MinimumLength = 1)] string? Text,
    [Required, RegularExpression(AiRewriteModes.Pattern)] string? Mode,
    [StringLength(AiRewriteModes.MaxNoteLength)] string? Note);

/// <summary>Schema AiRewrite: một đoạn đã viết lại; không ghi vào nội dung, người biên tập tự chọn thay/chèn/bỏ.</summary>
public sealed record AiRewrite(string Text, bool RequiresHumanApproval);

public static class AiRewriteModes
{
    public const string Improve = "IMPROVE";
    public const string Shorten = "SHORTEN";
    public const string Expand = "EXPAND";
    public const string FixSpelling = "FIX_SPELLING";
    public const string NeutralTone = "NEUTRAL_TONE";
    public const string Pattern = "^(IMPROVE|SHORTEN|EXPAND|FIX_SPELLING|NEUTRAL_TONE)$";
    public const int MaxTextLength = 6000;
    public const int MaxNoteLength = 300;

    /// <summary>Giá trị <c>action</c> trong nhật ký (CHECK ck_ai_requests_action của 0354, mô tả getAdminAiAudit).</summary>
    public const string AuditAction = "ai.rewrite";
}
