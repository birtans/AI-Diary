import { useCallback, useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Sidebar } from "../components/Sidebar";
import { EditorPane } from "../components/EditorPane";
import type { NoteDetail } from "../api/types";
import { useIsMobile } from "../lib/useIsMobile";

/** Left rail (notes grouped by entry date) + right editor pane. */
export function NotesView() {
  const isMobile = useIsMobile();
  const [searchParams, setSearchParams] = useSearchParams();
  const [selectedId, setSelectedId] = useState<string | null>(searchParams.get("note"));

  const select = useCallback(
    (id: string) => {
      setSelectedId(id);
      setSearchParams({ note: id }, { replace: true });
    },
    [setSearchParams],
  );

  const selectNew = useCallback(
    (id: string) => {
      setSelectedId(id);
      setSearchParams({ note: id, new: "1" }, { replace: true });
    },
    [setSearchParams],
  );

  useEffect(() => {
    const param = searchParams.get("note");
    if (param && param !== selectedId) setSelectedId(param);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams]);

  const onNoteChanged = useCallback((detail: NoteDetail | null) => {
    window.dispatchEvent(new CustomEvent("ai-diary:notes-changed", { detail }));
  }, []);

  return (
    <div className="main-layout">
      {!isMobile && (
        <Sidebar
          selectedId={selectedId}
          onSelect={select}
          onCreate={selectNew}
          onDeleted={(id) => {
            if (id === selectedId) {
              setSelectedId(null);
              setSearchParams({}, { replace: true });
            }
          }}
        />
      )}
      <EditorPane
        noteId={selectedId}
        startInEditMode={searchParams.get("new") === "1"}
        onNoteChanged={onNoteChanged}
      />
    </div>
  );
}
