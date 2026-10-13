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
| `keycloak` | OIDC provider and user store — shared, separate repository  | yes     |
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
docker-compose.yml      local development stack
docker-compose.prod.yml production stack (Coolify)
.github/workflows/      image build, push and Coolify redeploy
```

## Local development

### Prerequisites

- Docker Engine 20.10+ with the Compose v2 plugin (`docker compose`), or
  Docker Desktop
- `openssl` (to generate the master key)
- Optional, only for running the SPA outside Docker: Node.js 24

Free host ports: 8080 (API), 8083 (web, `WEB_PORT`), 5432 (Postgres),
9000/9001 (MinIO), 8025/1025 (Mailpit), plus 8082 and 8026 for the identity
stack.

### 1. Get both repositories side by side

Login runs through the shared identity server, which lives in its own
repository. The commands below assume it sits next to this one:

```bash
git clone https://github.com/birtans/AI-Diary.git
git clone <identity repository URL> identity
# workspace/
#   AI-Diary/
#   identity/
```

### 2. Start the identity server

```bash
cd identity
docker compose up -d
docker compose ps        # keycloak "healthy", keycloak-config "exited (0)"
```

`keycloak-config` creates the `diary` realm. If it shows a non-zero exit,
check `docker compose logs keycloak-config`. Details are in the identity
repository's README.

### 3. Configure this app

```bash
cd ../AI-Diary
cp .env.example .env
```

Edit `.env`:

- `DIARY_MASTER_KEY`: output of `openssl rand -base64 32`. Losing it makes
  every note unreadable, so keep a copy.
- `DIARY_ADMIN_EMAIL`: your address; the seed makes this user the admin.

The other values already match the dev stack. If you change `WEB_PORT`
from 8083, also set `DIARY_REALM_WEB_URL=http://localhost:<port>` in the
identity repository's `.env` and re-run `docker compose up keycloak-config`
there.

### 4. Start the stack

```bash
# API, web, Postgres + MinIO, Mailpit and the one-shot seed (dev profile)
docker compose --profile dev up -d --build

# optional: real embeddings (semantic search and chat need them). This
# starts Ollama and pulls bge-m3, a download of a few GB.
docker compose --profile ollama up -d ollama-pull
```

The API creates the MinIO bucket and runs database migrations on startup.

| What | URL |
| ---- | --- |
| Web app | http://localhost:8083 (`WEB_PORT`; proxies `/api` to the API) |
| API health | http://localhost:8080/health/live, `/health/ready` |
| Web health | http://localhost:8083/healthz |
| Keycloak admin (identity stack) | http://localhost:8082 (admin / admin) |
| Keycloak emails (identity stack) | http://localhost:8026 |
| App emails (Mailpit) | http://localhost:8025 |
| MinIO console | http://localhost:9001 (minioadmin / minioadmin) |

### 5. First sign-in

The seed (`dotnet AiDiary.Api.dll seed`) creates the application row for
`DIARY_ADMIN_EMAIL` with the admin flag, generates its keys, writes a few
sample notes and creates the matching Keycloak user. That user has no
password yet, and the Admin screen is gated on a Keycloak role, so:

1. Open http://localhost:8082 → switch to realm **diary** → *Users* → your
   email.
2. *Credentials* → *Set password*, with *Temporary* off.
3. *Role mapping* → *Assign role* → filter by realm roles →
   **diary-admin**.
4. Sign in at http://localhost:8083.

If the seed ran before Keycloak was ready, its log says it couldn't create
the Keycloak user. Re-run it with `docker compose --profile dev run --rm seed`.

### Running the SPA with hot reload (optional)

```bash
cd web
npm install
npm run dev      # http://localhost:5173, proxies /api to localhost:8080
```

The identity dev stack already allows `http://localhost:5173` as a redirect
URI.

### Troubleshooting

- **"Invalid parameter: redirect_uri"** on the login page: the web URL isn't
  in the realm. See the `WEB_PORT` note in step 3.
- **API returns 401 for every call**: the identity stack isn't running, or
  the API can't reach it at `host.docker.internal:8082`. Check
  `docker compose logs api`.
