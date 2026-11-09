using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Identity;

/// <summary>Module identity: Staff/member auth, RBAC, privacy requests (FR-ID). Sprint 0 chỉ có ranh giới; controller đặt trong Controllers/ (kế thừa TinhteControllerBase) theo WBS.</summary>
public sealed class IdentityModule : IModule
{
    public string Name => "identity";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }
}
