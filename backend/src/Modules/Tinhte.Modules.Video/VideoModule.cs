using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tinhte.Modules.Video.Application;
using Tinhte.Modules.Video.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Video;

/// <summary>
/// Module video: CMS video + media upload (WBS-046, ADR-0006). Shorts feed/watch/playback grant (WBS-041…045), workflow
/// duyệt/publish video (WBS-040) và pipeline transcode/CDN thật (WBS-036…039, chờ chọn vendor WBS-035) là việc sau.
/// </summary>
public sealed class VideoModule : IModule
{
    public const string LocalProvider = "Local";
    public const string NoneProvider = "None";

    public string Name => "video";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MediaOptions>().Bind(configuration.GetSection(MediaOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IMediaStorage>(sp => CreateStorage(sp.GetRequiredService<IOptions<MediaOptions>>().Value, sp.GetRequiredService<IHostEnvironment>()));
        services.AddSingleton<IMediaProcessor>(sp => sp.GetRequiredService<IMediaStorage>() is LocalMediaStorage
            ? new SimulatedMediaProcessor(sp.GetRequiredService<ITinhteDatabase>(), sp.GetRequiredService<IMediaStorage>(),
                sp.GetRequiredService<IOptions<MediaOptions>>().Value.SimulatedStepDelay, sp.GetRequiredService<ILogger<SimulatedMediaProcessor>>())
            : new NoMediaProcessor());
        services.AddSingleton<MediaService>();
        services.AddSingleton<VideoService>();

        // Cấu hình kho lưu trữ sai (Local ngoài Development) phải lỗi lúc khởi động, không đợi request tải lên đầu tiên.
        services.AddTransient<IStartupFilter, MediaStorageStartupCheck>();
    }

    /// <summary>
    /// Local chỉ được dùng ở Development (ADR-0006). Mặc định: Local ở Development, None (fail-closed, 503) ở môi trường khác
    /// cho tới khi có adapter object storage của vendor được chọn (WBS-035/039).
    /// </summary>
    public static IMediaStorage CreateStorage(MediaOptions options, IHostEnvironment environment)
    {
        var provider = string.IsNullOrWhiteSpace(options.StorageProvider)
            ? environment.IsDevelopment() ? LocalProvider : NoneProvider
            : options.StorageProvider.Trim();
        if (string.Equals(provider, NoneProvider, StringComparison.OrdinalIgnoreCase)) return new UnconfiguredMediaStorage();
        if (!string.Equals(provider, LocalProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Media:StorageProvider '{provider}' is not supported (Local for Development, None otherwise).");
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Media:StorageProvider 'Local' is development-only (ADR-0006). Configure an object storage adapter.");
        }

        return new LocalMediaStorage(string.IsNullOrWhiteSpace(options.LocalRoot) ? Path.Combine(Path.GetTempPath(), "tinhte-media") : options.LocalRoot);
    }

    private sealed class MediaStorageStartupCheck(IServiceProvider services) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            _ = services.GetRequiredService<IMediaStorage>();
            return next;
        }
    }
}
