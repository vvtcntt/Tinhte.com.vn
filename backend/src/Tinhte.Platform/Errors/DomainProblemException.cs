namespace Tinhte.Platform.Errors;

/// <summary>
/// Lỗi nghiệp vụ mang mã của x-error-catalog. Service/application ném ra; <c>DomainExceptionFilter</c> đổi thành
/// application/problem+json với HTTP status lấy từ <see cref="ErrorCatalog"/>. Mã không có trong catalog bị từ chối ngay.
/// </summary>
public sealed class DomainProblemException : Exception
{
    public DomainProblemException(string code, string title, string? detail = null, IDictionary<string, string[]>? errors = null)
        : base($"{code}: {detail ?? title}")
    {
        Status = ErrorCatalog.StatusOf(code);
        Code = code;
        Title = title;
        Detail = detail;
        Errors = errors;
    }

    public string Code { get; }

    public string Title { get; }

    public string? Detail { get; }

    public int Status { get; }

    public IDictionary<string, string[]>? Errors { get; }
}
