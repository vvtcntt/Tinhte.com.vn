using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Video;

/// <summary>Module video: Shorts, media pipeline, playback grant (FR-VID). Sprint 0 chỉ có ranh giới; controller đặt trong Controllers/ (kế thừa TinhteControllerBase) theo WBS.</summary>
public sealed class VideoModule : IModule
{
    public string Name => "video";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }
}
