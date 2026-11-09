import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeSanitize from "rehype-sanitize";
import type { NoteDetail } from "../api/types";
import { api, ApiError, fetchPresigned } from "../api/client";
import { decryptNoteBody } from "../lib/crypto";
import { stripFrontmatter } from "../lib/frontmatter";
import { getBuffer, putBuffer } from "../lib/indexeddb";
import { markSaved } from "../lib/crashFlush";
import { rehypeSourceOffsets, sourceRangeAt } from "../lib/sourceOffsets";
import { EditorTextarea, type EditorTextareaHandle } from "./EditorTextarea";
import { FormatToolbar } from "./FormatToolbar";
import { HistoryModal } from "./HistoryModal";

interface Props {
  noteId: string | null;
  startInEditMode?: boolean;
  onNoteChanged: (detail: NoteDetail | null) => void;
}

/**
 * Right pane: plain textarea for writing, sanitised markdown preview.
 * Debounced autosave (1.5 s idle, on blur, on Cmd/Ctrl-S), IndexedDB mirror,
 * and optimistic concurrency handling.
 */
export function EditorPane({ noteId, startInEditMode, onNoteChanged }: Props) {
  const [detail, setDetail] = useState<NoteDetail | null>(null);
  const [body, setBody] = useState("");
  const [lastSavedBody, setLastSavedBody] = useState("");
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [isPreview, setIsPreview] = useState(true);
  const [showHistory, setShowHistory] = useState(false);
  const [conflict, setConflict] = useState<{ message: string; currentRevision: string } | null>(null);
  const [savingVersion, setSavingVersion] = useState(false);
  const [versionNotice, setVersionNotice] = useState("");
  const [mirrorOffer, setMirrorOffer] = useState<{ mirror: string; server: string } | null>(null);
  const [metaError, setMetaError] = useState("");

  const [editingTitle, setEditingTitle] = useState(false);
  const [titleDraft, setTitleDraft] = useState("");
  const [editingDate, setEditingDate] = useState(false);
  const [dateDraft, setDateDraft] = useState("");
  const [editingLocation, setEditingLocation] = useState(false);
  const [locationDraft, setLocationDraft] = useState("");

  const editorRef = useRef<EditorTextareaHandle>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  // Set by a double-click in the preview: where to put the caret, and the
  // viewport y it should end up at, once the textarea has mounted.
  const pendingCaret = useRef<{ offset: number; clientY: number } | null>(null);
  const saveTimer = useRef<number | null>(null);
  const mirrorTimer = useRef<number | null>(null);
  const bodyRef = useRef(body);
  bodyRef.current = body;
  // Refs so a save queued behind another sees the revision/body the previous one produced.
  const detailRef = useRef(detail);
  detailRef.current = detail;
  const lastSavedRef = useRef(lastSavedBody);
  lastSavedRef.current = lastSavedBody;
  const inFlightSave = useRef<Promise<boolean> | null>(null);

  const togglePreview = () => setIsPreview((prev) => !prev);

  const editAtClick = (event: React.MouseEvent) => {
    const range = sourceRangeAt(event.target, bodyRef.current);
    if (range) pendingCaret.current = { offset: range.end, clientY: event.clientY };
    setIsPreview(false);
  };

  // Runs after the textarea has grown to full height (its own layout effect
  // runs first), so the scroll position isn't clamped by a one-row textarea.
  useLayoutEffect(() => {
    const pending = pendingCaret.current;
    pendingCaret.current = null;
    if (isPreview || !pending) return;
    const caretY = editorRef.current?.placeCaret(pending.offset);
    if (caretY != null && scrollRef.current) scrollRef.current.scrollTop += caretY - pending.clientY;
  }, [isPreview]);

  const load = useCallback(
    async (id: string) => {
      if (saveTimer.current) window.clearTimeout(saveTimer.current);
      setLoadError("");
      setConflict(null);
      setMirrorOffer(null);
      setMetaError("");
      try {
        const fetched = await api.get<NoteDetail>(`/api/notes/${id}`);
        const envelope = await fetchPresigned(fetched.presignedUrl);
        const content = await decryptNoteBody(envelope, fetched.dek, fetched.id, fetched.currentVersion);
        const serverBody = stripFrontmatter(content);

        const mirror = await getBuffer(id);
        if (mirror && mirror.updatedAt > Date.parse(fetched.updatedAt) && mirror.body !== serverBody) {
          setDetail(fetched);
          setBody(mirror.body);
          setLastSavedBody(mirror.body);
          setMirrorOffer({ mirror: mirror.body, server: serverBody });
          return;
        }
        setDetail(fetched);
        setBody(serverBody);
        setLastSavedBody(serverBody);
      } catch (ex) {
        setLoadError(ex instanceof Error ? ex.message : "Failed to load the note.");
      }
    },
    [],
  );

  useEffect(() => {
    if (noteId) {
      setIsPreview(!startInEditMode);
      void load(noteId);
    } else {
      setDetail(null);
      setBody("");
      setLastSavedBody("");
      setDirty(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [noteId, load]);

  // Mirror keystroke batches to IndexedDB (crash protection, not offline).
  useEffect(() => {
    if (!detail) return;
    if (mirrorTimer.current) window.clearTimeout(mirrorTimer.current);
    mirrorTimer.current = window.setTimeout(() => {
      if (bodyRef.current !== lastSavedBody) void putBuffer(detail.id, bodyRef.current);
    }, 800);
    return () => {
      if (mirrorTimer.current) window.clearTimeout(mirrorTimer.current);
    };
  }, [body, detail, lastSavedBody]);

  /**
   * Resolves true when the server holds `text` afterwards. Saves never overlap:
   * a call made while one is in flight waits for it, then saves whatever is
   * still unsaved. The editor text is never replaced, so typing during a save
   * is kept and stays dirty.
   */
  const save = useCallback(
    async (opts: { expectedRevision?: string; body?: string; silent?: boolean } = {}): Promise<boolean> => {
      while (inFlightSave.current) await inFlightSave.current;
      const current = detailRef.current;
      if (!current) return false;
      const text = opts.body ?? bodyRef.current;
      if (text === lastSavedRef.current) {
        setDirty(bodyRef.current !== text);
        return true;
      }
      setSaving(true);
      const request = api.put<NoteDetail>(`/api/notes/${current.id}`, {
        expectedRevision: opts.expectedRevision ?? current.revision,
        body: text,
      });
      inFlightSave.current = request.then(() => true, () => false);
      try {
        const updated = await request;
        // The user may have switched notes while this was in flight.
        if (detailRef.current?.id !== updated.id) return true;
        detailRef.current = updated;
        lastSavedRef.current = text;
        setDetail(updated);
        setLastSavedBody(text);
        setDirty(bodyRef.current !== text);
        setConflict(null);
        markSaved(updated.id);
        onNoteChanged(updated);
        return true;
      } catch (ex) {
        if (ex instanceof ApiError && ex.status === 409 && typeof ex.extensions.currentRevision === "string") {
          setConflict({
            message: "This note changed elsewhere. Choose what to keep.",
            currentRevision: ex.extensions.currentRevision,
          });
        } else if (!opts.silent) {
          setLoadError(ex instanceof Error ? ex.message : "Save failed.");
        }
        return false;
      } finally {
        inFlightSave.current = null;
        setSaving(false);
      }
    },
    [onNoteChanged],
  );

  // Flushes pending edits first so the kept version is exactly what is on screen.
  const saveVersion = useCallback(async () => {
    if (!detail) return;
    if (saveTimer.current) window.clearTimeout(saveTimer.current);
    setSavingVersion(true);
    setVersionNotice("");
    try {
      if (!(await save())) return;
      const updated = await api.post<NoteDetail>(`/api/notes/${detail.id}/versions`);
      setDetail(updated);
      onNoteChanged(updated);
      if (updated.needsIndex) {
        window.dispatchEvent(new CustomEvent("ai-diary:index-requested", { detail: updated.id }));
        setVersionNotice("Version saved · indexing for search");
      } else {
        setVersionNotice("Version saved");
      }
    } catch (ex) {
      setMetaError(ex instanceof Error ? ex.message : "Could not save the version.");
    } finally {
      setSavingVersion(false);
    }
  }, [detail, save, onNoteChanged]);

  useEffect(() => {
    if (!versionNotice) return;
    const timer = window.setTimeout(() => setVersionNotice(""), 2500);
    return () => window.clearTimeout(timer);
  }, [versionNotice]);

  const saveMeta = useCallback(
    async (overrides: { title?: string; entryDate?: string; locationLabel?: string | null }) => {
      if (!detail) return;
      const title = overrides.title ?? detail.title;
      const entryDate = overrides.entryDate ?? detail.entryDate;
      const locationLabel = overrides.locationLabel !== undefined ? overrides.locationLabel : detail.location?.label ?? null;
      const clearLocation = overrides.locationLabel !== undefined && !overrides.locationLabel;
      try {
        const updated = await api.patch<NoteDetail>(`/api/notes/${detail.id}/meta`, {
          title,
          entryDate,
          clearLocation,
          location:
            !clearLocation && locationLabel
              ? { lat: detail.location?.lat ?? 0, lon: detail.location?.lon ?? 0, label: locationLabel }
              : null,
        });
        setMetaError("");
        setDetail(updated);
        onNoteChanged(updated);
      } catch (ex) {
        setMetaError(ex instanceof Error ? ex.message : "Could not save.");
      }
    },
    [detail, onNoteChanged],
  );

  const startEditTitle = () => {
    if (!detail) return;
    setTitleDraft(detail.title);
    setEditingTitle(true);
  };

  const commitTitle = () => {
    setEditingTitle(false);
    if (detail && titleDraft !== detail.title) void saveMeta({ title: titleDraft });
  };

  const startEditDate = () => {
    if (!detail) return;
    setDateDraft(detail.entryDate);
    setEditingDate(true);
  };

  const commitDate = () => {
    setEditingDate(false);
    if (detail && dateDraft && dateDraft !== detail.entryDate) void saveMeta({ entryDate: dateDraft });
  };

  const startEditLocation = () => {
    if (!detail) return;
    setLocationDraft(detail.location?.label ?? "");
    setEditingLocation(true);
  };

  const commitLocation = () => {
    setEditingLocation(false);
    const current = detail?.location?.label ?? "";
    if (detail && locationDraft !== current) void saveMeta({ locationLabel: locationDraft || null });
  };

  const editableKeyDown = (event: React.KeyboardEvent<HTMLInputElement>, onCancel: () => void) => {
    if (event.key === "Enter") event.currentTarget.blur();
    else if (event.key === "Escape") onCancel();
  };

  // Debounced autosave. While a save is in flight the timer does nothing; when
  // it lands, `detail` changes and this re-arms if edits are still pending.
  useEffect(() => {
    if (!detail || !dirty) return;
    if (saveTimer.current) window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(() => {
      if (!inFlightSave.current) void save();
    }, 1500);
    return () => {
      if (saveTimer.current) window.clearTimeout(saveTimer.current);
    };
  }, [body, dirty, detail, save]);

  const onBodyChange = (next: string) => {
    setBody(next);
    setDirty(next !== lastSavedBody);
  };

  const onBlur = () => {
    if (bodyRef.current !== lastSavedBody) {
      // Cancel the pending debounced save — otherwise it fires ~1.5s later
      // with a stale expectedRevision and races this one into a conflict.
      if (saveTimer.current) window.clearTimeout(saveTimer.current);
      void save();
    }
  };

  const onKeyDown = (event: React.KeyboardEvent) => {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "s") {
      event.preventDefault();
      if (saveTimer.current) window.clearTimeout(saveTimer.current);
      void save();
    }
  };

  if (!detail) {
    return (
      <div className="editor-pane empty">
        {loadError ? <p className="error-text">{loadError}</p> : <p className="muted">Select or create a note.</p>}
      </div>
    );
  }

  return (
    <div className="editor-pane" onKeyDown={onKeyDown}>
      {!isPreview && <FormatToolbar editorRef={editorRef} />}

      {mirrorOffer && (
        <div className="notice-banner">
          <p>A newer unsaved version of this note was recovered from this browser.</p>
          <div className="notice-actions">
            <button
              type="button"
              className="primary-button"
              onClick={() => {
                setMirrorOffer(null);
                setDirty(true);
              }}
            >
              Keep recovered version
            </button>
            <button
              type="button"
              className="secondary-button"
              onClick={() => {
                setBody(mirrorOffer.server);
                setLastSavedBody(mirrorOffer.server);
                setMirrorOffer(null);
              }}
            >
              Discard it
            </button>
          </div>
        </div>
      )}

      {conflict && (
        <div className="notice-banner">
          <p>{conflict.message}</p>
          <div className="notice-actions">
            <button type="button" className="primary-button" onClick={() => void load(detail.id)}>
              Reload theirs
            </button>
            <button
              type="button"
              className="secondary-button"
              onClick={() => void save({ expectedRevision: conflict.currentRevision })}
            >
              Overwrite with mine
            </button>
          </div>
        </div>
      )}

      <div className="editor-scroll" ref={scrollRef}>
        <div className="editor-header">
          {editingTitle ? (
            <input
              className="note-title-input"
              value={titleDraft}
              autoFocus
              onChange={(e) => setTitleDraft(e.target.value)}
              onBlur={commitTitle}
              onKeyDown={(e) => editableKeyDown(e, () => setEditingTitle(false))}
            />
          ) : (
            <h2 className="note-title editable" title="Click to rename" onClick={startEditTitle}>
              {detail.title || "Untitled"}
            </h2>
          )}

          {editingDate ? (
            <input
              type="date"
              className="entry-date-input"
              value={dateDraft}
              autoFocus
              onChange={(e) => setDateDraft(e.target.value)}
              onBlur={commitDate}
              onKeyDown={(e) => editableKeyDown(e, () => setEditingDate(false))}
            />
          ) : (
            <span className="entry-date editable" title="Click to change the date" onClick={startEditDate}>
              {detail.entryDate}
            </span>
          )}

          {editingLocation ? (
            <input
              className="location-input"
              value={locationDraft}
              autoFocus
              placeholder="Add a place"
              onChange={(e) => setLocationDraft(e.target.value)}
              onBlur={commitLocation}
              onKeyDown={(e) => editableKeyDown(e, () => setEditingLocation(false))}
            />
          ) : (
            <span className="location-label editable" title="Click to set a location" onClick={startEditLocation}>
              {detail.location?.label ? `📍 ${detail.location.label}` : "+ Location"}
            </span>
          )}

          <span className="header-spacer" />
          {metaError && <span className="error-text small">{metaError}</span>}
          {saving ? (
            <span className="muted">Saving…</span>
          ) : dirty ? (
            <span className="muted">Unsaved</span>
          ) : versionNotice ? (
            <span className="muted">{versionNotice}</span>
          ) : null}
          {isPreview ? (
            <button type="button" className="secondary-button" onClick={togglePreview}>
              Edit
            </button>
          ) : (
            <>
              <button type="button" className="secondary-button" onClick={() => setShowHistory(true)}>
                History
              </button>
              <button
                type="button"
                className="primary-button"
                disabled={savingVersion}
                onClick={() => void saveVersion()}
                title="Keep the current text as a version for the retention period and index it for search"
              >
                Save &amp; Index
              </button>
              <button type="button" className="secondary-button" onClick={togglePreview}>
                Preview Mode
              </button>
            </>
          )}
        </div>

        <div className="editor-body">
          {isPreview ? (
            <div className="preview markdown-body" onDoubleClick={editAtClick}>
              <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize, rehypeSourceOffsets]}>
                {body || "_Nothing written yet._"}
              </ReactMarkdown>
            </div>
          ) : (
            <EditorTextarea
              ref={editorRef}
              value={body}
              onChange={onBodyChange}
              onBlur={onBlur}
              onDoubleClick={() => setIsPreview(true)}
            />
          )}
        </div>
      </div>

      {showHistory && (
        <HistoryModal
          noteId={detail.id}
          currentVersion={detail.currentVersion}
          onRestored={() => {
            setShowHistory(false);
            void load(detail.id);
          }}
          onClose={() => setShowHistory(false)}
        />
      )}
    </div>
  );
}
