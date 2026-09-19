import { putBuffer, clearBuffer } from "./indexeddb";

/** Flush the IndexedDB mirror on pagehide / visibilitychange. */
export function installCrashFlush(getCurrent: () => { noteId: string; body: string } | null): () => void {
  let lastFlush = "";

  const flush = () => {
    const current = getCurrent();
    if (!current) return;
    const key = current.noteId + "|" + current.body;
    if (key === lastFlush) return;
    lastFlush = key;
    void putBuffer(current.noteId, current.body);
  };

  const onVisibility = () => {
    if (document.visibilityState === "hidden") flush();
  };
  window.addEventListener("pagehide", flush);
  document.addEventListener("visibilitychange", onVisibility);
  return () => {
    window.removeEventListener("pagehide", flush);
    document.removeEventListener("visibilitychange", onVisibility);
  };
}

/** Mark a note as cleanly saved so a stale mirror is not offered again. */
export function markSaved(noteId: string): void {
  void clearBuffer(noteId);
}
