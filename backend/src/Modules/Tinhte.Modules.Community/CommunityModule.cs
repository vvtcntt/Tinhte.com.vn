using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Community;

/// <summary>Module community: Bình luận, moderation, takedown — P0* sau release gate (FR-COM, FR-MOD). Sprint 0 chỉ có ranh giới; controller đặt trong Controllers/ (kế thừa TinhteControllerBase) theo WBS.</summary>
public sealed class CommunityModule : IModule
{
    public string Name => "community";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }
}
