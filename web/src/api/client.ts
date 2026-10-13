export class ApiError extends Error {
  status: number;
  detail: string;
  type?: string;
  extensions: Record<string, unknown>;

  constructor(status: number, detail: string, type?: string, extensions: Record<string, unknown> = {}) {
    super(detail);
    this.status = status;
    this.detail = detail;
    this.type = type;
    this.extensions = extensions;
  }
}

let auth: { getToken: () => Promise<string | undefined>; login: () => void } | null = null;

export function attachAuth(instance: typeof auth) {
  auth = instance;
}

const baseUrl = (import.meta.env.VITE_API_URL as string | undefined) ?? "";

async function buildHeaders(json = false): Promise<Headers> {
  const headers = new Headers();
  if (json) headers.set("Content-Type", "application/json");
  if (auth) {
    const token = await auth.getToken();
    if (token) headers.set("Authorization", `Bearer ${token}`);
  }
  return headers;
}

async function handleError(response: Response): Promise<never> {
  let detail = response.statusText || "Request failed";
  let type: string | undefined;
  let extensions: Record<string, unknown> = {};
  try {
    const problem = await response.json();
    if (problem && typeof problem === "object") {
      const top = problem as Record<string, unknown>;
      detail = (top.title as string | undefined) ?? detail;
      type = top.type as string | undefined;
      // ProblemDetails.Extensions serialise inline (JsonExtensionData).
      extensions = (top.extensions as Record<string, unknown>) ?? {};
      if (typeof top.currentVersion === "number") extensions.currentVersion = top.currentVersion;
      if (typeof top.currentRevision === "string") extensions.currentRevision = top.currentRevision;
    }
  } catch {
    // not a JSON problem details body
  }
  if (response.status === 401 && auth) {
    auth.login();
  }
  throw new ApiError(response.status, detail, type, extensions);
}

export async function apiGet<T>(path: string): Promise<T> {
  const response = await fetch(baseUrl + path, { headers: await buildHeaders() });
  if (!response.ok) return handleError(response);
  return response.json() as Promise<T>;
}

export async function apiSend<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(baseUrl + path, {
    method,
    headers: await buildHeaders(body !== undefined),
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) return handleError(response);
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

export const api = {
  get: apiGet,
  post: <T>(path: string, body?: unknown) => apiSend<T>("POST", path, body),
  put: <T>(path: string, body?: unknown) => apiSend<T>("PUT", path, body),
  patch: <T>(path: string, body?: unknown) => apiSend<T>("PATCH", path, body),
  delete: <T>(path: string) => apiSend<T>("DELETE", path),
};

/**
 * Raw fetch of a presigned URL — no auth header, cross-origin to B2/MinIO.
 * no-store: the working copy is overwritten in place under the same key.
 */
export async function fetchPresigned(url: string): Promise<ArrayBuffer> {
  const response = await fetch(url, { cache: "no-store" });
  if (!response.ok) {
    throw new ApiError(response.status, "Failed to fetch the encrypted note from object storage.");
  }
  return response.arrayBuffer();
}

/**
 * POST with a streaming response parsed as SSE lines ("data: {...}").
 * Calls onEvent for every parsed event; rejects with ApiError when the server
 * sends an error event.
 */
export async function apiStream(
  path: string,
  body: unknown,
  onEvent: (event: Record<string, unknown>) => void,
): Promise<void> {
  const response = await fetch(baseUrl + path, {
    method: "POST",
    headers: await buildHeaders(true),
    body: JSON.stringify(body),
  });
  if (!response.ok) return handleError(response);
  if (!response.body) throw new ApiError(500, "No response stream.");

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let index;
    while ((index = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, index).trim();
      buffer = buffer.slice(index + 1);
      if (!line.startsWith("data:")) continue;
      const payload = line.slice(5).trim();
      if (!payload || payload === "[DONE]") continue;
      try {
        onEvent(JSON.parse(payload));
      } catch {
        // ignore malformed events
      }
    }
  }
}
