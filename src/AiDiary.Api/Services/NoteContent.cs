using System.Text;
using System.Text.RegularExpressions;

namespace AiDiary.Api.Services;

/// <summary>
/// Builds the decrypted .md file content (frontmatter + body) and splits it
/// again on read. Postgres is authoritative for metadata; the frontmatter is
/// a portability convenience for Obsidian/Pandoc/Hugo and for the decryptors.
/// </summary>
public static partial class NoteContent
{
    /// <summary>Escape a body whose first line is "---" so it is never parsed as frontmatter.</summary>
    public static string EscapeBody(string body)
    {
        var normalized = Normalize(body);
        return normalized.StartsWith("---\n", StringComparison.Ordinal)
            ? "\\---" + normalized[3..]
            : normalized;
    }

    /// <summary>UTF-8 semantics: LF line endings, no trailing blank lines, exactly one trailing newline.</summary>
    public static string Normalize(string body)
    {
        var text = body.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, "[ \\t]+\\n", "\n");
        text = text.TrimEnd('\n');
        return text.Length == 0 ? "" : text + "\n";
    }

    public static string FirstLine(string body)
    {
        var line = body.Replace("\r\n", "\n").Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] : line;
    }

    public static string Slug(string title)
    {
        var slug = SlugRegex().Replace(title.ToLowerInvariant().Trim(), "-");
        slug = MultipleDashRegex().Replace(slug, "-").Trim('-');
        if (slug.Length > 60)
        {
            slug = slug[..60].TrimEnd('-');
        }
        return slug.Length == 0 ? "note" : slug;
    }

    public static string ExportFilename(DateOnly entryDate, string title) =>
        $"{entryDate:yyyy-MM-dd}-{Slug(title)}.md";

    public static string BuildFrontmatter(NoteDto dto, int version)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {dto.Id}");
        sb.AppendLine($"title: {YamlScalar(dto.Title)}");
        sb.AppendLine($"entry_date: {dto.EntryDate:yyyy-MM-dd}");
        sb.AppendLine($"created_at: {dto.CreatedAt:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        sb.AppendLine($"timezone: {dto.Timezone}");
        if (dto.Location is { } location)
        {
            sb.AppendLine("location:");
            sb.AppendLine($"  lat: {location.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"  lon: {location.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrWhiteSpace(location.Label))
            {
                sb.AppendLine($"  label: {YamlScalar(location.Label)}");
            }
        }
        sb.AppendLine($"version: {version}");
        sb.AppendLine($"origin: {dto.Origin}");
        sb.AppendLine("---");
        return sb.ToString();
    }

    public static string BuildFile(NoteDto dto, int version, string body) =>
        BuildFrontmatter(dto, version) + "\n" + EscapeBody(body);

    /// <summary>
    /// Splits frontmatter from body. On any parse failure, logs and returns
    /// the whole content as the body — metadata still comes from Postgres.
    /// </summary>
    public static (string Frontmatter, string Body) Split(string content, Action<string>? onParseFailure = null)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            return ("", content);
        }
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---")
            {
                var fm = string.Join('\n', lines[..(i + 1)]);
                var body = string.Join('\n', lines[(i + 1)..]);
                // Unescape the guarded first line back to a literal "---".
                if (body.StartsWith("\\---", StringComparison.Ordinal))
                {
                    body = body[1..];
                }
                return (fm, body);
            }
        }
        onParseFailure?.Invoke("Frontmatter delimiter was opened but never closed; treating content as body.");
        return ("", content);
    }

    private static string YamlScalar(string value)
    {
        if (value.Contains('\n') || value.Contains('"') || value.Contains(':'))
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        return value;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"-+")]
    private static partial Regex MultipleDashRegex();
}

public sealed record NoteLocation(double Lat, double Lon, string? Label);

public sealed class NoteDto
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required DateOnly EntryDate { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Timezone { get; init; }
    public NoteLocation? Location { get; init; }
    public required string Origin { get; init; }
}
