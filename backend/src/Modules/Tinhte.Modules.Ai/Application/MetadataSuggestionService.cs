namespace Tinhte.Modules.Ai.Application;

/// <summary>
/// Gợi ý tag/tóm tắt cho bài hoặc video (postAdminAiMetadataSuggestions, FR-AI-001). Thứ tự kiểm, nhật ký (FR-AI-004), hạn mức và
/// ánh xạ lỗi nhà cung cấp: <see cref="AiRequestPipeline"/> (dùng chung với <see cref="RewriteService"/>).
/// Kết quả không ghi vào bài: requiresHumanApproval luôn true.
/// </summary>
public sealed class MetadataSuggestionService(AiRequestPipeline pipeline)
{
    /// <param name="onRateLimited">Nhận thời gian chờ trước khi ném 429 RATE_LIMITED (controller đặt header Retry-After).</param>
    public async Task<AiSuggestion> SuggestAsync(Guid actorId, AiSuggestionRequest request, Action<TimeSpan> onRateLimited, CancellationToken ct)
    {
        var task = request.Task!;
        var job = new AiRequestPipeline.Job<IReadOnlyList<string>>(
            AiTasks.AuditAction(task),
            MetadataPrompts.BuildInput,
            input => MetadataPrompts.For(task, input),
            content => MetadataPrompts.ParseSuggestions(task, content) is { Count: > 0 } list ? list : null);
        var suggestions = await pipeline.RunAsync(actorId, request.ContentId!.Value, job, onRateLimited, ct);
        return new AiSuggestion(suggestions, RequiresHumanApproval: true);
    }

    internal static string Sha256(string input) => AiRequestPipeline.Sha256(input);
}
