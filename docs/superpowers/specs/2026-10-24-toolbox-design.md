# Authenticated, transient diagnostic toolbox

Status: approved by the user in conversation on 2026-10-24. This document does not approve an architectural change.

## Intent and agreed requirements

Build the complete discussed toolbox in one delivery, inside the existing FullyQualified application. Hosted and self-hosted deployments use the same implementation and feature set. Every toolbox page and execution endpoint requires an authenticated session. Public tools may be considered later; no anonymous execution is introduced now.

Checks are independent of monitoring assets: entering a target does not create an asset, job, baseline, finding, certificate record, alarm or report entry. There is no history. Inputs and results are transient and must not be written to the database, files, application logs, browser storage or result caches. Refreshing or leaving the page clears them.

Existing scheduled monitoring and its history continue to work independently.

## Approaches considered

1. Transient, on-demand tools returning their result directly to the requesting browser. Minimal state and no retention. The user selected this approach explicitly.
2. Saved diagnostic runs with organization history and comparisons. Rejected by the user: nothing should be saved.

The implementation uses the existing .NET 10, minimal API, Blazor SSR, localization and check/service conventions. No new application, database table, external scanner service or hosted-only edition is introduced. Existing `ICheck` plugins remain the interface for monitoring checks; toolbox orchestration can call shared diagnostic helpers without invoking the monitoring runner or adding another plugin system.

## Included tools

### TLS endpoint inspection

Accept a hostname or IPv4/IPv6 address, an explicit port (default 443), and an optional separate TLS server name for SNI and hostname validation. Treat target address and expected server identity as separate fields. Support internal single-label hostnames when the installation permits private targets.

Display the connected address, SNI/expected identity, certificate subject and issuer, DNS/IP subject alternative names, validity period, SHA-256 fingerprint, public-key information, negotiated TLS protocol/cipher and validation errors. Display the peer certificate chain and chain-building findings separately; do not imply every locally built intermediate was supplied by the peer. Inspect invalid and expired certificates while clearly reporting validation failures.

Probe TLS 1.0, 1.1, 1.2 and 1.3 individually. Distinguish accepted, rejected, unreachable and untestable locally: a protocol disabled or unavailable in the scanner's runtime is not evidence of remote rejection. Do not change system crypto policy to enable obsolete protocols.

Probe a bounded, documented set of cipher suites with one suite offered per handshake where the runtime supports that control. Report accepted suites and weak configurations. Display the candidate list and the scanner's limitations; do not claim exhaustive coverage of all historical suites or independent TLS 1.3 suite control where unavailable. Report local platform limitations explicitly on supported deployments.

Resolve all target A/AAAA addresses and inspect a bounded maximum of eight addresses, showing per-address results and any omitted count. This provides IPv4/IPv6 and multi-server certificate/configuration comparison. Pin every connection to the checked address, preserving SNI, to avoid a second DNS lookup bypassing address restrictions.

### Mail TLS / STARTTLS

Support SMTP STARTTLS (default port 25 or explicit 587), IMAP STARTTLS (143), POP3 STLS (110), and implicit TLS for SMTP (465), IMAP (993) and POP3 (995). Use bounded protocol parsers, correct SMTP multiline replies, positive upgrade replies and protocol-appropriate preambles. Inspect TLS after negotiation using the shared TLS inspection engine. Never authenticate or send a message during these diagnostic handshakes.

For a mail domain, resolve MX records and check their SMTP STARTTLS capabilities and certificates, with a maximum of ten MX hosts. Explicitly report null MX and a domain without MX, rather than silently choosing an unrelated endpoint.

### Certificate file inspection

Accept pasted PEM certificate blocks or a size-limited certificate upload; support PEM chains and a single DER certificate. Inspect names, issuer, validity, fingerprint, key information and the chain using submitted certificates as intermediates. Reject private-key material and do not upload it to third parties. Distinguish local inspection from validation against a live server. No temporary upload files are needed.

### DNS lookup and resolver comparison

Expose existing DNS lookup capabilities in the toolbox, including A, AAAA, CNAME, MX, NS, TXT, SOA, CAA, DS, DNSKEY, SRV and PTR. Compare the installation's configured recursive resolvers and discovered authoritative nameservers, showing response codes, TTLs, timing and answer differences. Normalize unordered answers and ignore TTL differences when identifying differing record data. Clearly distinguish errors, NXDOMAIN and empty answers.

Apply the same target-address policy to discovered authoritative nameservers; do not accept arbitrary user-provided resolver destinations. Disable result caching for toolbox requests.

### SPF analysis

Parse the published SPF record and expand `include` and `redirect` paths with cycle detection, recursion and overall work bounds. Report multiple SPF records, syntax problems, overly permissive mechanisms, missing terminal policy, DNS failures and lookup-budget risks. Count lookup-causing terms according to SPF evaluation rules; label static worst-case expansion separately from an actual sender-IP evaluation. Provide optional sender IP, envelope sender and HELO inputs for a concrete evaluation. Avoid presenting static linting as proof that a real message will pass.

### DKIM inspection

Accept a domain and selector and inspect the corresponding TXT key record: syntax, key type, RSA key size where applicable, revoked/empty keys and DNS errors. Reuse existing DNS key parsing where possible. Do not claim that key discovery validates a message signature. Message signature verification requires the original signed message and is outside the header-only analyzer.

### DMARC inspection and record builder

Inspect published DMARC records, policy, subdomain policy, strict/relaxed alignment and report destinations. Validate syntax, multiplicity and external reporting authorization where applicable. Recognize organizational-domain fallback using a maintained public-suffix data source; do not infer registrable domains by taking the last two labels.

