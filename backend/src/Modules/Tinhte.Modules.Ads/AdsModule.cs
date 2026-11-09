using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Ads;

/// <summary>Module ads: Sponsored-lite, ad placements — P0-ADS (FR-ADS). Sprint 0 chỉ có ranh giới; controller đặt trong Controllers/ (kế thừa TinhteControllerBase) theo WBS.</summary>
public sealed class AdsModule : IModule
{
    public string Name => "ads";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }
}
