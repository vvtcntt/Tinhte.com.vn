using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Modules.News.Application;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.News;

/// <summary>
/// Module news: bài viết, revision, publish pointer (WBS-022; FR-NEWS-001/002/004/005, FR-CMS-001/004/006).
/// Workflow duyệt (WBS-024), lịch/preview (WBS-025), taxonomy + rights (WBS-023) là việc sau.
/// </summary>
public sealed class NewsModule : IModule
{
    public string Name => "news";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NewsOptions>().Bind(configuration.GetSection(NewsOptions.Section));
        services.AddSingleton<ArticleService>();
        services.AddSingleton<PublicArticleService>();
        services.AddSingleton<HomeService>();
        services.AddSingleton<HomeCurationService>();

        // bodyBlocks là JSON tự do (JsonElement): service tự kiểm, MVC không duyệt đệ quy vào cây JSON.
        services.Configure<MvcOptions>(o => o.ModelMetadataDetailsProviders.Add(new SuppressChildValidationMetadataProvider(typeof(JsonElement))));
    }
}
