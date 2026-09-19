import { useState } from "react";
import type { NoteDetail } from "../api/types";
import { api } from "../api/client";

interface Props {
  detail: NoteDetail;
  onSaved: (detail: NoteDetail) => void;
  onClose: () => void;
}

/** Title, entry date and location live in a collapsible panel, never inline. */
export function MetadataPanel({ detail, onSaved, onClose }: Props) {
  const [title, setTitle] = useState(detail.title);
  const [entryDate, setEntryDate] = useState(detail.entryDate);
  const [label, setLabel] = useState(detail.location?.label ?? "");
  const [clearLocation, setClearLocation] = useState(false);
  const [error, setError] = useState("");
  const [saving, setSaving] = useState(false);

  const save = async () => {
    setSaving(true);
    setError("");
    try {
      const updated = await api.patch<NoteDetail>(`/api/notes/${detail.id}/meta`, {
        title,
        entryDate,
        clearLocation,
        location:
          !clearLocation && label
            ? { lat: detail.location?.lat ?? 0, lon: detail.location?.lon ?? 0, label }
            : null,
      });
      onSaved(updated);
      onClose();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Save failed.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="metadata-panel">
      <div className="panel-header">
        <h3>Note details</h3>
        <button type="button" className="link-button" onClick={onClose}>
          Close
        </button>
      </div>
      <label>
        Title
        <input value={title} onChange={(e) => setTitle(e.target.value)} />
      </label>
      <label>
        Entry date
        <input type="date" value={entryDate} onChange={(e) => setEntryDate(e.target.value)} />
      </label>
      <label>
        Location
        <input value={label} onChange={(e) => setLabel(e.target.value)} placeholder="Kyoto, Japan" />
      </label>
      <label className="checkbox-row">
        <input type="checkbox" checked={clearLocation} onChange={(e) => setClearLocation(e.target.checked)} />
        Remove location
      </label>
      {error && <p className="error-text">{error}</p>}
      <div className="panel-actions">
        <button type="button" className="primary-button" onClick={() => void save()} disabled={saving}>
          {saving ? "Saving…" : "Save"}
        </button>
      </div>
    </div>
  );
}
