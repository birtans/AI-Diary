import type { Nodes, Root } from "hast";

/**
 * Rehype plugin: stamps every rendered element with the markdown source range
 * it came from (data-src-start / data-src-end). Must run after rehype-sanitize,
 * which strips data attributes but keeps node positions.
 */
export function rehypeSourceOffsets() {
  return (tree: Root) => stamp(tree);
}

function stamp(node: Nodes) {
  if (node.type === "element") {
    const start = node.position?.start.offset;
    const end = node.position?.end.offset;
    if (start !== undefined && end !== undefined) {
      node.properties.dataSrcStart = start;
      node.properties.dataSrcEnd = end;
    }
  }
  if ("children" in node) node.children.forEach(stamp);
}

/**
 * Maps a double-click in the rendered preview back to a range in the markdown
 * source. Uses the innermost stamped element as the window, then finds the word
 * the browser selected, counting earlier occurrences so a repeated word lands
 * on the right one. Falls back to the start of the element.
 */
export function sourceRangeAt(target: EventTarget, source: string): { start: number; end: number } | null {
  if (!(target instanceof Element)) return null;
  const block = target.closest<HTMLElement>("[data-src-start]");
  if (!block) return null;
  const start = Number(block.dataset.srcStart);
  const end = Number(block.dataset.srcEnd);

  const selection = window.getSelection();
  const word = selection?.toString().trim() ?? "";
  if (!selection || selection.rangeCount === 0 || !word) return { start, end: start };

  const before = document.createRange();
  before.setStart(block, 0);
  const picked = selection.getRangeAt(0);
  before.setEnd(picked.startContainer, picked.startOffset);
  const occurrence = countOccurrences(before.toString(), word);

  const slice = source.slice(start, end);
  let index = -1;
  for (let i = 0; i <= occurrence; i++) {
    const next = slice.indexOf(word, index + 1);
    if (next === -1) break;
    index = next;
  }
  if (index === -1) return { start, end: start };
  return { start: start + index, end: start + index + word.length };
}

function countOccurrences(text: string, word: string): number {
  let count = 0;
  for (let i = text.indexOf(word); i !== -1; i = text.indexOf(word, i + word.length)) count++;
  return count;
}
