import { useEffect, useState } from "react";
import type { SettingsResponse } from "../api/types";
import { api } from "../api/client";

/** BYO LLM endpoint settings. The API key is write-only: never returned. */
export function SettingsView() {
  const [endpoint, setEndpoint] = useState("");
  const [model, setModel] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [hasKey, setHasKey] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    void (async () => {
      try {
        const settings = await api.get<SettingsResponse>("/api/settings");
        setEndpoint(settings.endpointUrl ?? "");
        setModel(settings.model ?? "");
        setHasKey(settings.hasApiKey);
      } catch (ex) {
        setError(ex instanceof Error ? ex.message : "Could not load settings.");
      }
    })();
  }, []);

  const save = async () => {
    setMessage("");
    setError("");
    try {
      const updated = await api.put<SettingsResponse>("/api/settings", {
        endpointUrl: endpoint,
        model,
        // null = keep the stored key, "" = clear it, otherwise replace it.
        apiKey: apiKey === "" ? null : apiKey,
      });
      setHasKey(updated.hasApiKey);
      setApiKey("");
      setMessage("Saved.");
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not save settings.");
    }
  };

  return (
    <div className="page narrow">
      <h2>Settings</h2>
      <p className="muted">
        The assistant relays to your own OpenAI-compatible endpoint. Note content is sent to whatever endpoint you
        configure — the operator neither chooses nor controls it.
      </p>
      <label>
        Endpoint URL
        <input
          value={endpoint}
          onChange={(e) => setEndpoint(e.target.value)}
          placeholder="https://api.openai.com/v1"
        />
      </label>
      <label>
        Model
        <input value={model} onChange={(e) => setModel(e.target.value)} placeholder="gpt-4o-mini" />
      </label>
      <label>
        API key {hasKey && <span className="muted">(a key is stored — leave blank to keep it, type a space to clear it)</span>}
        <input type="password" value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
      </label>
      {message && <p className="success-text">{message}</p>}
      {error && <p className="error-text">{error}</p>}
      <button type="button" className="primary-button" onClick={() => void save()}>
        Save
      </button>
    </div>
  );
}
