using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;

namespace Tinhte.Platform.Mvc;

/// <summary>
/// Đổi ngoại lệ thành Problem theo hợp đồng (đăng ký toàn cục cho mọi controller):
/// <see cref="DomainProblemException"/> → mã của nó; <see cref="PostgresException"/> → <see cref="PostgresErrorTranslator"/>;
/// mất kết nối DB → 503 DEPENDENCY_UNAVAILABLE. Phần còn lại để UseExceptionHandler trả 500 INTERNAL_ERROR.
/// </summary>
public sealed partial class DomainExceptionFilter(ILogger<DomainExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case DomainProblemException e:
                context.Result = ProblemFactory.Create(context.HttpContext, e.Code, e.Title, e.Detail, e.Errors);
                context.ExceptionHandled = true;
                break;

            case PostgresException e when PostgresErrorTranslator.IsAppendOnlyViolation(e):
                LogAppendOnly(logger, context.HttpContext.Request.Method, context.HttpContext.Request.Path, e.Detail);
                break; // không xử lý → 500 INTERNAL_ERROR

            case PostgresException e when PostgresErrorTranslator.Translate(e) is { } translated:
                context.Result = ProblemFactory.Create(context.HttpContext, translated.Code, translated.Title, translated.Detail);
                context.ExceptionHandled = true;
                break;

            case NpgsqlException e when e is not PostgresException && e.IsTransient:
                LogDatabaseUnavailable(logger, e);
                context.Result = ProblemFactory.Create(context.HttpContext, ErrorCodes.DependencyUnavailable, "Database unavailable", "Please retry later.");
                context.ExceptionHandled = true;
                break;
        }
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Critical, Message = "SECURITY: append-only evidence modification attempted via {Method} {Path}: {Detail}")]
    private static partial void LogAppendOnly(ILogger logger, string method, string path, string? detail);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Error, Message = "Database unavailable")]
    private static partial void LogDatabaseUnavailable(ILogger logger, Exception exception);
}
