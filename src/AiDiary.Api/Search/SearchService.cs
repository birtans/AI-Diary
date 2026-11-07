using System.Globalization;
using System.Text;
using AiDiary.Api.Contracts;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using AiDiary.Api.Embeddings;
using AiDiary.Api.Services;
using AiDiary.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AiDiary.Api.Search;

public sealed class SearchService(
    AppDbContext db,
    OllamaEmbeddingService embeddings,
    ObjectStorage storage,
    KeyHierarchyService crypto)
{
    private const double MinScore = 0.35;
    private const int MaxExcerptChars = 400;

    private sealed class Hit
    {
        public Guid NoteId { get; set; }
        public int CharStart { get; set; }
        public int CharEnd { get; set; }
        public double Score { get; set; }
    }

    public async Task<List<SearchResultItem>> SearchAsync(Guid userId, SearchRequest request, CancellationToken ct = default)
    {
        var limit = Math.Clamp(request.Limit ?? 8, 1, 50);
        var queryVector = await embeddings.EmbedAsync(request.Query, ct);

        var q = new NpgsqlParameter("q", NpgsqlDbType.Text)
        {
            Value = ToVectorString(queryVector),
        };
        var uid = new NpgsqlParameter("uid", NpgsqlDbType.Uuid) { Value = userId };
        var from = new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = (object?)request.DateFrom ?? DBNull.Value };
        var to = new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = (object?)request.DateTo ?? DBNull.Value };
        var lim = new NpgsqlParameter("lim", NpgsqlDbType.Integer) { Value = limit };

        // A note can have several parts: rank each note by its best part, so
        // one long note can't take every result slot. Exact scan per user;
        // at this scale it beats a post-filtered HNSW scan on recall.
        var sql = """
            SELECT best."NoteId", best."CharStart", best."CharEnd", best."Score"
            FROM (
                SELECT DISTINCT ON (ne."NoteId")
                    ne."NoteId" AS "NoteId", ne."CharStart" AS "CharStart", ne."CharEnd" AS "CharEnd",
                    1 - (ne."Embedding" <=> @q::vector) AS "Score"
                FROM note_embeddings ne
                JOIN notes n ON n."Id" = ne."NoteId" AND n."DeletedAt" IS NULL
                WHERE ne."UserId" = @uid
                  AND ne."Embedding" IS NOT NULL
                  AND (@from IS NULL OR n."EntryDate" >= @from)
                  AND (@to IS NULL OR n."EntryDate" <= @to)
                ORDER BY ne."NoteId", ne."Embedding" <=> @q::vector
            ) best
            ORDER BY best."Score" DESC
            LIMIT @lim
            """;

        var hits = await db.Database.SqlQueryRaw<Hit>(sql, q, uid, from, to, lim).ToListAsync(ct);

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);

        var results = new List<SearchResultItem>();
        foreach (var hit in hits.Where(h => h.Score >= MinScore))
        {
            var note = await db.Notes.AsNoTracking()
                .Include(n => n.Versions)
                .FirstOrDefaultAsync(n => n.Id == hit.NoteId && n.UserId == userId, ct);
            if (note is null)
            {
                continue;
            }
            var title = crypto.DecryptText(ukek, note.TitleEnc);
            var excerpt = await ExcerptAsync(ukek, note, hit.CharStart, hit.CharEnd, ct);
            results.Add(new SearchResultItem(note.Id, note.EntryDate, title, excerpt, Math.Round(hit.Score, 4)));
        }
        return results;
    }

    /// <summary>Excerpt from the matching part; falls back to the body start if the body has since shrunk.</summary>
    private async Task<string> ExcerptAsync(byte[] ukek, Note note, int charStart, int charEnd, CancellationToken ct)
    {
        try
        {
            var version = note.Versions.FirstOrDefault(v => v.VersionNo == note.CurrentVersion);
            if (version is null)
            {
                return "";
            }
            var dek = crypto.UnwrapDek(ukek, note.WrappedDek);
            var file = await storage.GetAsync(version.S3Key, ct);
            var plaintext = AiDiary.Crypto.FileEnvelope.Open(file, dek);
            var (_, body) = NoteContent.Split(Encoding.UTF8.GetString(plaintext));
            if (charStart > 0 && charStart < charEnd && charEnd <= body.Length)
            {
                body = body[charStart..charEnd];
            }
            var flattened = string.Join(" ", body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            if (flattened.Length <= MaxExcerptChars)
            {
                return flattened;
            }
            var cut = flattened[..MaxExcerptChars];
            var lastSpace = cut.LastIndexOf(' ');
            return (lastSpace > 100 ? cut[..lastSpace] : cut) + "…";
        }
        catch
        {
            return "";
        }
    }

    private static string ToVectorString(float[] vector) =>
        "[" + string.Join(",", vector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
}
