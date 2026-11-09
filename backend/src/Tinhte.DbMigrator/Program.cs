using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Tinhte.DbMigrator;
using Tinhte.Platform;
using Tinhte.Platform.Data;
using Tinhte.Platform.Data.Migrations;
using Tinhte.Platform.Sites;

// Áp migration SQL + đồng bộ tt.sites (WBS-013). Chạy như một bước deploy riêng, KHÔNG chạy lúc API khởi động.
//   dotnet run --project backend/src/Tinhte.DbMigrator -- up       (mặc định)
//   dotnet run --project backend/src/Tinhte.DbMigrator -- status
//   dotnet run --project backend/src/Tinhte.DbMigrator -- grant-role --issuer <iss> --subject <sub> --role ADMIN --reason "..." [--name "..."]
// Cấu hình: ConnectionStrings__Tinhte (biến môi trường / secret), Database:Migrations:Directory, Sites:RegistryPath.
// Content root = thư mục chứa bản build để luôn đọc appsettings*.json của migrator, dù chạy từ thư mục nào.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
var command = args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('=')) ?? "up";
try
{
    var options = MigratorOptions.From(builder.Configuration, builder.Environment);
    var connectionString = builder.Configuration.GetConnectionString(TinhteDatabase.ConnectionStringName)
        ?? throw new MigrationException("ConnectionStrings:Tinhte is not configured.");
    await using var dataSource = NpgsqlDataSource.Create(connectionString);
    var runner = new MigrationRunner(dataSource);
    var scripts = MigrationRunner.Discover(options.MigrationsDirectory, options.DevBaseline);

    switch (command)
    {
        case "status":
            foreach (var s in await runner.StatusAsync(scripts))
            {
                Console.WriteLine($"{s.Version}  {s.State,-16} {s.AppliedAt?.ToString("u") ?? "-",-22} {s.Name}");
            }

            break;

        case "up":
            var applied = await runner.UpAsync(scripts, Console.WriteLine);
            Console.WriteLine(applied.Count == 0 ? "database is up to date" : $"applied {applied.Count} migration(s)");
            var registry = SiteRegistry.Load(options.SiteRegistryPath, "production");
            await runner.RunInTransactionAsync(SiteDbSync.Generate(registry.Document.Sites));
            Console.WriteLine($"synced tt.sites ({registry.Document.Sites.Count} site(s))");
            break;

        case "grant-role":
            var c = builder.Configuration;
            Console.WriteLine(await StaffBootstrap.GrantRoleAsync(dataSource, c["issuer"], c["subject"], c["role"], c["reason"], c["name"]));
            break;

        default:
            Console.Error.WriteLine($"unknown command '{command}' (use: up | status | grant-role)");
            return 2;
    }

    return 0;
}
catch (MigrationException e)
{
    Console.Error.WriteLine("migration failed: " + e.Message);
    return 1;
}
catch (NpgsqlException e)
{
    // Lỗi kết nối/xác thực là lỗi cấu hình thường gặp: báo gọn một dòng thay vì stack trace.
    var hint = (e as PostgresException)?.SqlState switch
    {
        "28P01" => " — check the password in ConnectionStrings__Tinhte (case-sensitive).",
        "3D000" => " — the database does not exist; create it first (README: Database).",
        "28000" => " — the role cannot log in; check the Username in ConnectionStrings__Tinhte.",
        _ => " — check that PostgreSQL is running and ConnectionStrings__Tinhte (Host/Port) is correct.",
    };
    Console.Error.WriteLine("cannot connect to the database: " + ((e as PostgresException)?.MessageText ?? e.Message) + hint);
    return 1;
}

namespace Tinhte.DbMigrator
{
    internal sealed record MigratorOptions(string MigrationsDirectory, string? DevBaseline, string SiteRegistryPath)
    {
        public static MigratorOptions From(IConfiguration configuration, IHostEnvironment env)
        {
            var baseline = configuration["Database:Migrations:DevBaseline"];
            // Stub schema 04 chỉ để dựng DB dev/test; tuyệt đối không áp lên staging/production (schema 04 thật do DBA cung cấp).
            if (!string.IsNullOrWhiteSpace(baseline) && !env.IsDevelopment())
            {
                throw new MigrationException("Database:Migrations:DevBaseline is only allowed in the Development environment.");
            }

            return new MigratorOptions(
                ContentPaths.Resolve(configuration["Database:Migrations:Directory"] ?? "doc/07_AI_REVIEW/v0.3/db/migrations", env, directory: true),
                string.IsNullOrWhiteSpace(baseline) ? null : ContentPaths.Resolve(baseline, env),
                ContentPaths.Resolve(configuration["Sites:RegistryPath"] ?? "config/sites/sites.json", env));
        }
    }
}
