import { useEffect, useState } from "react";
import type { AccessRequestItem } from "../api/types";
import { api, ApiError } from "../api/client";

/** Operator-only approval queue; visible when the account has the admin flag. */
export function AdminView() {
  const [items, setItems] = useState<AccessRequestItem[]>([]);
  const [error, setError] = useState("");

  const refresh = async () => {
    try {
      setItems(await api.get<AccessRequestItem[]>("/api/admin/access-requests"));
    } catch (ex) {
      setError(ex instanceof ApiError ? ex.detail : "Could not load access requests.");
    }
  };

  useEffect(() => {
    void refresh();
  }, []);

  const decide = async (id: string, action: "approve" | "reject") => {
    if (action === "approve" && !window.confirm("Approve? The Keycloak user is created and a setup email is sent.")) {
      return;
    }
    try {
      await api.post(`/api/admin/access-requests/${id}/${action}`);
      await refresh();
    } catch (ex) {
      setError(ex instanceof ApiError ? ex.detail : "Action failed.");
    }
  };

  return (
    <div className="page">
      <h2>Access requests</h2>
      {error && <p className="error-text">{error}</p>}
      {items.length === 0 && <p className="muted">No requests yet.</p>}
      <ul className="access-list">
        {items.map((item) => (
          <li key={item.id} className="access-item">
            <div>
              <strong>{item.email}</strong>
              <p>{item.message || "(no message)"}</p>
              <span className="muted small">
                {new Date(item.requestedAt).toLocaleString()} · {item.status}
              </span>
            </div>
            {item.status === "pending" && (
              <div className="list-actions">
                <button type="button" className="primary-button" onClick={() => void decide(item.id, "approve")}>
                  Approve
                </button>
                <button type="button" className="secondary-button" onClick={() => void decide(item.id, "reject")}>
                  Reject
                </button>
              </div>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}
