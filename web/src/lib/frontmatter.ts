import matter from "gray-matter";

/**
 * Split server-generated frontmatter from a decrypted note body.
 * On parse failure we fall back to the whole content — metadata is always
 * authoritative in Postgres, the frontmatter is a portability convenience.
 */
export function stripFrontmatter(content: string): string {
  if (!content.startsWith("---\n")) return content;
  try {
    const parsed = matter(content);
    if (typeof parsed.content === "string") {
      return unescapeFirstLine(parsed.content);
    }
    return content;
  } catch (error) {
    console.warn("Frontmatter parse failed; using raw content.", error);
    return content;
  }
}

/** Undo the server-side escape of a body that begins with a literal "---". */
function unescapeFirstLine(body: string): string {
  return body.startsWith("\\---") ? body.slice(1) : body;
}

/** Title defaults to the first non-empty line, capped at 120 chars. */
export function firstLineTitle(body: string): string {
  const line = body.split("\n").find((l) => l.trim().length > 0)?.trim() ?? "";
  return line.length > 120 ? line.slice(0, 120) : line;
}
