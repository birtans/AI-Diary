import React, { createContext, useContext, useEffect, useState } from "react";
import type Keycloak from "keycloak-js";
import keycloak, { initKeycloak, login as doLogin, logout as doLogout, getToken } from "./keycloak";
import { attachAuth } from "../api/client";

interface AuthState {
  initialized: boolean;
  authenticated: boolean;
  keycloak: Keycloak;
  login: () => Promise<void>;
  logout: () => Promise<void>;
  userEmail?: string;
}

const AuthContext = createContext<AuthState | null>(null);

attachAuth({ getToken, login: () => void doLogin() });

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [initialized, setInitialized] = useState(false);
  const [authenticated, setAuthenticated] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const ok = await initKeycloak();
      if (cancelled) return;
      setAuthenticated(ok);
      setInitialized(true);
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const value: AuthState = {
    initialized,
    authenticated,
    keycloak,
    login: doLogin,
    logout: doLogout,
    userEmail: authenticated
      ? ((keycloak.tokenParsed?.email as string | undefined) ??
        (keycloak.tokenParsed?.preferred_username as string | undefined))
      : undefined,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used inside AuthProvider");
  return value;
}
