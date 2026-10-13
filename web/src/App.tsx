import { useCallback, useEffect, useState } from "react";
import { Link, NavLink, Navigate, Route, Routes } from "react-router-dom";
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
import { ThemeToggle } from "./components/ThemeToggle";
import { MobileDrawer } from "./components/MobileDrawer";
import { useIsMobile } from "./lib/useIsMobile";

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
        <ThemeToggle className="theme-toggle-floating" />
        <p className="muted">Loading…</p>
      </div>
    );
  }
  if (!authenticated) {
    return (
      <>
        <ThemeToggle className="theme-toggle-floating" />
        <RequestAccessView />
      </>
    );
  }
  return <AuthedApp />;
}

function AuthedApp() {
  const { userEmail, logout, keycloak } = useAuth();
  const [status, setStatus] = useState<RecoveryStatusResponse["status"] | "loading">("loading");
  const [showRecovery, setShowRecovery] = useState(false);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const isAdmin = keycloak.hasRealmRole("diary-admin");
  const isMobile = useIsMobile();

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
      <>
        <ThemeToggle className="theme-toggle-floating" />
        <RecoverySetupView
          onDone={() => {
            setStatus("ok");
            setShowRecovery(false);
          }}
        />
      </>
    );
  }

  return (
    <div className={`app ${drawerOpen ? "drawer-open" : ""}`}>
      <header className="topbar">
        {isMobile && (
          <button type="button" className="icon-button drawer-toggle" onClick={() => setDrawerOpen((v) => !v)}>
            ☰
          </button>
        )}
        <Link to="/" className="topbar-logo" onClick={() => setDrawerOpen(false)}>
          AI Diary
        </Link>
        <nav className="topnav">
          <NavLink to="/" end onClick={() => setDrawerOpen(false)}>
            Notes
          </NavLink>
          <NavLink to="/search" onClick={() => setDrawerOpen(false)}>
            Search
          </NavLink>
          <NavLink to="/chat" onClick={() => setDrawerOpen(false)}>
            Chat
          </NavLink>
          <NavLink to="/settings" onClick={() => setDrawerOpen(false)}>
            Settings
          </NavLink>
          {isAdmin && (
            <NavLink to="/admin" onClick={() => setDrawerOpen(false)}>
              Admin
            </NavLink>
          )}
        </nav>
        <span className="topbar-user">
          <ThemeToggle />
          {userEmail && (
            <>
              <span className="user-avatar">{userEmail.slice(0, 2).toUpperCase()}</span>
              <span className="muted">{userEmail}</span>
            </>
          )}
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
      {isMobile && <MobileDrawer isAdmin={isAdmin} onNavigate={() => setDrawerOpen(false)} />}
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
