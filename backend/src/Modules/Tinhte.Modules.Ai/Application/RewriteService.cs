namespace Tinhte.Modules.Ai.Application;

/// <summary>
/// Viết lại đoạn văn người biên tập đã chọn (postAdminAiRewrite, FR-AI-001/004). Cùng cờ, thứ tự kiểm, hạn mức, nhật ký (action
/// <c>ai.rewrite</c>, objectId = contentId, chỉ độ dài + sha256 của đoạn gửi đi) và ánh xạ lỗi với gợi ý metadata —
/// <see cref="AiRequestPipeline"/>. contentId chỉ dùng cho nhật ký + kiểm tồn tại; nội dung bài/video không được gửi đi.
/// Kết quả không ghi vào nội dung: requiresHumanApproval luôn true.
/// </summary>
public sealed class RewriteService(AiRequestPipeline pipeline)
{
    /// <param name="onRateLimited">Nhận thời gian chờ trước khi ném 429 RATE_LIMITED (controller đặt header Retry-After).</param>
    public async Task<AiRewrite> RewriteAsync(Guid actorId, AiRewriteRequest request, Action<TimeSpan> onRateLimited, CancellationToken ct)
    {
        var mode = request.Mode!;
        var text = request.Text!;
        var job = new AiRequestPipeline.Job<string>(
            AiRewriteModes.AuditAction,
            (_, maxChars) => RewritePrompts.BuildInput(text, maxChars),
            input => RewritePrompts.For(mode, request.Note, input),
            RewritePrompts.ParseAnswer,
            EmptyInputMessage: "The text to rewrite is empty.", EmptyInputField: "text");
        var rewritten = await pipeline.RunAsync(actorId, request.ContentId!.Value, job, onRateLimited, ct);
        return new AiRewrite(rewritten, RequiresHumanApproval: true);
    }
}
