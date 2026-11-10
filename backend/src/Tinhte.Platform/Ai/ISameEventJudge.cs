namespace Tinhte.Platform.Ai;

/// <summary>Một mục tin đưa cho bộ so sánh: chỉ trường công khai của RSS (tiêu đề, tóm tắt đã làm sạch), không có toàn văn.</summary>
public sealed record SameEventCandidate(Guid ItemId, string Title, string? Summary, string? Language);

public enum SameEventVerdict
{
    /// <summary>Không kết luận (AI tắt, lỗi, hết ngân sách, không chắc) ⇒ bên gọi giữ quyết định của MinHash.</summary>
    Unknown,

    Same,

    Different,
}

/// <summary>Kết luận cho một cặp. <c>Provider</c> và <c>Model</c> để ghi lại cho BTV kiểm khi tách/gộp tay; null khi không gọi AI.</summary>
public sealed record SameEventJudgement(SameEventVerdict Verdict, double Confidence, string? Provider = null, string? Model = null)
{
    public static SameEventJudgement Unknown { get; } = new(SameEventVerdict.Unknown, 0);
}

/// <summary>
/// Điểm nối "AI xác định tin trùng chủ đề" của Radar tin (ADR-0008 §7 sửa đổi 10/11/2026, BA-01 P-02). Module Aggregation chỉ phụ thuộc interface này;
/// module kết nối AI (nhánh riêng) đăng ký bản thật thay cho <see cref="NoAiSameEventJudge"/>. Hợp đồng:
/// <list type="bullet">
///   <item>chỉ nhận cặp ứng viên đã lọc bằng MinHash, cùng site, cùng chuyên mục, cùng ngày — không gọi cho mọi cặp;</item>
///   <item>chỉ gửi tiêu đề + tóm tắt; không gửi toàn văn, URL nội bộ, dữ liệu người dùng;</item>
///   <item>không ném lỗi vì AI lỗi/hết ngân sách/hết giờ: trả <see cref="SameEventJudgement.Unknown"/> để Radar vẫn chạy bằng MinHash;</item>
///   <item>bên gọi chỉ dùng khi cờ <c>aggregation.ai_dedup</c> bật, và không gọi trong transaction đang giữ khóa.</item>
/// </list>
/// </summary>
public interface ISameEventJudge
{
    /// <summary>false: bản mặc định (không có AI) — bên gọi bỏ qua bước AI mà không cần đọc cờ.</summary>
    bool UsesAi { get; }

    Task<SameEventJudgement> JudgeAsync(SameEventCandidate first, SameEventCandidate second, CancellationToken ct);
}

/// <summary>Bản mặc định: không gọi AI, luôn "không kết luận" ⇒ gom cụm hoàn toàn bằng MinHash như GĐ1 ban đầu.</summary>
public sealed class NoAiSameEventJudge : ISameEventJudge
{
    public bool UsesAi => false;

    public Task<SameEventJudgement> JudgeAsync(SameEventCandidate first, SameEventCandidate second, CancellationToken ct) =>
        Task.FromResult(SameEventJudgement.Unknown);
}
