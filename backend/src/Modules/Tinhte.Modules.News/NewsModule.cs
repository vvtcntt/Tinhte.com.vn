using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.News;

/// <summary>Module news: Tin bài, chuyên mục, revision, publish (FR-NEWS, FR-CMS). Sprint 0 chỉ có ranh giới; controller đặt trong Controllers/ (kế thừa TinhteControllerBase) theo WBS.</summary>
public sealed class NewsModule : IModule
{
    public string Name => "news";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }
}
