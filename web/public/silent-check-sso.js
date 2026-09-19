// Used by Keycloak's silent SSO check; the actual handling happens in the
// Keycloak adapter itself, this just gives the redirect a page to land on.
window.parent.postMessage(location.href, location.origin);
