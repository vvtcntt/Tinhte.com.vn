namespace Tinhte.Api.Tests;

internal static class RepoPaths
{
    /// <summary>Thư mục gốc repo: chứa config/sites/sites.json.</summary>
    public static string Root { get; } = FindRoot();

    public static string File(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "config", "sites", "sites.json"))
                && Directory.Exists(Path.Combine(dir.FullName, "doc")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
