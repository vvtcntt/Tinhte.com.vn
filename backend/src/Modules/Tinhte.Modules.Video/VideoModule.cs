using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Video;

/// <summary>Module video: Shorts, media pipeline, playback grant (FR-VID). Sprint 0 chỉ có ranh giới; endpoint theo hợp đồng v0.3 được thêm theo WBS.</summary>
public sealed class VideoModule : IModule
{
    public string Name => "video";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
    }
}
