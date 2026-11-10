using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tinhte.Modules.Ai.Application;
using Tinhte.Modules.Ai.Infrastructure;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Ai;

/// <summary>
/// Module AI (FR-AI, P2 — cờ <c>ai.assistance</c>, production mặc định OFF, bật cần PO + Legal). Đã có: gợi ý tag/tóm tắt cho bài/video
/// qua nhà cung cấp tương thích OpenAI (mặc định cổng YesScale) — POST /admin/ai/metadata-suggestions; viết lại đoạn văn — POST /admin/ai/rewrite (0354); nhật ký gọi AI — GET /admin/ai/audit
/// (migration 0353). Quyền ĐỀ XUẤT <c>ai.assist</c> (config/rbac/rbac.json, chờ BA/PO). Chưa có: transcript-jobs, /ai/search.
/// </summary>
public sealed class AiModule : IModule
{
    public string Name => "ai";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        // Timeout theo từng lần thử do client tự đặt (AiProviderOptions.TimeoutSeconds), không dùng timeout chung của HttpClient.
        services.AddHttpClient(OpenAiCompatibleChatClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IAiChatClient, OpenAiCompatibleChatClient>();
        services.AddSingleton<AiRequestPipeline>();
        services.AddSingleton<MetadataSuggestionService>();
        services.AddSingleton<RewriteService>();
        services.AddSingleton<AiAuditService>();
    }
}
