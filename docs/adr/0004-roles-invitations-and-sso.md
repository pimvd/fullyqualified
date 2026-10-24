# 4. Roles, invitations and single sign-on

Status: proposed. Builds on ADR 0003.

- **Roles:** `Viewer` reads everything and downloads reports, `Member` also adds and deletes assets and starts scans, `Admin` also manages people. Each role includes the previous one. The first account is an admin. Roles are checked on the server: policies on the `/admin` endpoints and the Users page, and a check in each asset action; the UI only hides what a role may not do.
- **Sessions follow the database:** the cookie carries the role, but each request re-reads the user (cached for 30 seconds, cleared at once when an admin changes or removes someone). A demoted or removed person loses access immediately, not after the 14-day cookie.
- **Guards:** there is always at least one admin, and nobody removes themselves.
- **Invitations:** an admin creates a single-use link (7 days) with a role and an optional email address. With an SMTP server configured (`Alerts:Email`) the link is mailed. The link opens the passkey registration page. An invitation with an address can also be accepted by signing in through SSO with that address. Pending invitations can be revoked.
- **SSO:** one OpenID Connect provider, configured in `Sso:*` (authorization code flow with PKCE). SAML is not supported. A sign-in is matched, in order, to a stored identity (issuer + subject), to an existing user with the same e-mail address, to a pending invitation for that address, and only if `Sso:AutoProvision` is set, to a new account with `Sso:DefaultRole` (never admin). Otherwise it is refused.
- **E-mail trust:** an address from the provider is only used to link or create accounts if its domain is in `Sso:AllowedDomains`, or, without that setting, if the provider marks it verified. Entra ID does not send that flag, so set the domains there.
- **First account:** SSO cannot create it. While there are no users, `/setup` with a passkey is the only way in, so nobody can claim an empty instance through an identity provider.
- **Passkeys stay available** next to SSO. Enforcing SSO-only, SCIM provisioning, groups-to-roles mapping and per-asset permissions are not included.
- **Data:** `users` gains `email` and `role`, `invites` gains `email` and `role`, and `external_logins` links users to provider identities. Existing users become admins in the migration, since they had full access before.
