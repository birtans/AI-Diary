import { useState } from "react";
import { useNavigate } from "react-router-dom";
import type { SearchResultItem } from "../api/types";
import { api } from "../api/client";

/** Semantic search over note embeddings (bge-m3 via the API). */
export function SearchView() {
  const [query, setQuery] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [results, setResults] = useState<SearchResultItem[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [error, setError] = useState("");
  const navigate = useNavigate();

  const run = async () => {
    if (!query.trim()) return;
    setSearching(true);
    setError("");
    try {
      const found = await api.post<SearchResultItem[]>("/api/search", {
        query,
        dateFrom: from || null,
        dateTo: to || null,
      });
      setResults(found);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Search failed.");
    } finally {
      setSearching(false);
    }
  };

  return (
    <div className="page">
      <h2>Search</h2>
      <p className="muted">Semantic search across your diary — meaning, not keywords.</p>
      <div className="search-bar">
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") void run();
          }}
          placeholder="What are you looking for?"
          autoFocus
        />
        <label className="inline-label">
          From
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label className="inline-label">
          To
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
        <button type="button" className="primary-button" onClick={() => void run()} disabled={searching}>
          {searching ? "Searching…" : "Search"}
        </button>
      </div>
      {error && <p className="error-text">{error}</p>}
      {results && (
        <div className="search-results">
          {results.length === 0 && <p className="muted">Nothing relevant found.</p>}
          {results.map((result) => (
            <article key={result.noteId} className="search-result">
              <header>
                <strong>{result.title || "Untitled"}</strong>
                <span className="muted">
                  {result.entryDate} · {(result.score * 100).toFixed(0)}%
                </span>
              </header>
              <p>{result.excerpt}</p>
              <button type="button" className="link-button" onClick={() => void navigate(`/?note=${result.noteId}`)}>
                Open note
              </button>
            </article>
          ))}
        </div>
      )}
    </div>
  );
}
