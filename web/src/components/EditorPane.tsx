import React, { useCallback, useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeSanitize from "rehype-sanitize";
import type { NoteDetail } from "../api/types";
import { api, ApiError, fetchPresigned } from "../api/client";
import { decryptNoteBody } from "../lib/crypto";
import { stripFrontmatter } from "../lib/frontmatter";
import { getBuffer, putBuffer } from "../lib/indexeddb";
import { markSaved } from "../lib/crashFlush";
import { EditorTextarea, type EditorTextareaHandle } from "./EditorTextarea";
import { FormatToolbar } from "./FormatToolbar";
import { MetadataPanel } from "./MetadataPanel";
import { HistoryModal } from "./HistoryModal";

interface Props {
  noteId: string | null;
  onNoteChanged: (detail: NoteDetail | null) => void;
}

const PREVIEW_KEY = "ai-diary-preview-mode";

/**
 * Right pane: plain textarea for writing, sanitised markdown preview.
 * Debounced autosave (1.5 s idle, on blur, on Cmd/Ctrl-S), IndexedDB mirror,
 * and optimistic concurrency handling.
 */
export function EditorPane({ noteId, onNoteChanged }: Props) {
  const [detail, setDetail] = useState<NoteDetail | null>(null);
  const [body, setBody] = useState("");
  const [lastSavedBody, setLastSavedBody] = useState("");
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [isPreview, setIsPreview] = useState(() => sessionStorage.getItem(PREVIEW_KEY) === "1");
  const [showMeta, setShowMeta] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [conflict, setConflict] = useState<{ message: string; currentVersion: number } | null>(null);
  const [mirrorOffer, setMirrorOffer] = useState<{ mirror: string; server: string } | null>(null);

  const editorRef = useRef<EditorTextareaHandle>(null);
  const saveTimer = useRef<number | null>(null);
  const mirrorTimer = useRef<number | null>(null);
  const bodyRef = useRef(body);
  bodyRef.current = body;

  const togglePreview = () => {
    setIsPreview((prev) => {
      sessionStorage.setItem(PREVIEW_KEY, prev ? "0" : "1");
      return !prev;
    });
  };

  const load = useCallback(
    async (id: string) => {
      if (saveTimer.current) window.clearTimeout(saveTimer.current);
      setLoadError("");
      setConflict(null);
      setMirrorOffer(null);
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
    if (noteId) void load(noteId);
    else {
      setDetail(null);
      setBody("");
      setLastSavedBody("");
      setDirty(false);
    }
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

  const save = useCallback(
    async (opts: { expectedVersion?: number; body?: string; silent?: boolean } = {}) => {
      if (!detail) return;
      const text = opts.body ?? bodyRef.current;
      if (text === lastSavedBody) {
        setDirty(false);
        return;
      }
      setSaving(true);
      try {
        const updated = await api.put<NoteDetail>(`/api/notes/${detail.id}`, {
          expectedVersion: opts.expectedVersion ?? detail.currentVersion,
          body: text,
        });
        setDetail(updated);
        setLastSavedBody(text);
        setBody(text);
        setDirty(false);
        setConflict(null);
        markSaved(updated.id);
        onNoteChanged(updated);
      } catch (ex) {
        if (ex instanceof ApiError && ex.status === 409) {
          const current = Number(ex.extensions.currentVersion ?? detail.currentVersion + 1);
          setConflict({
            message: "This note changed elsewhere. Choose what to keep.",
            currentVersion: current,
          });
        } else if (!opts.silent) {
          setLoadError(ex instanceof Error ? ex.message : "Save failed.");
        }
      } finally {
        setSaving(false);
      }
    },
    [detail, lastSavedBody, onNoteChanged],
  );

  // Debounced autosave.
  useEffect(() => {
    if (!detail || !dirty) return;
    if (saveTimer.current) window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(() => void save(), 1500);
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
      // with a stale expectedVersion and races this one into a conflict.
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
      <div className="editor-header">
        <h2 className="note-title">{detail.title || "Untitled"}</h2>
        <span className="entry-date">
          {detail.entryDate}
          {detail.location ? " · 📍" : ""}
        </span>
        <span className="header-spacer" />
        {saving ? <span className="muted">Saving…</span> : dirty ? <span className="muted">Unsaved</span> : null}
        <button type="button" className="icon-button" title="History" onClick={() => setShowHistory(true)}>
          🕘
        </button>
        <button type="button" className="icon-button" title="Details" onClick={() => setShowMeta((v) => !v)}>
          ⓘ
        </button>
      </div>

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
              onClick={() => void save({ expectedVersion: conflict.currentVersion })}
            >
              Overwrite with mine
            </button>
          </div>
        </div>
      )}

      {showMeta && (
        <MetadataPanel
          detail={detail}
          onSaved={(updated) => {
            setDetail(updated);
            onNoteChanged(updated);
          }}
          onClose={() => setShowMeta(false)}
        />
      )}

      <div className="editor-body">
        {isPreview ? (
          <div className="preview markdown-body">
            <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize]}>
              {body || "_Nothing written yet._"}
            </ReactMarkdown>
          </div>
        ) : (
          <EditorTextarea ref={editorRef} value={body} onChange={onBodyChange} onBlur={onBlur} />
        )}
      </div>

      {!isPreview && (
        <FormatToolbar editorRef={editorRef} onPreview={togglePreview} isPreview={false} />
      )}

      {isPreview && (
        <div className="preview-bar">
          <button type="button" className="secondary-button" onClick={togglePreview}>
            Edit
          </button>
        </div>
      )}

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
