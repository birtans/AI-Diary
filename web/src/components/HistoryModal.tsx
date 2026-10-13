import { useEffect, useState } from "react";
import type { NoteVersionItem } from "../api/types";
import { api, ApiError } from "../api/client";

interface Props {
  noteId: string;
  currentVersion: number;
  onRestored: () => void;
  onClose: () => void;
}

/**
 * Version history: pick two versions for a rendered diff, or restore one.
 * Versions come from "Save Version" and from the first edit of each new day;
 * restoring replaces the current text, which is itself kept in history.
 */
export function HistoryModal({ noteId, currentVersion, onRestored, onClose }: Props) {
  const [versions, setVersions] = useState<NoteVersionItem[]>([]);
  const [a, setA] = useState<number | null>(null);
  const [b, setB] = useState<number | null>(null);
  const [diffHtml, setDiffHtml] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  useEffect(() => {
    void (async () => {
      try {
        const list = await api.get<NoteVersionItem[]>(`/api/notes/${noteId}/versions`);
        setVersions(list);
        if (list.length >= 2) {
          setA(list[1].versionNo);
          setB(list[0].versionNo);
        }
      } catch (ex) {
        setError(ex instanceof Error ? ex.message : "Could not load versions.");
      } finally {
        setLoading(false);
      }
    })();
  }, [noteId]);

  useEffect(() => {
    if (a === null || b === null) return;
    let cancelled = false;
    setDiffHtml("");
    void (async () => {
      try {
        const result = await api.get<{ html: string }>(`/api/notes/${noteId}/versions/${a}/diff/${b}`);
        if (!cancelled) setDiffHtml(result.html);
      } catch (ex) {
        if (!cancelled) setError(ex instanceof Error ? ex.message : "Diff failed.");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [noteId, a, b]);

  const restore = async (versionNo: number) => {
    if (!window.confirm(`Restore version ${versionNo}? Your current text is kept in history.`)) return;
    try {
      await api.post(`/api/notes/${noteId}/versions/${versionNo}/restore`);
      onRestored();
    } catch (ex) {
      setError(ex instanceof ApiError ? ex.detail : "Restore failed.");
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal history-modal" onClick={(e) => e.stopPropagation()}>
        <div className="panel-header">
          <h3>Version history</h3>
          <button type="button" className="link-button" onClick={onClose}>
            Close
          </button>
        </div>
        {loading ? (
          <p>Loading…</p>
        ) : (
          <>
            <div className="version-pickers">
              <label>
                Compare
                <select value={a ?? ""} onChange={(e) => setA(Number(e.target.value))}>
                  {versions.map((v) => (
                    <option key={v.versionNo} value={v.versionNo}>
                      v{v.versionNo}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                with
                <select value={b ?? ""} onChange={(e) => setB(Number(e.target.value))}>
                  {versions.map((v) => (
                    <option key={v.versionNo} value={v.versionNo}>
                      v{v.versionNo}
                    </option>
                  ))}
                </select>
              </label>
            </div>
            {error && <p className="error-text">{error}</p>}
            <div
              className="diff-output"
              // The server renders and HTML-escapes every line; tags are fixed.
              dangerouslySetInnerHTML={{ __html: diffHtml }}
            />
            <ul className="version-list">
              {versions.map((v) => (
                <li key={v.versionNo}>
                  <span className="version-no">v{v.versionNo}</span>
                  <span className="version-meta">
                    {new Date(v.createdAt).toLocaleString()} · {formatBytes(v.byteSize)}
                    {v.versionNo === currentVersion && " · current"}
                  </span>
                  {v.versionNo !== currentVersion && (
                    <button type="button" className="link-button" onClick={() => void restore(v.versionNo)}>
                      Restore
                    </button>
                  )}
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
    </div>
  );
}

function formatBytes(size: number): string {
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / (1024 * 1024)).toFixed(1)} MB`;
}
