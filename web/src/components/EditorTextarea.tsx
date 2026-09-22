import React, { forwardRef, useEffect, useImperativeHandle, useRef } from "react";

export interface EditorTextareaHandle {
  focus: () => void;
  insertText: (text: string) => void;
  wrapSelection: (before: string, after: string) => void;
  prefixLines: (prefix: string) => void;
  undo: () => void;
  redo: () => void;
}

interface Props {
  value: string;
  onChange: (next: string) => void;
  onBlur?: () => void;
  onDoubleClick?: () => void;
  placeholder?: string;
}

/**
 * The editing surface. Every programmatic edit goes through
 * document.execCommand("insertText"): assigning to textarea.value destroys the
 * browser's native undo stack. Deprecated, and the only thing that works.
 */
export const EditorTextarea = forwardRef<EditorTextareaHandle, Props>(function EditorTextarea(
  { value, onChange, onBlur, onDoubleClick, placeholder },
  ref,
) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // Auto-grow: no inner scrollbar.
  useEffect(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight + 2}px`;
  }, [value]);

  useImperativeHandle(
    ref,
    () => ({
      focus: () => textareaRef.current?.focus(),
      insertText: (text: string) => {
        const el = textareaRef.current;
        if (!el) return;
        el.focus();
        document.execCommand("insertText", false, text);
      },
      wrapSelection: (before: string, after: string) => {
        const el = textareaRef.current;
        if (!el) return;
        el.focus();
        const start = el.selectionStart;
        const end = el.selectionEnd;
        const selected = el.value.slice(start, end);
        const inserted = before + selected + after;
        document.execCommand("insertText", false, inserted);
        el.selectionStart = start + before.length;
        el.selectionEnd = start + before.length + selected.length;
      },
      prefixLines: (prefix: string) => {
        const el = textareaRef.current;
        if (!el) return;
        el.focus();
        const start = el.selectionStart;
        const end = el.selectionEnd;
        const lineStart = el.value.lastIndexOf("\n", start - 1) + 1;
        const lineEndRaw = el.value.indexOf("\n", end);
        const lineEnd = lineEndRaw === -1 ? el.value.length : lineEndRaw;
        const block = el.value.slice(lineStart, lineEnd);
        const lines = block.split("\n");
        const prefixed = lines.map((l) => prefix + l).join("\n");
        document.execCommand("insertText", false, prefixed);
        el.selectionStart = lineStart + prefix.length;
        el.selectionEnd = lineStart + prefixed.length;
      },
      undo: () => document.execCommand("undo"),
      redo: () => document.execCommand("redo"),
    }),
    [],
  );

  const handleKeyDown = (event: React.KeyboardEvent<HTMLTextAreaElement>) => {
    const el = event.currentTarget;
    if (event.key === "Tab") {
      event.preventDefault();
      indentSelection(el, event.shiftKey);
      return;
    }
    if (
      event.key === "Enter" &&
      !event.shiftKey &&
      !event.metaKey &&
      !event.ctrlKey &&
      !event.altKey &&
      !event.nativeEvent.isComposing
    ) {
      const continuation = listContinuation(el.value, el.selectionStart);
      if (continuation !== null) {
        event.preventDefault();
        document.execCommand("insertText", false, continuation);
      }
    }
  };

  return (
    <textarea
      ref={textareaRef}
      className="editor-textarea"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      onBlur={onBlur}
      onDoubleClick={onDoubleClick}
      onKeyDown={handleKeyDown}
      placeholder={placeholder ?? "Write…"}
      spellCheck
      rows={1}
    />
  );
});

// ------------------------------------------------------------- behaviours

const LIST_RE = /^(\s*)([-*+]|\d+[.)])\s+(\[[ xX]\]\s+)?(.*)$/;

function indentSelection(el: HTMLTextAreaElement, dedent: boolean) {
  const start = el.selectionStart;
  const end = el.selectionEnd;
  const lineStart = el.value.lastIndexOf("\n", start - 1) + 1;
  const lineEndRaw = el.value.indexOf("\n", end);
  const lineEnd = lineEndRaw === -1 ? el.value.length : lineEndRaw;
  const block = el.value.slice(lineStart, lineEnd);
  const lines = block.split("\n");

  const transformed = lines
    .map((line) => {
      if (!dedent) return "  " + line;
      if (line.startsWith("  ")) return line.slice(2);
      if (line.startsWith("\t")) return line.slice(1);
      return line;
    })
    .join("\n");

  document.execCommand("insertText", false, transformed);
  el.selectionStart = lineStart + (dedent ? 0 : 2);
  el.selectionEnd = lineStart + transformed.length;
}

/**
 * List/blockquote continuation on Enter. Returns the text to insert after the
 * newline, or null to let Enter behave normally. An empty list item exits the
 * list.
 */
function listContinuation(value: string, caret: number): string | null {
  const lineStart = value.lastIndexOf("\n", caret - 1) + 1;
  const lineEndRaw = value.indexOf("\n", caret);
  const lineEnd = lineEndRaw === -1 ? value.length : lineEndRaw;
  const line = value.slice(lineStart, lineEnd);

  const quote = /^(\s*>\s?)/.exec(line);
  if (quote) {
    return "\n" + quote[1];
  }

  const match = LIST_RE.exec(line);
  if (!match) return null;
  const [, indent, marker, task, rest] = match;
  const isNumbered = /^\d+[.)]$/.test(marker);
  const itemBody = (task ?? "") + rest;

  // Empty list item: exit the list (insert a bare newline, then remove the
  // now-empty bullet line by returning an empty continuation).
  if (itemBody.trim().length === 0 && !task?.includes("x") && !task?.includes("X")) {
    // Replace the empty item with an empty line (exit list).
    const el = document.activeElement as HTMLTextAreaElement;
    if (el) {
      const bulletStart = lineStart + indent.length;
      el.setSelectionRange(bulletStart, lineEnd);
      document.execCommand("insertText", false, "");
    }
    return null;
  }

  if (isNumbered) {
    const number = parseInt(marker, 10);
    return "\n" + indent + `${number + 1}. ` + (task ?? "");
  }
  return "\n" + indent + marker + " " + (task ?? "");
}