Build an example TXT record from explicit form fields, with an explanation of each selected setting. Return text only: no DNS publishing, report ingestion or automatic policy changes.

### Mail header analyzer

Accept pasted message headers with folded and repeated fields. Show the Received route, From, Return-Path, DKIM signing identities and Authentication-Results / Received-SPF information. Explain reported SPF/DKIM/DMARC results and alignment using the identities actually available. Treat pasted authentication results as unverified assertions; without a trusted receiver boundary, do not certify that the message passed authentication. Do not infer delivery proof from headers or relay acceptance.

### MTA-STS and TLS-RPT

Inspect `_mta-sts` TXT, retrieve the policy over HTTPS at the defined MTA-STS hostname, and check policy syntax, mode, max_age and MX pattern coverage. Inspect `_smtp._tls` reporting TXT records and destinations. Validate HTTPS and restrict each resolved address and redirect; redirects should be disabled unless explicitly permitted by the protocol implementation. Bound downloaded bytes and parsing time. Sending reports or accepting reports is excluded.

### Existing DMARC sending test

Make the existing sending test discoverable from the toolbox and remove its `AlertLogEntry` writes, so it also has no application history. Preserve its admin-only access, sender-domain authorization and per-tenant sending limits. This exception requires an already authorized sender domain because it transmits mail; ordinary diagnostic tools accept unmonitored targets. Existing historical email-test log entries are not deleted as part of this feature.

SMTP delivery necessarily uses the relay's mail queue and produces receiver-side records. Explain this before sending: the no-storage guarantee covers toolbox application inputs/results, not the receiving mailbox, configured relay queue/logs or external services. Do not claim that SMTP acceptance establishes a DMARC outcome. Do not add a second unrestricted mail-sending endpoint.

## Access, privacy and operational behavior

All signed-in roles may run read-only diagnostics; retain the existing admin restriction for sending test mail. Check authorization server-side on every request. Execution uses POST bodies, not target-bearing URL query parameters, to reduce accidental input logging and URL history. Protect cookie-authenticated POST requests against cross-site requests using the existing antiforgery conventions. Return clear 401/403 status codes to scripts.

Use `Cache-Control: no-store` for toolbox pages/results. Do not use localStorage, sessionStorage, indexedDB, service-worker caches or retained result state. Clear inputs/results on reset and navigation, including browser back/forward cache restoration where needed. Render untrusted values as text. Neither headers, uploaded material nor target values appear in exception messages emitted to application logs. Use error codes and localized safe descriptions.

Authentication still reads the existing user/tenant session and can renew its cookie; this does not persist diagnostic data. In-memory rate/concurrency counters contain identities and counts, not target inputs or results, and expire promptly.

Respect `Scanning:AllowPrivateAddresses` for all direct, resolved and discovered destinations, including redirects and MX/NS expansion. Preserve one product version with configuration controlling allowed network reachability. Apply stricter hosted configuration without a hosted-only code path. Do not open customer environments for writing.

Suggested initial limits, left as documented implementation choices: 64 KiB pasted headers/certificates, 256 KiB certificate uploads/HTTP policy fetches, four concurrent network tools per user, sixteen per process, and thirty requests per minute per user. Bound each network connection to five seconds and an entire run to sixty seconds. Keep limits configurable and return partial observations with explicit timeout/limit status; never turn an incomplete probe into a successful finding. No background job table or polling history is used.

## User experience and localization

Add an authenticated Toolbox navigation entry and `/toolbox` page with tool selection, relevant input fields, a run button, progress/cancel/reset controls and structured findings. Preserve the existing application style and responsive/accessibility conventions. Inputs/results remain in the current page only; language changes perform a navigation and clear them.

Use English code, resource keys and documentation. All labels, validation, explanations, errors and findings use `IStringLocalizer` with matching English and Dutch resource files. Technical observations such as DNS records and cipher identifiers remain literal values. Do not display backend exception text as user-facing guidance.

## Verification and delivery

Track the work in a GitHub issue and deliver through a pull request for human review and merge, per `AGENTS.md`. Do not merge. If implementation requires a structural architectural change, propose an ADR and obtain the architect's approval before adopting it; this design is not that approval.

Test parsers with malformed/folded/duplicate records, SPF cycles and lookup boundaries, DMARC public-suffix behavior, certificate formats and private-key rejection. Use deterministic local TCP/TLS fixtures for TLS, SNI, chain and STARTTLS negotiation; simulate incomplete and negative responses, blocked targets, timeouts and cancellation. Avoid relying on live public internet outcomes for CI.

Test every endpoint's authentication and antiforgery, bounded request sizes/concurrency, private target rejection (including secondary addresses), and no-store responses. Use database-backed integration tests to establish that diagnostic runs and sending tests add no assets, jobs, results, certificates, changes or alert logs; retain monitoring regression coverage. Verify browser reset/refresh/navigation clears state and that neither submitted values nor results are written to application logs or browser storage.

Run the full existing test suite with an isolated PostgreSQL test instance, Docker production build and authenticated UI smoke checks in both languages before claiming completion. Document toolbox use, platform-dependent probe limits, deployment configuration and the SMTP retention boundary in README.

## Decisions delegated to implementation

Keep the existing SSR plus small JavaScript architecture; choose the detailed component/file split, DTOs and shared helper extraction without introducing an independent frontend. Select a maintained public-suffix parser/data package, precise bounded cipher candidate set and parsing helpers after verifying primary documentation and licenses. Record selected dependencies and platform limitations in the implementation plan. Cancellation, rate limiting and no-storage guarantees apply equally to every tool.
