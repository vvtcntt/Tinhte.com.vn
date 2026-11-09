using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Community;

/// <summary>Module community: Bình luận, moderation, takedown — P0* sau release gate (FR-COM, FR-MOD). Sprint 0 chỉ có ranh giới; endpoint theo hợp đồng v0.3 được thêm theo WBS.</summary>
public sealed class CommunityModule : IModule
{
    public string Name => "community";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
    }
}
