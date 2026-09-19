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
  const [timezone, setTimezone] = useState(detail.timezone);
  const [lat, setLat] = useState(detail.location ? String(detail.location.lat) : "");
  const [lon, setLon] = useState(detail.location ? String(detail.location.lon) : "");
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
        timezone,
        clearLocation,
        location:
          !clearLocation && (lat || lon)
            ? { lat: Number(lat) || 0, lon: Number(lon) || 0, label: label || null }
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
        Timezone
        <input value={timezone} onChange={(e) => setTimezone(e.target.value)} placeholder="Europe/Amsterdam" />
      </label>
      <div className="location-row">
        <label>
          Latitude
          <input inputMode="decimal" value={lat} onChange={(e) => setLat(e.target.value)} />
        </label>
        <label>
          Longitude
          <input inputMode="decimal" value={lon} onChange={(e) => setLon(e.target.value)} />
        </label>
      </div>
      <label>
        Place label
        <input value={label} onChange={(e) => setLabel(e.target.value)} />
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
