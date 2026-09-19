# AI Diary

A self-hosted, encrypted personal diary. Notes are plain-text files stored
encrypted in S3-compatible object storage, indexed in Postgres, searched
semantically via pgvector, and queried through a chat agent running on the
user's own LLM endpoint.

Built for the operator plus fewer than ten invited users. Not a product, not
multi-tenant SaaS.

## Architecture

| Service    | Role                                                        | Exposed |
| ---------- | ----------------------------------------------------------- | ------- |
| `web`      | React SPA, static assets behind nginx/Traefik               | yes     |
| `api`      | ASP.NET Core — the only component holding the master key    | yes     |
| `postgres` | Identity index, note metadata, versions, embeddings, chats  | no      |
| `keycloak` | OIDC provider, user store, password and email flows         | yes     |
| `ollama`   | `bge-m3` embeddings                                         | no      |

Object storage is Backblaze B2 over the S3-compatible API, reached directly by
the browser through presigned URLs. On read, note bodies do not transit the
API: the browser fetches the ciphertext from B2 and the API returns the
unwrapped data key over TLS.

### Encryption

Three-level envelope encryption, AES-256-GCM throughout:

- **Master Key** — deployment-wide, `DIARY_MASTER_KEY` env var. Never in
  Postgres, never in a backup.
- **User Key (UKEK)** — one per user, wrapped by the master key and again by a
  recovery key.
- **Data Key (DEK)** — one per note (shared by its versions), wrapped by the
  UKEK.

Every object in B2 is self-describing (`WNMD1` magic, format version, note id,
version number, GCM nonce, ciphertext, tag). Note id + version are the GCM
AAD, so an attacker with bucket write access cannot swap one note's
ciphertext into another note's slot undetected.

**Not end-to-end encrypted.** The operator (and root on the host) can read
everything. See the build specification for the full threat model and the
migration path to E2EE — the ciphertext format is designed not to change when
that happens.

## Repository layout

```
src/AiDiary.Crypto/     shared crypto library (envelope, keys, base32)
src/AiDiary.Api/        ASP.NET Core API (migrations included)
src/AiDiary.Decryptor/  standalone .NET reference decryptor
tools/decryptor.py      standalone Python reference decryptor (stdlib only)
web/                    React + TypeScript SPA (Vite)
keycloak/               realm export for the "diary" realm
scripts/                dev helpers (Keycloak setup, Postgres init)
docker-compose.yml      local development stack
```

## Local development

```bash
cp .env.example .env
# set DIARY_MASTER_KEY:  openssl rand -base64 32
# set DIARY_ADMIN_EMAIL to your address

# stack + MinIO + Mailpit + one-shot seed (dev profile)
docker compose --profile dev up -d --build

# optional: real embeddings — this pulls bge-m3 into a local Ollama
docker compose --profile ollama up -d ollama-pull
```

- Web app: http://localhost:8081 (proxies `/api` to the API container)
- API: http://localhost:8080 — `/health/live`, `/health/ready`
- Keycloak admin: http://localhost:8082 (admin / admin, realm `diary`)
- MinIO console: http://localhost:9001, Mailpit: http://localhost:8025

The seed command (`dotnet AiDiary.Api.dll seed`) creates the application row
for `DIARY_ADMIN_EMAIL` with the admin flag, generates its keys and writes a
few sample notes. It also attempts to create the matching Keycloak user —
log in with that user via the Keycloak console (set the password there) or
grant the `diary-admin` realm role to any user to see the Admin screen.

### First login

On first login every user runs a one-time recovery setup: a 26-character
recovery key is shown **once**, with a downloadable recovery kit, and must be
re-entered to confirm it was saved. Rotating the recovery key rewraps the
UKEK and touches no note.

### The chat agent

Settings → set your own OpenAI-compatible endpoint, model and API key (the
key is encrypted under your UKEK and never returned). The agent can search
notes and propose drafts through a review pane — it never writes directly.
Endpoints without tool calling fall back to a JSON-only prompt.

## Exports

- `POST /api/export` — zip of plain `.md` (frontmatter + body).
- `POST /api/export/encrypted` — the archive the decryptors understand:
  ciphertext objects exactly as stored, `keys.json` (wrapped DEKs) and
  `recovery.dat` (recovery-wrapped UKEK).

Decrypt an encrypted export with only a recovery key:

