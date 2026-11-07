import { useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router-dom";
import type { NoteListItem } from "../api/types";
import { api } from "../api/client";
import { ConfirmDialog } from "./ConfirmDialog";

interface Props {
  selectedId: string | null;
  onSelect: (id: string) => void;
  onCreate: (id: string) => void;
  onDeleted: (id: string) => void;
}

interface Group {
  label: string;
  notes: NoteListItem[];
}

function groupLabel(entryDate: string): string {
  const today = new Date();
  const yesterday = new Date();
  yesterday.setDate(today.getDate() - 1);
  const iso = (d: Date) => d.toISOString().slice(0, 10);
  if (entryDate === iso(today)) return "Today";
  if (entryDate === iso(yesterday)) return "Yesterday";
  return entryDate;
}

export function Sidebar({ selectedId, onSelect, onCreate, onDeleted }: Props) {
  const [notes, setNotes] = useState<NoteListItem[]>([]);
  const [error, setError] = useState("");
  const [pendingDelete, setPendingDelete] = useState<NoteListItem | null>(null);
  const [creating, setCreating] = useState(false);
  // Ref guard blocks rapid repeat clicks that land before the disabled state re-renders.
  const creatingRef = useRef(false);

  const refresh = async () => {
    try {
      setNotes(await api.get<NoteListItem[]>("/api/notes"));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not load notes.");
    }
  };

  useEffect(() => {
    void refresh();
    const onListChanged = () => void refresh();
    window.addEventListener("ai-diary:notes-changed", onListChanged);
    return () => window.removeEventListener("ai-diary:notes-changed", onListChanged);
  }, []);

  const groups = useMemo<Group[]>(() => {
    const map = new Map<string, NoteListItem[]>();
    for (const note of notes) {
      const key = groupLabel(note.entryDate);
      const list = map.get(key) ?? [];
      list.push(note);
      map.set(key, list);
    }
    return [...map.entries()].map(([label, list]) => ({ label, notes: list }));
  }, [notes]);

  const createNote = async () => {
    if (creatingRef.current) return;
    creatingRef.current = true;
    setCreating(true);
    try {
      const detail = await api.post<{ id: string }>("/api/notes", {
        body: "",
        title: "",
        timezone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC",
      });
      window.dispatchEvent(new CustomEvent("ai-diary:notes-changed"));
      onCreate(detail.id);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not create a note.");
    } finally {
      creatingRef.current = false;
      setCreating(false);
    }
  };

  const deleteNote = async (id: string) => {
    try {
      await api.delete(`/api/notes/${id}`);
      setNotes((prev) => prev.filter((n) => n.id !== id));
      onDeleted(id);
      window.dispatchEvent(new CustomEvent("ai-diary:notes-changed"));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not delete the note.");
    }
  };

  return (
    <nav className="sidebar">
      {error && <p className="error-text">{error}</p>}
      <div className="note-list">
        <button
          type="button"
          className="new-note-button"
          onClick={() => void createNote()}
          disabled={creating}
          aria-busy={creating}
        >
          <span className="new-note-icon">＋</span>
          {creating ? "Creating…" : "New note"}
        </button>
        {groups.map((group) => (
          <section key={group.label}>
            <h3 className="group-label">{group.label}</h3>
            {group.notes.map((note) => (
              <button
                key={note.id}
                type="button"
                className={`note-item ${note.id === selectedId ? "selected" : ""}`}
                onClick={() => onSelect(note.id)}
                onContextMenu={(e) => {
                  e.preventDefault();
                  setPendingDelete(note);
                }}
              >
                <span className="note-item-title">{note.title || "Untitled"}</span>
                {note.origin === "agent" && <span className="origin-tag">agent</span>}
              </button>
            ))}
          </section>
        ))}
        {groups.length === 0 && <p className="muted">No notes yet.</p>}
      </div>
      <div className="sidebar-footer">
        <Link className="link-button" to="/trash">
          Trash
        </Link>
      </div>
      {pendingDelete && (
        <ConfirmDialog
          title="Move to trash?"
          message={`"${pendingDelete.title || "Untitled"}" will be moved to the trash.`}
          confirmLabel="Move to trash"
          danger
          onConfirm={() => {
            const id = pendingDelete.id;
            setPendingDelete(null);
            void deleteNote(id);
          }}
          onCancel={() => setPendingDelete(null)}
        />
      )}
    </nav>
  );
}
