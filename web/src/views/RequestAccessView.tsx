import { useEffect, useRef, useState } from "react";
import type { PublicConfig } from "../api/types";
import { api } from "../api/client";
import { useAuth } from "../auth/AuthContext";

declare global {
  interface Window {
    turnstile?: {
      render: (el: HTMLElement, opts: { sitekey: string; callback: (token: string) => void }) => string;
      getResponse: (widgetId?: string) => string;
      remove: (widgetId: string) => void;
    };
  }
}

/**
 * Public landing: sign in, or request access. This is the only screen
 * strangers see; the API treats the request endpoint accordingly.
 */
export function RequestAccessView() {
  const { login } = useAuth();
  const [config, setConfig] = useState<PublicConfig>({ captchaProvider: "", captchaSiteKey: "" });
  const [email, setEmail] = useState("");
  const [message, setMessage] = useState("");
  const [captchaToken, setCaptchaToken] = useState("");
  const [sent, setSent] = useState(false);
  const [error, setError] = useState("");
  const captchaRef = useRef<HTMLDivElement>(null);
  const widgetId = useRef<string>("");

  useEffect(() => {
    void (async () => {
      try {
        const loaded = await api.get<PublicConfig>("/api/config");
        setConfig(loaded);
        if (loaded.captchaProvider === "turnstile") {
          loadTurnstile(() => {
            if (captchaRef.current) {
              widgetId.current = window.turnstile!.render(captchaRef.current, {
                sitekey: loaded.captchaSiteKey,
                callback: (token) => setCaptchaToken(token),
              });
            }
          });
        }
      } catch {
        // config endpoint is best-effort
      }
    })();
    return () => {
      if (widgetId.current && window.turnstile) window.turnstile.remove(widgetId.current);
    };
  }, []);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setError("");
    setSent(false);
    try {
      await api.post("/api/access-requests", { email, message, captchaToken: captchaToken || null });
      setSent(true);
      setEmail("");
      setMessage("");
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not send the request.");
    }
  };

  return (
    <div className="landing">
      <div className="landing-card">
        <h1>AI Diary</h1>
        <p className="muted">A private, encrypted diary. Access is by invitation.</p>
        <button type="button" className="primary-button" onClick={() => void login()}>
          Sign in
        </button>
        <hr />
        <h2>Request access</h2>
        {sent ? (
          <p className="success-text">Thanks — your request was received.</p>
        ) : (
          <form onSubmit={(e) => void submit(e)}>
            <label>
              Email
              <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
            </label>
            <label>
              Why do you want in? <span className="muted">(optional)</span>
              <textarea rows={3} value={message} onChange={(e) => setMessage(e.target.value)} maxLength={500} />
            </label>
            {config.captchaProvider === "turnstile" && <div ref={captchaRef} />}
            {error && <p className="error-text">{error}</p>}
            <button type="submit" className="primary-button" disabled={!email}>
              Send request
            </button>
          </form>
        )}
      </div>
    </div>
  );
}

function loadTurnstile(onLoaded: () => void) {
  if (window.turnstile) {
    onLoaded();
    return;
  }
  const script = document.createElement("script");
  script.src = "https://challenges.cloudflare.com/turnstile/v0/api.js";
  script.async = true;
  script.onload = onLoaded;
  document.head.appendChild(script);
}
