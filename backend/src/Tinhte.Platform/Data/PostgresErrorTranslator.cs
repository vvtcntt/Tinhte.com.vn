using Npgsql;
using Tinhte.Platform.Errors;

namespace Tinhte.Platform.Data;

/// <summary>Kết quả dịch lỗi PostgreSQL sang mã của hợp đồng.</summary>
public sealed record TranslatedDbError(string Code, string Title, string? Detail);

/// <summary>
/// Dịch lỗi PostgreSQL sang mã x-error-catalog theo db/README §6:
/// <list type="bullet">
///   <item>SQLSTATE P0001 do <c>tt.raise_domain_error</c> ném ra: MESSAGE chính là mã (FEATURE_DISABLED, STATE_CONFLICT…).</item>
///   <item>Vi phạm CHECK/NOT NULL/FK/unique/dữ liệu sai kiểu → VALIDATION_FAILED (không trả 500).</item>
///   <item>APPEND_ONLY (sửa/xóa bằng chứng) KHÔNG thuộc catalog → null: là bug hoặc truy cập trái phép, để thành 500 và cảnh báo.</item>
/// </list>
/// Chi tiết lỗi gửi cho client chỉ gồm DETAIL do trigger soạn hoặc tên ràng buộc — không lộ câu SQL.
/// </summary>
public static class PostgresErrorTranslator
{
    public const string AppendOnly = "APPEND_ONLY";

    public static bool IsAppendOnlyViolation(PostgresException e) => e.SqlState == PostgresErrorCodes.RaiseException && e.MessageText == AppendOnly;

    public static TranslatedDbError? Translate(PostgresException e)
    {
        if (e.SqlState == PostgresErrorCodes.RaiseException)
        {
            return ErrorCatalog.Statuses.ContainsKey(e.MessageText)
                ? new TranslatedDbError(e.MessageText, TitleOf(e.MessageText), e.Detail)
                : null;
        }

        return e.SqlState switch
        {
            PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.ForeignKeyViolation
                or PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation =>
                new TranslatedDbError(ErrorCodes.ValidationFailed, "Validation failed", ConstraintDetail(e)),
            PostgresErrorCodes.StringDataRightTruncation or PostgresErrorCodes.NumericValueOutOfRange
                or PostgresErrorCodes.InvalidTextRepresentation or PostgresErrorCodes.InvalidDatetimeFormat =>
                new TranslatedDbError(ErrorCodes.ValidationFailed, "Validation failed", "A value has an invalid format or is out of range."),
            _ => null,
        };
    }

    private static string ConstraintDetail(PostgresException e) => e.ConstraintName is { Length: > 0 } name
        ? $"Constraint '{name}' was violated."
        : e.ColumnName is { Length: > 0 } column ? $"Column '{column}' is invalid." : "A data constraint was violated.";

    private static string TitleOf(string code) => code switch
    {
        ErrorCodes.FeatureDisabled => "Feature disabled",
        ErrorCodes.StateConflict => "State conflict",
        ErrorCodes.SponsorshipLabelRequired => "Sponsorship label required",
        ErrorCodes.SponsorshipExpired => "Sponsorship expired",
        ErrorCodes.ValidationFailed => "Validation failed",
        _ => code.Replace('_', ' ').ToLowerInvariant() is var t && t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : code,
    };
}
