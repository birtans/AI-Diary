import { useEffect, useState } from "react";
import type { EditorTextareaHandle } from "./EditorTextarea";

interface Props {
  editorRef: React.RefObject<EditorTextareaHandle | null>;
  onPreview: () => void;
  isPreview: boolean;
}

const tools = [
  { id: "bold", label: "B", title: "Bold", before: "**", after: "**" },
  { id: "italic", label: "I", title: "Italic", before: "_", after: "_" },
  { id: "h2", label: "H", title: "Heading", prefix: "## " },
  { id: "list", label: "•", title: "Bullet list", prefix: "- " },
  { id: "quote", label: "❝", title: "Quote", prefix: "> " },
] as const;

/**
 * A small format toolbar pinned above the on-screen keyboard via the
 * visualViewport API, so it stays reachable while typing on mobile.
 */
export function FormatToolbar({ editorRef, onPreview, isPreview }: Props) {
  const [keyboardOffset, setKeyboardOffset] = useState(0);

  useEffect(() => {
    const viewport = window.visualViewport;
    if (!viewport) return;
    const update = () => {
      const bottom = window.innerHeight - (viewport.offsetTop + viewport.height);
      setKeyboardOffset(Math.max(0, Math.round(bottom)));
    };
    viewport.addEventListener("scroll", update);
    viewport.addEventListener("resize", update);
    update();
    return () => {
      viewport.removeEventListener("scroll", update);
      viewport.removeEventListener("resize", update);
    };
  }, []);

  const apply = (tool: (typeof tools)[number]) => {
    const editor = editorRef.current;
    if (!editor) return;
    if ("prefix" in tool && tool.prefix) {
      editor.prefixLines(tool.prefix);
    } else if ("before" in tool && tool.before) {
      editor.wrapSelection(tool.before, tool.after ?? "");
    }
  };

  const link = () => {
    const editor = editorRef.current;
    if (!editor) return;
    const url = window.prompt("Link URL", "https://");
    if (url) editor.wrapSelection("[", `](${url})`);
  };

  return (
    <div className="format-toolbar" style={{ bottom: keyboardOffset }}>
      {tools.map((tool) => (
        <button
          key={tool.id}
          type="button"
          className="tool-button"
          title={tool.title}
          onMouseDown={(e) => e.preventDefault()}
          onClick={() => apply(tool)}
        >
          {tool.label}
        </button>
      ))}
      <button
        type="button"
        className="tool-button"
        title="Link"
        onMouseDown={(e) => e.preventDefault()}
        onClick={link}
      >
        🔗
      </button>
      <button
        type="button"
        className="tool-button"
        title="Undo"
        onMouseDown={(e) => e.preventDefault()}
        onClick={() => editorRef.current?.undo()}
      >
        ↶
      </button>
      <button
        type="button"
        className="tool-button"
        title="Redo"
        onMouseDown={(e) => e.preventDefault()}
        onClick={() => editorRef.current?.redo()}
      >
        ↷
      </button>
      <span className="toolbar-spacer" />
      <button
        type="button"
        className={`tool-button ${isPreview ? "active" : ""}`}
        title={isPreview ? "Back to editing" : "Preview"}
        onMouseDown={(e) => e.preventDefault()}
        onClick={onPreview}
      >
        {isPreview ? "✏️" : "👁"}
      </button>
    </div>
  );
}
