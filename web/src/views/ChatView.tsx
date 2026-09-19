import { useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeSanitize from "rehype-sanitize";
import { useNavigate } from "react-router-dom";
import type { ChatMessageItem, ConversationListItem, NoteDetail } from "../api/types";
import { api, apiStream } from "../api/client";
import { todayIso, timezoneOf } from "../lib/crypto";

interface LocalMessage {
  role: "user" | "assistant";
  body: string;
  pending?: boolean;
  citedNoteIds?: string[];
}

interface DraftState {
  title: string;
  body: string;
  entryDate: string;
}

/** Chat with the user's own LLM endpoint; the agent proposes, never writes. */
export function ChatView() {
  const [conversations, setConversations] = useState<ConversationListItem[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [messages, setMessages] = useState<LocalMessage[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const [draft, setDraft] = useState<DraftState | null>(null);
  const [error, setError] = useState("");
  const bottomRef = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();

  const refreshConversations = async () => {
    try {
      setConversations(await api.get<ConversationListItem[]>("/api/chat/conversations"));
    } catch {
      // non-fatal
    }
  };

  useEffect(() => {
    void refreshConversations();
  }, []);

  useEffect(() => {
    if (!activeId) return;
    void (async () => {
      try {
        const list = await api.get<ChatMessageItem[]>(`/api/chat/conversations/${activeId}`);
        setMessages(list.map((m) => ({ role: m.role, body: m.body, citedNoteIds: m.citedNoteIds })));
      } catch (ex) {
        setError(ex instanceof Error ? ex.message : "Could not load the conversation.");
      }
    })();
  }, [activeId]);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, busy]);

  const send = async () => {
    const text = input.trim();
    if (!text || busy) return;
    setInput("");
    setBusy(true);
    setError("");
    setDraft(null);
    setMessages((prev) => [...prev, { role: "user", body: text }, { role: "assistant", body: "", pending: true }]);

    try {
      await apiStream(
        "/api/chat",
        { conversationId: activeId ?? null, message: text },
        (event) => {
          switch (event.type) {
            case "delta":
              if (typeof event.content === "string") {
                setMessages((prev) => {
                  const next = [...prev];
                  const last = next[next.length - 1];
                  if (last && last.role === "assistant") {
                    last.body += event.content as string;
                    last.pending = false;
                  }
                  return next;
                });
              }
              break;
            case "reset":
              // The stream fell back to a buffered response; clear the partial
              // text before the full answer arrives.
              setMessages((prev) => {
                const next = [...prev];
                const last = next[next.length - 1];
                if (last && last.role === "assistant") last.body = "";
                return next;
              });
              break;
            case "draft": {
              const raw = event.extra as { title?: string; body?: string; entry_date?: string } | undefined;
              if (raw) {
                setDraft({
                  title: raw.title ?? "",
                  body: raw.body ?? "",
                  entryDate: raw.entry_date ?? todayIso(),
                });
              }
              break;
            }
            case "done":
              if (typeof event.conversation_id === "string") {
                setActiveId(event.conversation_id);
              }
              break;
            case "error":
              if (typeof event.content === "string") setError(event.content);
              break;
          }
        },
      );
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "The assistant could not be reached.");
    } finally {
      setBusy(false);
      void refreshConversations();
    }
  };

  const saveDraft = async () => {
    if (!draft) return;
    try {
      await api.post<NoteDetail>("/api/notes", {
        title: draft.title,
        body: draft.body,
        entryDate: draft.entryDate || todayIso(),
        timezone: timezoneOf(),
        origin: "agent",
      });
      window.dispatchEvent(new CustomEvent("ai-diary:notes-changed"));
      setDraft(null);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not save the draft.");
    }
  };

  return (
    <div className="chat-layout">
      <aside className="chat-sidebar">
        <div className="chat-sidebar-header">
          <button
            type="button"
            className="primary-button"
            onClick={() => {
              setActiveId(null);
              setMessages([]);
            }}
          >
            ＋ New chat
          </button>
        </div>
        {conversations.map((conversation) => (
          <button
            key={conversation.id}
            type="button"
            className={`note-item ${conversation.id === activeId ? "selected" : ""}`}
            onClick={() => setActiveId(conversation.id)}
          >
            <span className="note-item-title">{conversation.title}</span>
            <span className="muted small">{new Date(conversation.lastMessageAt).toLocaleDateString()}</span>
          </button>
        ))}
      </aside>
      <div className="chat-main">
        <div className="chat-messages">
          {messages.length === 0 && (
            <p className="muted">
              Ask about your diary. The assistant can search your notes and propose drafts — it never writes
              directly.
            </p>
          )}
          {messages.map((message, index) => (
            <div key={index} className={`chat-message ${message.role}`}>
              <div className="chat-bubble markdown-body">
                {message.role === "assistant" && message.body === "" && message.pending ? (
                  <span className="muted">Thinking…</span>
                ) : (
                  <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize]}>
                    {message.body}
                  </ReactMarkdown>
                )}
              </div>
              {message.citedNoteIds && message.citedNoteIds.length > 0 && (
                <div className="citations">
                  {message.citedNoteIds.map((id) => (
                    <button key={id} type="button" className="link-button" onClick={() => void navigate(`/?note=${id}`)}>
                      📄 {id.slice(0, 8)}
                    </button>
                  ))}
                </div>
              )}
            </div>
          ))}
          {error && <p className="error-text">{error}</p>}
          <div ref={bottomRef} />
        </div>

        {draft && (
          <div className="draft-panel">
            <h3>Draft proposed by the assistant</h3>
            <label>
              Title
              <input value={draft.title} onChange={(e) => setDraft({ ...draft, title: e.target.value })} />
            </label>
            <label>
              Entry date
              <input type="date" value={draft.entryDate} onChange={(e) => setDraft({ ...draft, entryDate: e.target.value })} />
            </label>
            <label>
              Body
              <textarea
                rows={6}
                value={draft.body}
                onChange={(e) => setDraft({ ...draft, body: e.target.value })}
              />
            </label>
            <div className="notice-actions">
              <button type="button" className="primary-button" onClick={() => void saveDraft()}>
                Save as note
              </button>
              <button type="button" className="secondary-button" onClick={() => setDraft(null)}>
                Discard
              </button>
            </div>
          </div>
        )}

        <div className="chat-input">
          <input
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                void send();
              }
            }}
            placeholder="Ask about your diary…"
            disabled={busy}
          />
          <button type="button" className="primary-button" onClick={() => void send()} disabled={busy || !input.trim()}>
            Send
          </button>
        </div>
      </div>
    </div>
  );
}
