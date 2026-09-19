import type { EditorTextareaHandle } from "./EditorTextarea";

interface Props {
  editorRef: React.RefObject<EditorTextareaHandle | null>;
}

const tools = [
  { id: "bold", label: "B", title: "Bold", before: "**", after: "**" },
  { id: "italic", label: "I", title: "Italic", before: "_", after: "_" },
  { id: "h2", label: "H", title: "Heading", prefix: "## " },
  { id: "list", label: "•", title: "Bullet list", prefix: "- " },
  { id: "quote", label: "❝", title: "Quote", prefix: "> " },
] as const;

/** Formatting bar pinned above the note title/date/location. Only shown while editing. */
export function FormatToolbar({ editorRef }: Props) {
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
    <div className="format-toolbar">
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
      <button type="button" className="tool-button" title="Link" onMouseDown={(e) => e.preventDefault()} onClick={link}>
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
    </div>
  );
}
