/**
 * Split server-generated frontmatter from a decrypted note body.
 * Mirrors NoteContent.Split on the server: a plain line-based scan for the
 * closing "---" delimiter, not a YAML parse — metadata is always
 * authoritative in Postgres, the frontmatter is a portability convenience,
 * so it only needs to be located and removed, not understood. (A YAML
 * library, e.g. gray-matter, doesn't run in the browser without a Buffer
 * polyfill; this needs none.)
 */
export function stripFrontmatter(content: string): string {
  if (!content.startsWith("---\n")) return content;
  const lines = content.split("\n");
  for (let i = 1; i < lines.length; i++) {
    if (lines[i].trimEnd() !== "---") continue;
    const rest = lines.slice(i + 1);
    if (rest[0] === "") rest.shift(); // the blank line separating frontmatter from body
    return unescapeFirstLine(rest.join("\n"));
  }
  return content;
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
