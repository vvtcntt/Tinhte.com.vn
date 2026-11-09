using Tinhte.Modules.Ads;
using Tinhte.Modules.Community;
using Tinhte.Modules.Identity;
using Tinhte.Modules.News;
using Tinhte.Modules.Video;
using Tinhte.Platform;
using Tinhte.Platform.Endpoints;
using Tinhte.Platform.Modules;

var builder = WebApplication.CreateBuilder(args);

IModule[] modules = [new NewsModule(), new VideoModule(), new CommunityModule(), new AdsModule(), new IdentityModule()];
builder.Services.AddTinhtePlatform(builder.Configuration, modules);

var app = builder.Build();

app.UseTinhtePlatform();      // forwarded headers → phân giải site theo Host → chặn Origin → header bảo mật
app.MapPlatformEndpoints();   // /health/*, GET /api/v1/site/config
app.MapTinhteModules();       // endpoint của từng module dưới /api/v1

app.Run();

/// <summary>Điểm vào cho WebApplicationFactory trong test.</summary>
public partial class Program;
