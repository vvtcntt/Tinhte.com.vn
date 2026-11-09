using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;

namespace Tinhte.Api.Tests.Support;

/// <summary>Controller chỉ dùng trong test để kiểm hợp đồng chung WBS-014: phân trang cursor, ETag/If-Match, idempotency.</summary>
[Route(ApiRoutes.Prefix + "/_contract")]
public sealed class ContractProbeController(ICursorCodec cursors, ITinhteDatabase database) : TinhteControllerBase
{
    public sealed record Item(Guid Id, string Title, DateTimeOffset PublishedAt);

    public sealed record ItemPosition(DateTimeOffset PublishedAt, Guid Id);

    public sealed record Note(Guid Id, string Text, long RowVersion);

    public sealed record CreateNote([Required, StringLength(50, MinimumLength = 2)] string Text, [Range(0, 10)] int Priority = 0);

    public sealed record EditNote([Required] string Text);

    /// <summary>25 bài giả, sắp theo (publishedAt DESC, id DESC) — giống feed thật.</summary>
    public static readonly IReadOnlyList<Item> Items = Enumerable.Range(1, 25)
        .Select(i => new Item(new Guid(i, 0, 0, new byte[8]), $"Item {i}", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i / 2)))
        .OrderByDescending(x => x.PublishedAt).ThenByDescending(x => x.Id)
        .ToList();

    private static readonly Dictionary<Guid, Note> Notes = new() { [Guid.Parse("00000000-0000-0000-0000-000000000003")] = new(Guid.Parse("00000000-0000-0000-0000-000000000003"), "hello", 3) };

    public static int CreateCalls;

    [HttpGet("items")]
    public ActionResult<CursorPage<Item>> List([FromQuery] PageRequest page)
    {
        var scope = CursorScope("items");
        IEnumerable<Item> query = Items;
        if (page.Cursor is { } cursor)
        {
            var after = cursors.Decode<ItemPosition>(scope, cursor);
            query = query.Where(x => x.PublishedAt < after.PublishedAt || (x.PublishedAt == after.PublishedAt && x.Id.CompareTo(after.Id) < 0));
        }

        return Ok(query.Take(page.Limit + 1).ToList().ToPage(page.Limit, cursors, scope, x => new ItemPosition(x.PublishedAt, x.Id)));
    }

    [HttpGet("other-items")]
    public ActionResult<CursorPage<Item>> OtherList([FromQuery] PageRequest page)
    {
        if (page.Cursor is { } cursor) cursors.Decode<ItemPosition>(CursorScope("other-items"), cursor);
        return Ok(new CursorPage<Item>([], null));
    }

    [HttpGet("notes/{id:guid}")]
    public ActionResult<Note> GetNote(Guid id)
    {
        if (!Notes.TryGetValue(id, out var note)) return Problem(ErrorCodes.NotFound, "Not found", "Note not found");
        SetETag(note.RowVersion);
        return Ok(note);
    }

    [HttpPatch("notes/{id:guid}")]
    public ActionResult<Note> EditNoteAction(Guid id, EditNote request)
    {
        if (!Notes.TryGetValue(id, out var note)) return Problem(ErrorCodes.NotFound, "Not found", "Note not found");
        var expected = RequireIfMatch();
        ETags.EnsureMatches(note.RowVersion, expected);
        // Probe không ghi đè trạng thái dùng chung để các test chạy song song không ảnh hưởng nhau.
        var updated = note with { Text = request.Text, RowVersion = note.RowVersion + 1 };
        SetETag(updated.RowVersion);
        return Ok(updated);
    }

    [HttpPost("notes")]
    [Idempotent]
    public ActionResult<Note> CreateNoteAction(CreateNote request)
    {
        Interlocked.Increment(ref CreateCalls);
        var note = new Note(Guid.NewGuid(), request.Text, 1);
        SetETag(note.RowVersion);
        return CreatedAtAction(nameof(GetNote), new { id = note.Id }, note);
    }

    [HttpPost("notes/fail")]
    [Idempotent]
    public IActionResult FailingCreate() => throw new InvalidOperationException("boom after side effects were rolled back");

    [HttpGet("domain-error")]
    public IActionResult DomainError([FromQuery] string code) => throw new DomainProblemException(code, "Probe", "probe detail");

    /// <summary>Kích hoạt trigger thật của migration 0307: bình luận khi community.video_comments OFF.</summary>
    [HttpPost("db/comment")]
    public async Task<IActionResult> InsertComment(CancellationToken cancellationToken)
    {
        await database.InTransactionAsync(Guid.NewGuid(), async (connection, tx) =>
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO tt.comments (video_id, author_id, text) VALUES (gen_random_uuid(), gen_random_uuid(), 'x')", connection, tx);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
        return NoContent();
    }

    /// <summary>Vi phạm CHECK thật (idempotency_key ngắn hơn 8 ký tự) → 422 VALIDATION_FAILED.</summary>
    [HttpPost("db/check-violation")]
    public async Task<IActionResult> CheckViolation(CancellationToken cancellationToken)
    {
        await database.InTransactionAsync(null, async (connection, tx) =>
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO tt.idempotency_keys (actor_key, http_method, route_template, idempotency_key, request_hash) VALUES ('a', 'POST', 'r', 'short', repeat('0', 64))",
                connection, tx);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
        return NoContent();
    }
}
