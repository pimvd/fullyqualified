# 3. Accounts with passkeys

Status: proposed

- **Decision:** accounts sign in with passkeys (WebAuthn) only. There are no passwords, so there is nothing to leak, reuse or reset. This replaces the shared HTTP Basic password of the first release.
- **Library:** Fido2NetLib (MIT) verifies registrations and sign-ins. Credentials are discoverable, so the login page asks for no user name.
- **Session:** an ASP.NET Core cookie (HttpOnly, SameSite=Lax, 14 days sliding). Every page and endpoint requires it by default (fallback policy); only `/health`, `/culture`, `/login`, `/setup`, `/invite/{token}` and the sign-in half of `/auth` are anonymous.
- **Challenge state:** between the options and completion calls it sits in a 5-minute protected cookie, so the server stays stateless. Data Protection keys are stored in Postgres so cookies survive restarts and redeploys.
- **Tables:** `users`, `passkeys` and `invites` carry `tenant_id` like every table. `data_protection_keys` is infrastructure owned by the framework and has none.
- **Onboarding:** while there are no users, `/setup` creates the first account; set `Auth:SetupCode` to stop a stranger from claiming an instance that is reachable before setup. After that, people join through single-use invitation links (7 days, only a hash is stored) created by a signed-in user.
- **Recovery:** a user keeps several passkeys (the last one cannot be removed). Losing all of them means asking a colleague for a new invitation; the old account stays. For a single-user install the database is the last resort.
- **Relying party:** the domain and origin come from the request host. Behind a proxy that rewrites the host, set `Auth:RpId` and `Auth:Origin`. Passkeys need HTTPS, except on `localhost`.
- **Not included:** roles and permissions, SSO, account deletion, an admin list of users. Passkeys registered on a host name stay bound to it.
