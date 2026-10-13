import { useEffect, useState } from "react";
import type { AccessRequestItem, AdminUserItem } from "../api/types";
import { api, ApiError } from "../api/client";
import { ConfirmDialog } from "../components/ConfirmDialog";

const noteCount = (n: number) => `${n} ${n === 1 ? "note" : "notes"}`;

/** Operator-only approval queue and user list; visible when the account has the admin flag. */
export function AdminView() {
  const [items, setItems] = useState<AccessRequestItem[]>([]);
  const [users, setUsers] = useState<AdminUserItem[]>([]);
  const [error, setError] = useState("");
  const [pendingDelete, setPendingDelete] = useState<AdminUserItem | null>(null);

  const refresh = async () => {
    try {
      const [requests, accounts] = await Promise.all([
        api.get<AccessRequestItem[]>("/api/admin/access-requests"),
        api.get<AdminUserItem[]>("/api/admin/users"),
      ]);
      setItems(requests);
      setUsers(accounts);
    } catch (ex) {
      setError(ex instanceof ApiError ? ex.detail : "Could not load the admin data.");
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

  const deleteUser = async (id: string) => {
    try {
      await api.delete(`/api/admin/users/${id}`);
      await refresh();
    } catch (ex) {
      setError(ex instanceof ApiError ? ex.detail : "Delete failed.");
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

      <h2>Users</h2>
      {users.length === 0 && <p className="muted">No users yet.</p>}
      <ul className="access-list">
        {users.map((user) => (
          <li key={user.id} className="access-item">
            <div>
              <strong>{user.email}</strong>
              <p className="muted small">
                {noteCount(user.noteCount)} · joined {new Date(user.createdAt).toLocaleDateString()}
                {user.isAdmin && " · admin"}
                {user.isCurrentUser && " · you"}
              </p>
            </div>
            {!user.isCurrentUser && (
              <div className="list-actions">
                <button type="button" className="link-button danger" onClick={() => setPendingDelete(user)}>
                  Delete
                </button>
              </div>
            )}
          </li>
        ))}
      </ul>

      {pendingDelete && (
        <ConfirmDialog
          title="Delete user?"
          message={`${pendingDelete.email} loses access immediately, and their login, ${noteCount(pendingDelete.noteCount)} with their full history, chats and settings are deleted forever. This cannot be undone.`}
          confirmLabel="Delete user"
          danger
          onConfirm={() => {
            const id = pendingDelete.id;
            setPendingDelete(null);
            void deleteUser(id);
          }}
          onCancel={() => setPendingDelete(null)}
        />
      )}
    </div>
  );
}
