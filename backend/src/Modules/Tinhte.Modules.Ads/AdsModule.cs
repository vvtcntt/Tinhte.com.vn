using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Ads;

/// <summary>Module ads: Sponsored-lite, ad placements — P0-ADS (FR-ADS). Sprint 0 chỉ có ranh giới; endpoint theo hợp đồng v0.3 được thêm theo WBS.</summary>
public sealed class AdsModule : IModule
{
    public string Name => "ads";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
    }
}
