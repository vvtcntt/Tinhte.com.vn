using System.Diagnostics;
using Tinhte.Platform.Data.Migrations;
using Tinhte.Platform.Sites;

namespace Tinhte.Api.Tests;

/// <summary>WBS-013: phần migration không cần DB — phát hiện file, checksum, SQL đồng bộ tt.sites khớp bản Node.</summary>
public sealed class MigrationTests : IDisposable
{
    private static readonly string MigrationsDir = RepoPaths.File("doc", "07_AI_REVIEW", "v0.3", "db", "migrations");
    private readonly string _temp = Directory.CreateTempSubdirectory("tt-mig-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Discovers_v03_migrations_in_numeric_order_without_optional_ones()
    {
        var scripts = MigrationRunner.Discover(MigrationsDir);
        Assert.Equal(["0299", "0300", "0301", "0302", "0303", "0304", "0305", "0306", "0307", "0308", "0309", "0310", "0312", "0313", "0314", "0320", "0321", "0322", "0323"], scripts.Select(s => s.Version));
        Assert.DoesNotContain(scripts, s => s.Name.Contains("privacy", StringComparison.Ordinal)); // 0311 chỉ chạy khi Legal quyết định
        Assert.All(scripts, s => Assert.DoesNotMatch(@"(?im)^\s*(BEGIN|COMMIT)\s*;", s.Sql)); // runner bọc transaction
    }

    [Fact]
    public void Dev_baseline_runs_first_as_version_0000()
    {
        var baseline = RepoPaths.File("doc", "07_AI_REVIEW", "v0.3", "db", "tests", "baseline_04_stub.sql");
        var scripts = MigrationRunner.Discover(MigrationsDir, baseline);
        Assert.Equal("0000", scripts[0].Version);
        Assert.Equal("dev_baseline_04_stub", scripts[0].Name);
    }

    [Fact]
    public void Checksum_ignores_line_endings_but_not_content()
    {
        Assert.Equal(MigrationRunner.ComputeChecksum("SELECT 1;\nSELECT 2;\n"), MigrationRunner.ComputeChecksum("SELECT 1;\r\nSELECT 2;\r\n"));
        Assert.NotEqual(MigrationRunner.ComputeChecksum("SELECT 1;"), MigrationRunner.ComputeChecksum("SELECT 2;"));
    }

    [Theory]
    [InlineData("12_short.up.sql")]
    [InlineData("0400_Bad-Name.up.sql")]
    public void Rejects_badly_named_files(string file)
    {
        File.WriteAllText(Path.Combine(_temp, file), "SELECT 1;");
        Assert.Throws<MigrationException>(() => MigrationRunner.Discover(_temp));
    }

    [Fact]
    public void Rejects_duplicate_versions()
    {
        File.WriteAllText(Path.Combine(_temp, "0400_a.up.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(_temp, "0400_b.up.sql"), "SELECT 1;");
        Assert.Contains("0400", Assert.Throws<MigrationException>(() => MigrationRunner.Discover(_temp)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Site_sync_sql_matches_the_node_generator_line_by_line()
    {
        var registry = SiteRegistry.Load(RepoPaths.File("config", "sites", "sites.json"), "production");
        var csharp = SiteDbSync.Generate(registry.Document.Sites).Split('\n');

        var psi = new ProcessStartInfo("node", "tools/sites/generate-db-sync.mjs")
        {
            WorkingDirectory = RepoPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var node = Process.Start(psi)!;
        var output = node.StandardOutput.ReadToEnd();
        node.WaitForExit();
        Assert.True(node.ExitCode == 0, node.StandardError.ReadToEnd());

        var js = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.StartsWith("-- GENERATED", js[0], StringComparison.Ordinal);
        Assert.Equal(js[1..], csharp[1..]); // dòng 0 là tiêu đề nêu tên bộ sinh
    }
}
