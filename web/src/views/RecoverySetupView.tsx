import { useCallback, useEffect, useState } from "react";
import JSZip from "jszip";
import type { RecoveryStatusResponse } from "../api/types";
import { api, ApiError } from "../api/client";
import { useAuth } from "../auth/AuthContext";

const KIT_README = `AI Diary recovery kit
========================

Your recovery key:

    {key}

Keep this file somewhere safe and separate from your encrypted archive.
If you ever lose access to the AI Diary deployment, download an encrypted
export and decrypt it with the reference decryptor:

    dotnet run --project src/AiDiary.Decryptor -- --key YOUR-KEY --dir EXPORT_DIR
    python3 tools/decryptor.py --key YOUR-KEY --dir EXPORT_DIR
`;

async function downloadKit(recoveryKey: string, userId: string) {
  const exported = await api.get<{ userId: string; recoveryWrappedUkek: string }>("/api/recovery/export");
  const zip = new JSZip();
  zip.file("recovery-key.txt", recoveryKey);
  zip.file(
    "recovery.dat",
    JSON.stringify({ user_id: exported.userId, recovery_wrapped_ukek: exported.recoveryWrappedUkek }),
  );
  zip.file("README.txt", KIT_README.replace("{key}", recoveryKey));
  const blob = await zip.generateAsync({ type: "blob" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `ai-diary-recovery-${userId.slice(0, 8)}.zip`;
  link.click();
  URL.revokeObjectURL(url);
}

/**
 * First-login recovery flow. The key is shown exactly once; the user must
 * re-enter it to prove they saved it. Losing it is survivable while the
 * deployment is healthy (rotate it) — it matters when the deployment is gone.
 */
export function RecoverySetupView({ onDone }: { onDone: () => void }) {
  const [status, setStatus] = useState<RecoveryStatusResponse["status"] | "loading">("loading");
  const [recoveryKey, setRecoveryKey] = useState<string | null>(null);
  const [userId, setUserId] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState("");
  const [error, setError] = useState("");
  const { logout } = useAuth();

  const refresh = useCallback(async () => {
    try {
      const result = await api.get<RecoveryStatusResponse>("/api/recovery/status");
      setStatus(result.status);
      if (result.status === "ok") onDone();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not check recovery status.");
    }
  }, [onDone]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const setup = async () => {
    setError("");
    try {
      const result = await api.post<{ status: string; recoveryKey?: string; userId?: string }>("/api/recovery/setup");
      if (result.recoveryKey && result.userId) {
        setRecoveryKey(result.recoveryKey);
        setUserId(result.userId);
        setStatus("setup_pending_ack");
      } else {
        setError("Recovery was already set up. If you lost the key, rotate it.");
        await refresh();
      }
    } catch (ex) {
      setError(ex instanceof ApiError ? ex.detail : "Setup failed.");
    }
  };

  const rotate = async () => {
    setError("");
    try {
      const result = await api.post<{ recoveryKey?: string; userId?: string }>("/api/recovery/rotate");
      setRecoveryKey(result.recoveryKey ?? null);
      setUserId(result.userId ?? null);
      setConfirmation("");
    } catch (ex) {
      setError(ex instanceof ApiError ? ex.detail : "Rotation failed.");
    }
  };

  const acknowledge = async () => {
    setError("");
    try {
      await api.post("/api/recovery/ack", { recoveryKey: confirmation });
      setRecoveryKey(null);
      await refresh();
    } catch (ex) {
      setError(ex instanceof ApiError ? ex.detail : "That key does not match.");
    }
  };

  if (status === "loading") {
    return (
      <div className="recovery-screen">
        <p className="muted">Loading…</p>
      </div>
    );
  }

  return (
    <div className="recovery-screen">
      <div className="recovery-card">
        <h2>Set up your recovery key</h2>
        <p>
          Everything you write is encrypted. If this diary's server is ever lost, your recovery key is the only way
          to decrypt your archive. It is shown <strong>once</strong>.
        </p>

        {recoveryKey && (
          <>
            <p className="recovery-key">{recoveryKey}</p>
            <div className="notice-actions">
              <button
                type="button"
                className="primary-button"
                onClick={() => void downloadKit(recoveryKey, userId ?? "")}
              >
                Download recovery kit
              </button>
              <button
                type="button"
                className="secondary-button"
                onClick={() => {
                  setRecoveryKey(null);
                  setConfirmation("");
                }}
              >
                I saved it — continue
              </button>
            </div>
            <p className="muted">
              The kit contains the key, your user id and instructions. Store the key somewhere durable — a password
              manager, a safe, or paper.
            </p>
          </>
        )}

        {!recoveryKey && status === "not_setup" && (
          <button type="button" className="primary-button" onClick={() => void setup()}>
            Generate recovery key
          </button>
        )}

        {!recoveryKey && status === "setup_pending_ack" && (
          <>
            <p className="muted">
              A recovery key was already generated. If you lost it, you can rotate it now — rotating rewraps your
              user key and touches no notes.
            </p>
            <div className="notice-actions">
              <button type="button" className="primary-button" onClick={() => void rotate()}>
                Rotate recovery key
              </button>
            </div>
            <label>
              Or confirm the key you saved
              <input value={confirmation} onChange={(e) => setConfirmation(e.target.value)} placeholder="XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX" />
            </label>
            <button type="button" className="secondary-button" onClick={() => void acknowledge()} disabled={!confirmation}>
              Confirm
            </button>
          </>
        )}

        {error && <p className="error-text">{error}</p>}
        <p>
          <button type="button" className="link-button" onClick={() => void logout()}>
            Sign out
          </button>
        </p>
      </div>
    </div>
  );
}
