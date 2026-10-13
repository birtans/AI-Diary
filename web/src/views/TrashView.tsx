import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import type { NoteListItem } from "../api/types";
import { api } from "../api/client";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { clearBuffer } from "../lib/indexeddb";

/** Soft-deleted notes; the reaper purges them 30 days after deletion. */
export function TrashView() {
  const [items, setItems] = useState<NoteListItem[]>([]);
  const [error, setError] = useState("");
  const [pendingPurge, setPendingPurge] = useState<NoteListItem | null>(null);
  const navigate = useNavigate();

  const refresh = async () => {
    try {
      setItems(await api.get<NoteListItem[]>("/api/notes?deleted=true"));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not load the trash.");
    }
  };

  useEffect(() => {
    void refresh();
  }, []);

  const restore = async (id: string) => {
    try {
      await api.post(`/api/notes/${id}/restore`);
      await refresh();
      window.dispatchEvent(new CustomEvent("ai-diary:notes-changed"));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Restore failed.");
    }
  };

  const purge = async (id: string) => {
    try {
      await api.delete(`/api/notes/${id}/permanent`);
      await clearBuffer(id);
      await refresh();
      window.dispatchEvent(new CustomEvent("ai-diary:notes-changed"));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Delete failed.");
    }
  };

  return (
    <div className="page">
      <h2>Trash</h2>
      <p className="muted">Notes are kept here for 30 days after deletion, then permanently removed.</p>
      {error && <p className="error-text">{error}</p>}
      {items.length === 0 && <p className="muted">Trash is empty.</p>}
      <ul className="trash-list">
        {items.map((item) => (
          <li key={item.id}>
            <span>{item.title || "Untitled"}</span>
            <span className="muted">
              {item.entryDate}
              {item.deletedAt ? ` · deleted ${new Date(item.deletedAt).toLocaleDateString()}` : ""}
            </span>
            <span className="list-actions">
              <button type="button" className="link-button" onClick={() => void restore(item.id)}>
                Restore
              </button>
              <button
                type="button"
                className="link-button"
                onClick={() => void navigate(`/?note=${item.id}`)}
              >
                Open
              </button>
              <button type="button" className="link-button danger" onClick={() => setPendingPurge(item)}>
                Delete permanently
              </button>
            </span>
          </li>
        ))}
      </ul>
      <p>
        <Link className="link-button" to="/">
          ← Back to notes
        </Link>
      </p>
      {pendingPurge && (
        <ConfirmDialog
          title="Delete permanently?"
          message={`"${pendingPurge.title || "Untitled"}" and its entire history will be deleted forever. This cannot be undone.`}
          confirmLabel="Delete permanently"
          danger
          onConfirm={() => {
            const id = pendingPurge.id;
            setPendingPurge(null);
            void purge(id);
          }}
          onCancel={() => setPendingPurge(null)}
        />
      )}
    </div>
  );
}