```bash
# .NET
dotnet run --project src/AiDiary.Decryptor -- --key YOUR-KEY --dir EXPORT_DIR

# Python (standard library only)
python3 tools/decryptor.py --key YOUR-KEY --dir EXPORT_DIR
```

This is the guarantee that the archive outlives the application.

## Configuration

All variables are listed in `.env.example`. Highlights:

| Variable | Meaning |
| -------- | ------- |
| `DIARY_MASTER_KEY` | 256-bit base64. Generate with `openssl rand -base64 32`. Keep offline. |
| `DIARY_DB_CONNECTION` | Postgres connection string |
| `DIARY_S3_*` | B2 (or MinIO) endpoint, region, bucket, keys |
| `DIARY_OLLAMA_URL` / `DIARY_EMBED_MODEL` | Internal Ollama address, `bge-m3` |
| `DIARY_OIDC_*` | Keycloak authority, admin client id/secret, expected audience |
| `DIARY_CAPTCHA_PROVIDER` | `turnstile`, `hcaptcha` or `none` for the public request form |
| `DIARY_*_RETENTION_DAYS` | 90 (versions), 30 (trash), 30 (conversations) — policy, not tuning knobs |
| `DIARY_SMTP_*` | optional SMTP for admin notifications |

Additions beyond the specification's variable list: `DIARY_OIDC_AUDIENCE`
(default `diary-web`), `DIARY_CORS_ORIGINS`, `DIARY_BASE_URL` and `DIARY_SMTP_*`
— all with sensible defaults. One endpoint was added to the API surface:
`GET /api/chat/conversations/{id}` (the UI needs message history to render a
conversation).

### Keycloak

`keycloak/realm-export.json` is imported automatically by the dev stack:
realm `diary`, self-registration disabled, public SPA client `diary-web`
(PKCE), confidential client `diary-api` with a service account that holds the
realm-management roles the approval flow needs. If role assignment does not
come through on import, run:

```bash
./scripts/configure-keycloak.sh
```

SMTP is configured at the realm level (Keycloak admin → Realm settings →
Email, or `KC_SMTP_*` variables). **Without SMTP the approval flow cannot
complete** — approve-then-email is how invited users set their passwords.

### Local networking note

The API must reach Ollama, which in production lives in a different Coolify
stack with no published ports. Attach the API container to that stack's
network (or a shared external network). Do not publish an Ollama port to
solve this.

## Operations

- **Migrations** run automatically at startup; the initial migration creates
  the `vector` and `citext` extensions and an HNSW index on
  `note_embeddings` (`vector_cosine_ops`). On a managed database whose user
  cannot create extensions, run `CREATE EXTENSION IF NOT EXISTS vector;` and
  `... citext;` as the admin once.
- **Retention** runs nightly at 03:00 UTC (and once on startup): superseded
  versions after 90 days, trashed notes after 30 days, conversations after
  30 days, access-request IPs after 90 days. Deletes are B2-first, then
  Postgres. The clock starts at supersession, not creation.
- **Logging** is redacted by property name (bodies, titles, keys, presigned
  URLs, chat content) — a presigned URL in a log is a five-minute window onto
  a note for anyone reading the log.
- **Backups**: Postgres via Coolify; B2 via cross-region replication. The
  master key lives offline in a password manager — never in Coolify, never in
  the repo, never in a committed `.env`. Embeddings are not backed up; they
  are regenerated from notes.
- **Restore drill** belongs in the initial build: restore the Postgres dump
  to a scratch database, point a throwaway API at it with the real master
  key, confirm a note decrypts. An untested backup is a guess.

### Before inviting people

Working data export (`/api/export`), working deletion, a privacy notice
naming controller/purposes/location/retention, a stated retention period for
never-approved `access_requests`, and a processor relationship with Backblaze
(EU-region bucket). The privacy notice should say plainly that note content
is sent to whatever LLM endpoint each user configures.

## Deployment to a registry

Deliberately out of scope for now — the operator will supply the registry and
tagging scheme, after which only the `build`/`push` step changes.

## Security model in one line

B2 contents alone: unreadable. Postgres dump alone: metadata, wrapped keys,
invertible embeddings — no plaintext. Both together without the master key:
still unreadable. Master key + either store, or root on the host, or the
operator: full read. That is inherent to the model.
