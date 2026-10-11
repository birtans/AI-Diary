import Keycloak from "keycloak-js";

const keycloak = new Keycloak({
  url: (import.meta.env.VITE_KEYCLOAK_URL as string | undefined) || "http://localhost:8082",
  realm: (import.meta.env.VITE_KEYCLOAK_REALM as string | undefined) || "diary",
  clientId: (import.meta.env.VITE_KEYCLOAK_CLIENT_ID as string | undefined) || "diary-web",
});

export default keycloak;

export async function initKeycloak(): Promise<boolean> {
  try {
    return await keycloak.init({
      onLoad: "check-sso",
      silentCheckSsoRedirectUri: `${window.location.origin}/silent-check-sso.html`,
      pkceMethod: "S256",
      checkLoginIframe: false,
    });
  } catch (error) {
    console.error("Keycloak init failed", error);
    return false;
  }
}

export async function login(): Promise<void> {
  await keycloak.login({ redirectUri: window.location.origin + window.location.pathname });
}

export async function logout(): Promise<void> {
  await keycloak.logout({ redirectUri: window.location.origin });
}

export async function getToken(): Promise<string | undefined> {
  try {
    await keycloak.updateToken(30);
  } catch {
    return undefined;
  }
  return keycloak.token;
}
