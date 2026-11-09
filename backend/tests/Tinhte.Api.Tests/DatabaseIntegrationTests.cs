using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Platform.Data;
using Tinhte.Platform.Data.Migrations;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Flags;
using Tinhte.Platform.Sites;

namespace Tinhte.Api.Tests;

/// <summary>
/// Dựng DB sạch một lần cho nhóm test: xóa schema tt/tt_migration/tt_meta → migrate (stub schema 04 + v0.3) → đồng bộ tt.sites.
/// CẢNH BÁO: xóa dữ liệu của DB trỏ tới bởi TT_TEST_PG — chỉ dùng DB dành riêng cho test.
/// </summary>
public sealed class MigratedDatabaseFixture : IAsyncLifetime
{
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable(DbFactAttribute.EnvVar) ?? "";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public IReadOnlyList<MigrationScript> Scripts { get; } = MigrationRunner.Discover(
        RepoPaths.File("doc", "07_AI_REVIEW", "v0.3", "db", "migrations"),
        RepoPaths.File("doc", "07_AI_REVIEW", "v0.3", "db", "tests", "baseline_04_stub.sql"));

    public IReadOnlyList<string> AppliedOnFirstRun { get; private set; } = [];

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await using (var connection = await DataSource.OpenConnectionAsync())
        await using (var drop = new NpgsqlCommand("DROP SCHEMA IF EXISTS tt CASCADE; DROP SCHEMA IF EXISTS tt_migration CASCADE; DROP SCHEMA IF EXISTS tt_meta CASCADE;", connection))
        {
            await drop.ExecuteNonQueryAsync();
        }

        var runner = new MigrationRunner(DataSource);
        AppliedOnFirstRun = await runner.UpAsync(Scripts);
        var registry = SiteRegistry.Load(RepoPaths.File("config", "sites", "sites.json"), "production");
        await runner.RunInTransactionAsync(SiteDbSync.Generate(registry.Document.Sites));
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? default : (T)value;
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<MigratedDatabaseFixture>
{
    public const string Name = "database";
}

/// <summary>WBS-013/014 trên PostgreSQL thật: migration runner, ánh xạ lỗi từ trigger/CHECK, transaction, idempotency đầu-cuối.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class DatabaseIntegrationTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client() => factory.WithProbes(db.ConnectionString).Client();

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    // ---------------------------------------------------------------- migration runner

    [DbFact]
    public async Task Fresh_database_applies_baseline_and_every_v03_migration_then_is_idempotent()
    {
        Assert.Equal(db.Scripts.Select(s => s.Version), db.AppliedOnFirstRun);
        var runner = new MigrationRunner(db.DataSource);
        Assert.Empty(await runner.UpAsync(db.Scripts));
        Assert.All(await runner.StatusAsync(db.Scripts), s => Assert.Equal(MigrationState.Applied, s.State));
    }

    [DbFact]
    public async Task Edited_applied_migration_is_detected_and_blocks_up()
    {
        var edited = db.Scripts.Select(s => s.Version == "0309" ? s with { Checksum = MigrationRunner.ComputeChecksum(s.Sql + "\n-- edited") } : s).ToList();
        var runner = new MigrationRunner(db.DataSource);
        Assert.Contains(await runner.StatusAsync(edited), s => s is { Version: "0309", State: MigrationState.ChecksumMismatch });
        var error = await Assert.ThrowsAsync<MigrationException>(() => runner.UpAsync(edited));
        Assert.Contains("0309", error.Message, StringComparison.Ordinal);
    }

    [DbFact]
    public async Task Sites_table_is_synced_from_the_registry()
    {
        var registry = SiteRegistry.Load(RepoPaths.File("config", "sites", "sites.json"), "production");
        var keys = await db.ScalarAsync<string>("SELECT string_agg(key, ',' ORDER BY key) FROM tt.sites WHERE status <> 'retired'");
        Assert.Equal(string.Join(',', registry.Document.Sites.Select(s => s.Key).Order(StringComparer.Ordinal)), keys);
    }

    [DbFact]
    public async Task Production_flag_defaults_are_off_after_migration()
    {
        // CLAUDE.md §3: không tự bật cờ public. Chỉ cờ có productionDefault=true (community.moderation, công cụ staff) được ON.
        var enabled = await db.ScalarAsync<string>("SELECT string_agg(key, ',' ORDER BY key) FROM tt.feature_gates WHERE enabled");
        Assert.Equal(string.Join(',', FeatureFlagKeys.All.Where(FeatureFlagKeys.ProductionDefault).Order(StringComparer.Ordinal)), enabled);
    }

    // ---------------------------------------------------------------- transaction + lỗi DB

    [DbFact]
    public async Task Transaction_sets_actor_and_rolls_back_on_exception()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Tinhte"] = db.ConnectionString }).Build();
        await using var database = new TinhteDatabase(config);
        var actor = Guid.NewGuid();

        var seen = await database.InTransactionAsync(actor, async (c, tx) =>
        {
            await using var command = new NpgsqlCommand("SELECT current_setting('tt.actor_id', true)", c, tx);
            return (string?)await command.ExecuteScalarAsync();
        });
        Assert.Equal(actor.ToString(), seen);

