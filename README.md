# Zono licence server

Activates and refreshes licences for Zono Workforce Suite (ZonoSoft). Small ASP.NET Core service with SQLite, behind
Caddy for HTTPS.

- `POST /api/v1/activate` — licence key + install id → signed licence token
- `POST /api/v1/refresh` — token → fresh token (status: active / expired / revoked)
- `POST /api/v1/deactivate` — frees the activation
- `GET /api/v1/public-key` — the public key the apps use to check tokens offline
- `/admin` — ZonoSoft's page for making and managing licences (needs the admin key)

Tokens are signed with an ECDSA P-256 key created on the server (`/var/lib/zono-licence/signing-key.pem`); it never
leaves the server. Licence keys are stored only as hashes. There are no secrets in this repository.

Install or update on Ubuntu, as root:

    curl -fsSL https://raw.githubusercontent.com/ZonoSoft/zono-licence-server/main/deploy/install.sh | bash

© ZonoSoft. All rights reserved.
