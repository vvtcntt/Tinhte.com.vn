using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Tinhte.Modules.Aggregation;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Platform;
using Tinhte.Platform.Data;
using Tinhte.Worker;

// Worker thu thập RSS của Radar tin GĐ1 (ADR-0008 §3–§4; A-1 chưa chốt — theo phương án mặc định: project riêng, deploy riêng như
// Tinhte.DbMigrator). Không chạy trong tiến trình API. Chạy nhiều bản song song được: feed được thuê bằng FOR UPDATE SKIP LOCKED + lease.
//   dotnet run --project backend/src/Tinhte.Worker                         (chạy worker)
//   dotnet run --project backend/src/Tinhte.Worker -- calibrate --days 90   (báo cáo hiệu chỉnh ngưỡng gom cụm từ tách/gộp tay — chỉ đọc)
// Cấu hình: ConnectionStrings__Tinhte (secret), Sites:Environment (mặc định theo môi trường host), Aggregation:* (appsettings.json).
// Cờ aggregation.radar OFF (mặc định production) ⇒ worker chạy nhưng không thu thập gì; kiểm lại mỗi vòng quét.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddTinhteSiteRegistry();
builder.Services.AddSingleton<ITinhteDatabase, TinhteDatabase>();
AggregationModule.AddCollectorWorker(builder.Services, builder.Configuration);
builder.Services.AddHostedService<FeedCollectorWorker>();

// Giám sát (WBS-021, chưa chọn nền tảng): số đo xuất qua OTLP CHỈ khi có endpoint — biến chuẩn OTEL_EXPORTER_OTLP_ENDPOINT hoặc
// Telemetry:Otlp:Endpoint. Không có thì không gửi gì ra ngoài (dotnet-counters vẫn đọc được meter Tinhte.Aggregation).
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? builder.Configuration["Telemetry:Otlp:Endpoint"];
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    var endpoint = new Uri(otlpEndpoint, UriKind.Absolute);
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("tinhte-worker", serviceVersion: typeof(FeedCollectorWorker).Assembly.GetName().Version?.ToString(3),
            serviceInstanceId: FeedCollectorWorker.WorkerId)
            .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
        .WithMetrics(m => m
            .AddMeter(CollectorMetrics.MeterName)
            .AddRuntimeInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = endpoint));
}

var command = args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('=')) ?? "run";
using var host = builder.Build();
if (!host.Services.GetRequiredService<ITinhteDatabase>().IsConfigured)
{
    Console.Error.WriteLine("ConnectionStrings:Tinhte is not configured.");
    return 1;
}

switch (command)
{
    case "run":
        await host.RunAsync();
        return 0;
    case "calibrate":
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var days = int.TryParse(builder.Configuration["days"], out var d) && d is > 0 and <= 3650 ? d : 90;
        var report = await host.Services.GetRequiredService<ClusterCalibrationService>().RunAsync(days, CancellationToken.None);
        Console.WriteLine($"Hiệu chỉnh gom cụm — {days} ngày gần nhất");
        Console.WriteLine(report.ToText());
        return 0;
    default:
        Console.Error.WriteLine($"unknown command '{command}' (use: run | calibrate [--days N])");
        return 2;
}