- **Admin screen missing**: the user lacks the `diary-admin` realm role
  (step 5).
- **Starting over**: `docker compose down -v` here and/or in the identity
  repository deletes that stack's volumes (notes, users).

### Recovery setup on first login

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

Keycloak is not part of this application. It's a shared identity server
with one realm per project, kept in its own repository and deployed as its
own Coolify resource. Neither side needs the other to be redeployed.

Its `realms/diary.json` defines realm `diary`: self-registration
disabled, public SPA client `diary-web` (PKCE), confidential client
`diary-api` with a service account that holds the realm-management roles
the approval flow needs, the `diary-user` / `diary-admin` roles, and the
realm's SMTP settings. It is applied on every deploy (create or update),
so a change there plus a redeploy of the identity server is all it takes.

**Without SMTP the approval flow cannot complete** — approve-then-email is
how invited users set their passwords. In dev it goes to the identity
stack's Mailpit.

The web image bakes the Keycloak URL in at build time: set the
`KEYCLOAK_URL` repository variable in GitHub before running the publish
workflow.

### Local networking note

The API must reach Ollama, which in production lives in a different Coolify
stack with no published ports, on that stack's private `ai-net` network.
`docker-compose.prod.yml` attaches the `api` service to it as an external
network named by `OLLAMA_NETWORK`, and reaches Ollama by its `ollama` alias.
Compose prefixes network names with the project (in Coolify, the resource
UUID), so look up the real name on the server:

```sh
docker network ls --filter name=ai-net
```

Do not publish an Ollama port to solve this.

## Production deployment

Production runs on Coolify from `docker-compose.prod.yml`: `postgres`,
`api` and `web`, with images pulled from the registry. Everything else is
external.

### Prerequisites

- **Identity server** deployed from the identity repository, with the
  `diary` realm applied, and its `DIARY_REALM_WEB_URL` set to this app's
  public web URL. Note its auth URL (e.g. `https://auth.example.com`) and
  the `DIARY_REALM_API_CLIENT_SECRET` you gave it.
- **S3-compatible bucket** (Backblaze B2 in an EU region), with an
  application key that can read, write and delete in it, and a CORS rule
  allowing `GET` from the web app's origin. The browser downloads note
  ciphertext straight from the bucket through presigned URLs.
- **Ollama** serving `bge-m3`, reachable from the API container (see
  *Local networking note* below).
- **SMTP** account for admin notifications.
- **Registry**: `registry.birtan.dev`. The Coolify server must be able to
  pull from it (`docker login` on the server, or a registry entry in
  Coolify).

### 1. Coolify resource

Create a **Docker Compose** resource from this repository with compose file
`docker-compose.prod.yml`. Don't deploy yet.

- **Domains**: `web` (e.g. `https://diary.example.com`) and `api`
  (e.g. `https://diary-api.example.com`). The web container also proxies
  `/api` to the API itself.
- **Environment**: the stack refuses to start until every required
  variable is set:

| Variable | Value |
| -------- | ----- |
| `DIARY_MASTER_KEY` | `openssl rand -base64 32`. Store the master copy offline first; see *Backups* |
| `POSTGRES_PASSWORD` | a strong random password |
| `DIARY_S3_ENDPOINT`, `DIARY_S3_PUBLIC_ENDPOINT` | the bucket endpoint, e.g. `https://s3.eu-central-003.backblazeb2.com` (both the same for B2) |
| `DIARY_S3_REGION`, `DIARY_S3_BUCKET` | the bucket's region and name |
| `DIARY_S3_ACCESS_KEY`, `DIARY_S3_SECRET_KEY` | the bucket's application key |
| `OLLAMA_NETWORK` | the Ollama stack's network, e.g. `<uuid>_ai-net` (see *Local networking note*). Defaults to `ai-net`, which only matches if that stack names its network explicitly |
| `DIARY_OIDC_AUTHORITY` | `https://auth.example.com/realms/diary` |
| `DIARY_OIDC_CLIENT_SECRET` | same value as `DIARY_REALM_API_CLIENT_SECRET` on the identity server |
| `DIARY_ADMIN_EMAIL` | your address (receives access-request notifications) |
| `DIARY_CORS_ORIGINS` | the public web URL |
| `DIARY_BASE_URL` | the public API URL |
| `DIARY_SMTP_HOST`, `DIARY_SMTP_FROM` | SMTP server and sender. Also set `DIARY_SMTP_PORT` (465 = TLS on connect, e.g. Resend; 587 = STARTTLS), `DIARY_SMTP_USERNAME`, `DIARY_SMTP_PASSWORD` as needed |

