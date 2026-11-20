# ZonoSoft licence server

Licences, demo keys, the customer portal, app updates and the zonosoft.co.uk website for Shifenza and Shifenza Team.
A small ASP.NET Core service (`src/ZonoSoft.Licence`) with SQLite, behind Caddy for HTTPS.

For the apps:
- `POST /api/v1/register` — from the app: a ZonoSoft account and a licence waiting for payment
- `POST /api/v1/activate` — licence key (ZSFT-…) + install id → signed licence token
- `POST /api/v1/refresh` — token → fresh token (status: active / expired / revoked)
- `POST /api/v1/deactivate` — frees the activation
- `POST /api/v1/update` — with a licence or demo token: is there a newer version (signed answer)
- `GET /api/v1/time`, `/api/v1/public-key`, `/api/v1/import-key`, `/api/v1/health`

For people:
- `/portal` — customers see their licence key, plan and payments, and download the apps
- `/admin` — ZonoSoft's page: licences, payments, downloads, the private vault (needs the admin key)

What's kept: accounts, licences and their seats, and sales and admin records. No IP addresses, no activity or download
logs (see the Project's data-protection notes).

Security: tokens are signed with an ECDSA P-256 key made on the server (`/var/lib/zonosoft-licence/signing-key.pem`).
Customers' details and licence keys are encrypted in the database (AES-256-GCM, key in
`/var/lib/zonosoft-licence/secret.key`); usernames are looked up by keyed hash; passwords are PBKDF2-SHA256 hashes.
Portal sessions are HttpOnly, Secure, SameSite=Strict cookies. There are no secrets in this repository.

Install on Ubuntu, as root (afterwards it updates itself from this repository every 15 minutes):

    curl -fsSL -o i.sh https://raw.githubusercontent.com/ZonoSoft/zonosoft-licence-server/main/deploy/install.sh; bash i.sh

© ZonoSoft. All rights reserved.
