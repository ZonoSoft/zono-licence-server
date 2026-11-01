# Zono licence server

Free trials, registration, licences and the customer portal for Zono Workforce Suite (ZonoSoft). Small ASP.NET Core
service with SQLite, behind Caddy for HTTPS.

For the apps:
- `POST /api/v1/hello` — every launch: machine id (a one-way hash made on the device) → trial start date (30 days from
  the first time the machine — or any machine of the same business — was seen, so reinstalling or resetting the app
  doesn't restart it) and, once paid for, the signed licence token
- `POST /api/v1/register` — from the app: business, contact name, email, phone (with international dialling code),
  username and password, plan and team members → customer account and a licence waiting for payment
- `POST /api/v1/activate` — licence key + install id → signed licence token
- `POST /api/v1/refresh` — token → fresh token (status: active / expired / revoked)
- `POST /api/v1/deactivate` — frees the activation
- `GET /api/v1/public-key` — the public key the apps use to check tokens offline

For people:
- `/portal` — customers sign in to see their licence key, plan, payment and contact details, and change their password
- `/admin` — ZonoSoft's page: licences, "Mark paid", machines and trials, activity log (needs the admin key)

Security: tokens are signed with an ECDSA P-256 key made on the server (`/var/lib/zono-licence/signing-key.pem`).
Customers' personal details and licence keys are encrypted in the database (AES-256-GCM, key in
`/var/lib/zono-licence/secret.key`); usernames are looked up by keyed hash; passwords are PBKDF2-SHA256 hashes. Portal
sessions are HttpOnly, Secure, SameSite=Strict cookies. There are no secrets in this repository.

Install on Ubuntu, as root (afterwards it updates itself from this repository every 15 minutes):

    curl -fsSL -o i.sh https://raw.githubusercontent.com/ZonoSoft/zono-licence-server/main/deploy/install.sh; bash i.sh

© ZonoSoft. All rights reserved.
