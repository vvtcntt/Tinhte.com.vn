using Tinhte.Modules.Ads;
using Tinhte.Modules.Community;
using Tinhte.Modules.Identity;
using Tinhte.Modules.News;
using Tinhte.Modules.Video;
using Tinhte.Platform;
using Tinhte.Platform.Modules;

var builder = WebApplication.CreateBuilder(args);

// ASP.NET Core Web API dạng Controller (ADR-0002) + module (ADR-0001 cho đa subdomain).
IModule[] modules = [new NewsModule(), new VideoModule(), new CommunityModule(), new AdsModule(), new IdentityModule()];
builder.Services.AddTinhtePlatform(builder.Configuration, modules);   // AddControllers + ApplicationPart của module
builder.Services.AddOpenApi();                                         // tài liệu sinh từ code, chỉ để xem khi dev

var app = builder.Build();

app.UseTinhtePlatform();   // exception → forwarded headers → phân giải site theo Host → chặn Origin → header bảo mật

if (app.Environment.IsDevelopment())
{
    // Nguồn sự thật vẫn là hợp đồng doc/07_AI_REVIEW/v0.3/07_OPENAPI_TINHTE_v0.3_DRAFT.yaml (contract-first).
    app.MapOpenApi("/api/v1/openapi.json");
}

app.MapControllers();

app.Run();

/// <summary>Điểm vào cho WebApplicationFactory trong test.</summary>
public partial class Program;
