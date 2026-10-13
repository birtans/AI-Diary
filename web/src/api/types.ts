export interface NoteLocation {
  lat: number;
  lon: number;
  label?: string;
}

export interface NoteListItem {
  id: string;
  title: string;
  entryDate: DateOnlyString;
  currentVersion: number;
  deletedAt?: string;
  origin: "user" | "agent";
}

export type DateOnlyString = string; // YYYY-MM-DD

export interface NoteDetail {
  id: string;
  title: string;
  entryDate: DateOnlyString;
  createdAt: string;
  updatedAt: string;
  timezone: string;
  location?: NoteLocation | null;
  currentVersion: number;
  revision: string; // opaque concurrency token, echoed back as expectedRevision
  origin: "user" | "agent";
  deletedAt?: string | null;
  presignedUrl: string;
  dek: string; // base64 of the unwrapped 32-byte data key
}

export interface NoteVersionItem {
  versionNo: number;
  filename: string;
  byteSize: number;
  createdAt: string;
  supersededAt?: string | null;
}

export interface DiffResponse {
  versionA: number;
  versionB: number;
  html: string;
}

export interface SearchResultItem {
  noteId: string;
  entryDate: DateOnlyString;
  title: string;
  excerpt: string;
  score: number;
}

export interface ConversationListItem {
  id: string;
  title: string;
  lastMessageAt: string;
}

export interface ChatMessageItem {
  id: string;
  role: "user" | "assistant";
  body: string;
  citedNoteIds: string[];
  createdAt: string;
}

export interface SettingsResponse {
  endpointUrl?: string;
  model?: string;
  hasApiKey: boolean;
}

export interface AccessRequestItem {
  id: string;
  email: string;
  message?: string;
  status: "pending" | "approved" | "rejected";
  requestedAt: string;
}

export interface AdminUserItem {
  id: string;
  email: string;
  isAdmin: boolean;
  noteCount: number;
  createdAt: string;
  isCurrentUser: boolean;
}

export interface RecoveryStatusResponse {
  status: "not_setup" | "setup_pending_ack" | "ok";
}

export interface RecoverySetupResponse {
  status: string;
  recoveryKey?: string;
  userId?: string;
}

export interface PublicConfig {
  captchaProvider: string;
  captchaSiteKey: string;
}

export interface ChatDraft {
  title: string;
  body: string;
  entryDate: string;
}
