using Microsoft.AspNetCore.Mvc;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests.Support;

/// <summary>Probe RBAC trên bề mặt admin (chỉ site cms): quyền ở attribute và ở "domain layer".</summary>
[Route(ApiRoutes.Prefix + "/admin/_rbac")]
public sealed class RbacProbeController : TinhteControllerBase
{
    public sealed record Allowed(string Permission, Guid? UserId);

    [HttpGet("publish")]
    [RequirePermission(Permissions.ContentPublish)]
    public ActionResult<Allowed> Publish() => Ok(new Allowed(Permissions.ContentPublish, User.GetUserId()));

    [HttpGet("read")]
    [RequirePermission(Permissions.ContentRead)]
    public ActionResult<Allowed> Read() => Ok(new Allowed(Permissions.ContentRead, User.GetUserId()));

    /// <summary>Kiểm quyền trong code (service), không qua attribute.</summary>
    [HttpGet("domain")]
    public ActionResult<Allowed> Domain([FromQuery] string permission) => Ok(new Allowed(permission, User.RequirePermission(permission)));

    [HttpPost("taxonomy")]
    [RequirePermission(Permissions.TaxonomyManage)]
    public ActionResult<Allowed> Taxonomy() => Ok(new Allowed(Permissions.TaxonomyManage, User.GetUserId()));
}

/// <summary>Probe trên site công khai: phiên staff không được công nhận ngoài cms.</summary>
[Route(ApiRoutes.Prefix + "/_whoami")]
public sealed class WhoAmIProbeController : TinhteControllerBase
{
    public sealed record WhoAmI(bool Authenticated, bool Staff);

    [HttpGet]
    public ActionResult<WhoAmI> Get() => Ok(new WhoAmI(User.Identity?.IsAuthenticated == true, User.IsStaff()));
}