Optional: `DIARY_CAPTCHA_*` for the public access-request form,
`DIARY_OIDC_INTERNAL_URL` (leave empty to reach Keycloak via its public
URL), `DIARY_OLLAMA_URL` (defaults to `http://ollama:11434`), and the
retention variables.

Copy the resource's UUID from Coolify for the next step.

### 2. GitHub settings

In this repository → *Settings → Secrets and variables → Actions*:

| Kind | Name | Value |
| ---- | ---- | ----- |
| Secret | `REGISTRY_USER`, `REGISTRY_PASSWORD` | registry credentials |
| Secret | `COOLIFY_API_TOKEN` | Coolify API token with deploy permission |
| Secret | `COOLIFY_APP_UUID` | the resource UUID from step 1 |
| Variable | `KEYCLOAK_URL` | the auth URL, e.g. `https://auth.example.com` (no `/realms/…`) |
| Variable | `KEYCLOAK_REALM`, `KEYCLOAK_CLIENT_ID` | optional, default `diary` / `diary-web` |

`KEYCLOAK_URL` is baked into the web bundle at build time, so changing it
means rebuilding the web image.

### 3. Build and deploy

*Actions* → **Build and publish Docker images** → *Run workflow*. It builds
and pushes `ai-diary-web` and `ai-diary-api` (tagged `latest` and the commit
SHA), then triggers the Coolify deploy. On first start the API runs the
database migrations and creates the bucket if it doesn't exist.

Check that `https://<api-domain>/health/ready` returns healthy and that the
web app redirects to the Keycloak login page.

### 4. Create the first admin

There is no seed in production. Admin rights have two parts: the
`diary-admin` Keycloak role shows the Admin screen, and the `IsAdmin` flag
in the database authorizes the admin API.

1. In the identity server's admin console, realm **diary** → *Users* →
   *Add user*: your email (as in `DIARY_ADMIN_EMAIL`), *Email verified* on.
   Then *Credentials* → set a password (*Temporary* off), and *Role
   mapping* → assign **diary-user** and **diary-admin**.
2. Sign in to the web app once. This creates your application row; complete
   the recovery-key setup.
3. Set the admin flag:

   ```bash
   docker exec <ai-diary-postgres> psql -U diary -d diary \
     -c "UPDATE users SET \"IsAdmin\" = true WHERE \"Email\" = 'you@example.com';"
   ```

Everyone else comes in through *Request access* → you approve in the Admin
screen → Keycloak emails them a link to set their password.

### Updating

Run the workflow again; Coolify redeploys with the new `latest` images.
Migrations apply automatically. To roll back, set `IMAGE_TAG` in Coolify to
an earlier commit SHA and redeploy.

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
  master key's durable copy lives offline in a password manager. Coolify
  holds it only as the running API's environment variable, which is not a
  backup of it. Never in the repo, never in a committed `.env`. Embeddings
  are not backed up; they are regenerated from notes.
- **Restore drill** belongs in the initial build: restore the Postgres dump
  to a scratch database, point a throwaway API at it with the real master
  key, confirm a note decrypts. An untested backup is a guess.

### Before inviting people

Working data export (`/api/export`), working deletion, a privacy notice
naming controller/purposes/location/retention, a stated retention period for
never-approved `access_requests`, and a processor relationship with Backblaze
(EU-region bucket). The privacy notice should say plainly that note content
is sent to whatever LLM endpoint each user configures.

## Security model in one line

B2 contents alone: unreadable. Postgres dump alone: metadata, wrapped keys,
invertible embeddings — no plaintext. Both together without the master key:
still unreadable. Master key + either store, or root on the host, or the
operator: full read. That is inherent to the model.