        var slug = "rollback-" + Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InTransactionAsync<int>(actor, async (c, tx) =>
        {
            await using var insert = new NpgsqlCommand($"INSERT INTO tt.categories (slug, name) VALUES ('{slug}', 'x')", c, tx);
            await insert.ExecuteNonQueryAsync();
            throw new InvalidOperationException("abort");
        }));
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.categories WHERE slug = '{slug}'"));
        Assert.True(await database.PingAsync());
    }

    [DbFact]
    public async Task Trigger_domain_error_becomes_contract_problem()
    {
        using var client = Client();
        using var response = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/db/comment"));
        var body = await ProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.FeatureDisabled);
        Assert.Contains("community.video_comments", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [DbFact]
    public async Task Check_violation_becomes_422_validation_failed()
    {
        using var client = Client();
        using var response = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/db/check-violation"));
        var body = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        Assert.DoesNotContain("INSERT", body.GetRawText(), StringComparison.OrdinalIgnoreCase); // không lộ SQL
    }

    [DbFact]
    public async Task Ready_checks_the_database()
    {
        using var client = Client();
        var body = await client.GetFromJsonAsync<JsonElement>("/health/ready");
        Assert.Equal("ok", body.GetProperty("checks").GetProperty("database").GetString());
    }

    // ---------------------------------------------------------------- idempotency đầu-cuối

    [DbFact]
    public async Task Replaying_the_same_request_returns_the_original_response_without_rerunning()
    {
        using var client = Client();
        var key = "create-" + Guid.NewGuid();
        using var first = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"hello","priority":1}""", user: "u1", idempotencyKey: key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var calls = ContractProbeController.CreateCalls;

        // Cùng nội dung, khác thứ tự trường → vẫn là cùng request.
        using var replay = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{ "priority": 1, "text": "hello" }""", user: "u1", idempotencyKey: key));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(first.Headers.ETag, replay.Headers.ETag);
        Assert.Equal(calls, ContractProbeController.CreateCalls); // action không chạy lại
    }

    [DbFact]
    public async Task Same_key_with_different_payload_is_409_idempotency_key_reused()
    {
        using var client = Client();
        var key = "reuse-" + Guid.NewGuid();
        using var first = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"hello"}""", user: "u1", idempotencyKey: key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var second = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"other"}""", user: "u1", idempotencyKey: key));
        await ProblemAsync(second, HttpStatusCode.Conflict, ErrorCodes.IdempotencyKeyReused);
    }

    [DbFact]
    public async Task Keys_are_scoped_per_actor()
    {
        using var client = Client();
        var key = "actor-" + Guid.NewGuid();
        using var a = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"hello"}""", user: "alice", idempotencyKey: key));
        using var b = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"hello"}""", user: "bob", idempotencyKey: key));
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.False(b.Headers.Contains("Idempotent-Replayed"));
        Assert.NotEqual(await a.Content.ReadAsStringAsync(), await b.Content.ReadAsStringAsync());
    }

    [DbFact]
    public async Task Failed_requests_release_the_key_so_the_client_can_fix_and_retry()
    {
        using var client = Client();
        var key = "fix-" + Guid.NewGuid();
        using var invalid = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"x"}""", user: "u1", idempotencyKey: key));
        await ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        using var fixedRequest = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"fixed"}""", user: "u1", idempotencyKey: key));
        Assert.Equal(HttpStatusCode.Created, fixedRequest.StatusCode);

        var failKey = "boom-" + Guid.NewGuid();
        using var boom = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes/fail", "{}", user: "u1", idempotencyKey: failKey));
        await ProblemAsync(boom, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.idempotency_keys WHERE idempotency_key = '{failKey}'"));
    }

    [DbFact]
    public async Task Request_in_progress_with_same_key_is_409_with_retry_after()
    {
        var key = "busy-" + Guid.NewGuid();
        // Mô phỏng request đầu đang chạy: bản ghi status_code NULL, vừa tạo.
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tt.idempotency_keys (actor_key, http_method, route_template, idempotency_key, request_hash)
            VALUES ('u1', 'POST', 'www api/v1/_contract/notes', @key, repeat('0', 64))
            """, connection))
        {
            insert.Parameters.AddWithValue("key", key);
            await insert.ExecuteNonQueryAsync();
        }

        using var client = Client();
        using var response = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"hello"}""", user: "u1", idempotencyKey: key));
        // Khóa đang chạy nhưng cho request khác (hash khác) → vẫn là dùng lại khóa cho request khác.
        await ProblemAsync(response, HttpStatusCode.Conflict, ErrorCodes.IdempotencyKeyReused);

        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var sameHash = new NpgsqlCommand("UPDATE tt.idempotency_keys SET request_hash = @hash WHERE idempotency_key = @key", connection))
        {
            sameHash.Parameters.AddWithValue("hash", await HashOf("/api/v1/_contract/notes", """{"text":"hello"}"""));
            sameHash.Parameters.AddWithValue("key", key);
            await sameHash.ExecuteNonQueryAsync();
        }

        using var busy = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"hello"}""", user: "u1", idempotencyKey: key));
        await ProblemAsync(busy, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        Assert.Equal("1", busy.Headers.GetValues("Retry-After").Single());
    }

    private static async Task<string> HashOf(string path, string json)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return await Tinhte.Platform.Mvc.RequestHasher.HashAsync(context.Request, CancellationToken.None);
    }
}
