/**
 * Browser-side decryption of the WNMD1 envelope with WebCrypto (AES-256-GCM).
 * The ciphertext arrives via a presigned URL; the DEK arrives from the API.
 */
export async function decryptNoteBody(
  envelope: ArrayBuffer,
  dekBase64: string,
  noteId: string,
  version: number,
): Promise<string> {
  const bytes = new Uint8Array(envelope);
  const magic = new TextDecoder().decode(bytes.slice(0, 5));
  if (magic !== "WNMD1") throw new Error("Not an AI Diary encrypted object.");
  if (bytes[5] !== 1) throw new Error("Unsupported envelope version.");

  const idBytes = uuidToBytes(noteId);
  const versionBytes = new Uint8Array(4);
  new DataView(versionBytes.buffer).setUint32(0, version, false); // big-endian

  const nonce = bytes.slice(26, 38);
  const ciphertext = bytes.slice(38, bytes.length - 16);
  const tag = bytes.slice(bytes.length - 16);

  const dek = Uint8Array.from(atob(dekBase64), (c) => c.charCodeAt(0));
  const key = await crypto.subtle.importKey("raw", dek, { name: "AES-GCM" }, false, ["decrypt"]);

  // WebCrypto expects the tag appended to the ciphertext.
  const combined = new Uint8Array(ciphertext.length + tag.length);
  combined.set(ciphertext);
  combined.set(tag, ciphertext.length);

  const aad = new Uint8Array(idBytes.length + versionBytes.length);
  aad.set(idBytes);
  aad.set(versionBytes, idBytes.length);

  const plaintext = await crypto.subtle.decrypt(
    { name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: 128 },
    key,
    combined,
  );
  return new TextDecoder().decode(plaintext);
}

function uuidToBytes(uuid: string): Uint8Array {
  const hex = uuid.replace(/-/g, "");
  const bytes = new Uint8Array(16);
  for (let i = 0; i < 16; i++) {
    bytes[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  }
  return bytes;
}

export function timezoneOf(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  } catch {
    return "UTC";
  }
}

export function todayIso(): string {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}
