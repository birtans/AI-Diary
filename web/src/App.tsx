import { useCallback, useEffect, useState } from "react";
import { Link, Navigate, Route, Routes } from "react-router-dom";
import { AuthProvider, useAuth } from "./auth/AuthContext";
import { api } from "./api/client";
import type { RecoveryStatusResponse } from "./api/types";
import { RequestAccessView } from "./views/RequestAccessView";
import { RecoverySetupView } from "./views/RecoverySetupView";
import { NotesView } from "./views/NotesView";
import { TrashView } from "./views/TrashView";
import { SearchView } from "./views/SearchView";
import { ChatView } from "./views/ChatView";
import { SettingsView } from "./views/SettingsView";
import { AdminView } from "./views/AdminView";

export function App() {
  return (
    <AuthProvider>
      <Root />
    </AuthProvider>
  );
}

function Root() {
  const { initialized, authenticated } = useAuth();
  if (!initialized) {
    return (
      <div className="landing">
        <p className="muted">Loading…</p>
      </div>
    );
  }
  if (!authenticated) {
    return <RequestAccessView />;
  }
  return <AuthedApp />;
}

function AuthedApp() {
  const { userEmail, logout, keycloak } = useAuth();
  const [status, setStatus] = useState<RecoveryStatusResponse["status"] | "loading">("loading");
  const [showRecovery, setShowRecovery] = useState(false);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const isAdmin = keycloak.hasRealmRole("diary-admin");

  const refreshStatus = useCallback(async () => {
    try {
      const result = await api.get<RecoveryStatusResponse>("/api/recovery/status");
      setStatus(result.status);
      if (result.status === "not_setup") setShowRecovery(true);
    } catch {
      setStatus("ok");
    }
  }, []);

  useEffect(() => {
    void refreshStatus();
  }, [refreshStatus]);

  if (showRecovery) {
    return (
      <RecoverySetupView
        onDone={() => {
          setStatus("ok");
          setShowRecovery(false);
        }}
      />
    );
  }

  return (
    <div className={`app ${drawerOpen ? "drawer-open" : ""}`}>
      <header className="topbar">
        <button type="button" className="icon-button drawer-toggle" onClick={() => setDrawerOpen((v) => !v)}>
          ☰
        </button>
        <nav className="topnav">
          <Link to="/" onClick={() => setDrawerOpen(false)}>
            Notes
          </Link>
          <Link to="/search" onClick={() => setDrawerOpen(false)}>
            Search
          </Link>
          <Link to="/chat" onClick={() => setDrawerOpen(false)}>
            Chat
          </Link>
          <Link to="/settings" onClick={() => setDrawerOpen(false)}>
            Settings
          </Link>
          {isAdmin && (
            <Link to="/admin" onClick={() => setDrawerOpen(false)}>
              Admin
            </Link>
          )}
        </nav>
        <span className="topbar-user">
          {userEmail && <span className="muted">{userEmail}</span>}
          <button type="button" className="link-button" onClick={() => void logout()}>
            Sign out
          </button>
        </span>
      </header>

      {status === "setup_pending_ack" && (
        <div className="notice-banner top">
          <p>Your recovery key has not been confirmed yet. Do it now — an unconfirmed key is not a key.</p>
          <button type="button" className="primary-button" onClick={() => setShowRecovery(true)}>
            Review recovery
          </button>
        </div>
      )}

      <div className="drawer-scrim" onClick={() => setDrawerOpen(false)} />
      <Routes>
        <Route path="/" element={<NotesView />} />
        <Route path="/trash" element={<TrashView />} />
        <Route path="/search" element={<SearchView />} />
        <Route path="/chat" element={<ChatView />} />
        <Route path="/settings" element={<SettingsView />} />
        <Route path="/admin" element={<AdminView />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </div>
  );
}
