/**
 * Per-note reading position, kept in localStorage on this device only. Not a
 * cookie: cookies ride along on every request to the API, and where someone
 * is in their diary is nobody else's business. Only note ids and numbers are
 * stored, never content.
 */
const STORAGE_KEY = "ai-diary-scroll-positions";
const MAX_ENTRIES = 300;

export type ScrollMode = "preview" | "edit";

export interface ScrollPosition {
  top: number;
  /** top as a fraction of the scrollable range, used when the mode differs. */
  ratio: number;
  mode: ScrollMode;
  at: number;
}

function readAll(): Record<string, ScrollPosition> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : null;
    return parsed && typeof parsed === "object" ? (parsed as Record<string, ScrollPosition>) : {};
  } catch {
    return {};
  }
}

export function getScrollPosition(noteId: string): ScrollPosition | null {
  const entry = readAll()[noteId];
  return entry && typeof entry.top === "number" ? entry : null;
}

export function setScrollPosition(noteId: string, position: Omit<ScrollPosition, "at">): void {
  const all = readAll();
  all[noteId] = { ...position, at: Date.now() };
  const ids = Object.keys(all);
  if (ids.length > MAX_ENTRIES) {
    ids
      .sort((a, b) => all[a].at - all[b].at)
      .slice(0, ids.length - MAX_ENTRIES)
      .forEach((id) => delete all[id]);
  }
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(all));
  } catch {
    // Storage full or blocked (private mode) — just don't remember.
  }
}
