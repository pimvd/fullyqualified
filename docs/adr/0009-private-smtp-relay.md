# 9. Private SMTP relay in the Compose environment

Status: proposed

## Context

The application already sends alarms, invitations and user-requested email authentication tests through its server-wide SMTP configuration. The Compose environment does not provide a mail server. Commercial submission providers typically require verified sender identities, which limits controlled tests of unauthorized sending.

## Decision

Add a Postfix service built from the official Debian slim image and Debian's Postfix package. Keep the application-facing SMTP interface unchanged. Postfix delivers directly to recipient MX hosts and preserves the submitted sender without adding DKIM signatures.

Only the application and Postfix join a dedicated mail network. Postfix trusts that network and loopback, rejects other clients and has no published host port. Persist its queue in a named volume, log to stdout and wait for its health check before starting the application. The public SMTP hostname, default sender and network subnet are operator settings. The default `.invalid` identities support local startup only.

The existing admin authorization, per-tenant send limits and tenant-scoped test logs remain in force. Tests are explicit user actions. SMTP acceptance means queued for delivery; the receiver's authentication results determine the DMARC outcome. DMARC report ingestion remains out of scope. No customer DNS or mail configuration is changed.

## Consequences

External delivery requires outbound TCP port 25, a suitable public IP and hostname, reverse DNS and any provider-specific SMTP enablement. Network or reputation failures can prevent delivery independently of DMARC. Submission on the private Docker network is plaintext; outbound delivery uses opportunistic TLS. There is no public inbound mail service or mailbox hosting.

The relay is also the default SMTP route for alarms and invitations. Operators can override the server-wide SMTP settings to use an external provider instead. A dedicated test route and a production alarm provider can be considered separately if both are needed simultaneously.

Architect review is required before adopting this proposed deployment change, as required by `AGENTS.md`. This ADR does not claim approval.
