using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tinhte.Modules.Aggregation;
using Tinhte.Platform;
using Tinhte.Platform.Data;
using Tinhte.Worker;

// Worker thu thập RSS của Radar tin GĐ1 (ADR-0008 §3–§4; A-1 chưa chốt — theo phương án mặc định: project riêng, deploy riêng như
// Tinhte.DbMigrator). Không chạy trong tiến trình API. Chạy nhiều bản song song được: feed được thuê bằng FOR UPDATE SKIP LOCKED + lease.
//   dotnet run --project backend/src/Tinhte.Worker
// Cấu hình: ConnectionStrings__Tinhte (secret), Sites:Environment (mặc định theo môi trường host), Aggregation:* (appsettings.json).
// Cờ aggregation.radar OFF (mặc định production) ⇒ worker chạy nhưng không thu thập gì; kiểm lại mỗi vòng quét.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddTinhteSiteRegistry();
builder.Services.AddSingleton<ITinhteDatabase, TinhteDatabase>();
AggregationModule.AddCollectorWorker(builder.Services, builder.Configuration);
builder.Services.AddHostedService<FeedCollectorWorker>();

using var host = builder.Build();
if (!host.Services.GetRequiredService<ITinhteDatabase>().IsConfigured)
{
    Console.Error.WriteLine("ConnectionStrings:Tinhte is not configured.");
    return 1;
}

await host.RunAsync();
return 0;
