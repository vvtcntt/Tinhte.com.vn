using Microsoft.Extensions.Hosting;

namespace Tinhte.Platform;

/// <summary>
/// Tìm file/thư mục cấu hình dùng chung của repo (config/sites/sites.json, migration SQL…) từ đường dẫn tương đối:
/// dò ngược từ thư mục chạy và content root. Đường dẫn tuyệt đối dùng nguyên.
/// </summary>
public static class ContentPaths
{
    public static string Resolve(string configured, IHostEnvironment env, bool directory = false)
    {
        if (Path.IsPathRooted(configured)) return configured;
        foreach (var start in new[] { AppContext.BaseDirectory, env.ContentRootPath })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, configured);
                if (directory ? Directory.Exists(candidate) : File.Exists(candidate)) return candidate;
            }
        }

        throw new FileNotFoundException($"'{configured}' not found from {AppContext.BaseDirectory} or {env.ContentRootPath}.");
    }
}
