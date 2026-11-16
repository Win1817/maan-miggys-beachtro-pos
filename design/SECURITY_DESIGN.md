# Security, access and operational protection design

Version 1.0 · November 16, 2026 · POS-DEC-001. Design only. Surely Win Dilag is product owner, sole engineer, internal reviewer, tester and support owner. Cashier/Manager/Administrator below are **application permission templates**, not newly invented engineering personnel. Sources: [BRD](../Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md) §§6,8–11,15,18–20; FR-001–003,008,018,024,026–029; D-14/17 in [decision register](DECISION_REGISTER.md).

## Trust boundaries and assurance

Assets: accurate sale/correction/stock/cash records, account hashes, GCash references/verification, business registration, backup archives and Drive tokens. Ordinary POS users, local filesystem/Windows identity, external printer/spooler and Google OAuth/Drive are different boundaries. Offline login authorizes application access, not arbitrary OS file access.

Application RBAC and database guards protect against ordinary user misuse and accidental application writes. Windows administrator, malware under the same OS identity, physical disk access or stolen valid operator session can bypass some controls. No claim of tamper-proof local audit or unextractable desktop secrets. Use a dedicated standard Windows account, current OS/endpoint protection, screen lock, restricted physical access and device encryption where available. Installing/updating software may require Windows elevation but daily checkout should not.

## Authentication and sessions

Design selection: maintained `Microsoft.AspNetCore.Identity.PasswordHasher<LocalUser>` usable as a library in desktop services; no web server/full Identity EF schema needed. Configure current versioned PBKDF2-HMAC-SHA512 format with at least 210000 iterations; verify actual format/work factor in selected package and benchmark on target device. Increase cost if appropriate without blocking UI thread. Let maintained implementation create unique salts and fixed-time verification; no custom hash construction or reversible password encryption. Microsoft recommends PasswordHasher rather than low-level PBKDF2 for new password stores. [Microsoft guidance](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/consumer-apis/password-hashing?view=aspnetcore-10.0). OWASP documents modern password-hashing/work-factor guidance; revisit selected parameters before release. [OWASP password storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html).

Recommended owner-policy defaults: minimum 15 characters, allow spaces/Unicode, maximum 128 characters (never silently truncate), no arbitrary character-class rules or scheduled expiry. Validate maximum input before expensive hashing; do not log password lengths/values unnecessarily. Reject obvious common passwords using a local maintained list if available without network dependency; absence of online breach lookup cannot block offline login. Reset forces change at next login. Rehash after successful login if format/work factor is old. Hash verification runs off WPF dispatcher with bounded concurrent attempts.

Normalize username trim+invariant case folding under one documented algorithm and unique NormalizedUsername. Do not normalize/case-fold or trim passwords. Sign-in errors generic for nonexistent/inactive/wrong password; audit safe attempted username and outcome category. Proposed cooldown: after five consecutive failures lock that username for five minutes, persisted in DB; no permanent lock that requires internet to regain access. Rate-limit nonexistent-name attempts per process too. Successful login resets counters. Time rollback beyond tolerated clock skew keeps security warning and does not instantly bypass persisted cooldown.

Session is in-memory cryptographically random ID, actor ID, SecurityVersion and issued/last-activity monotonic times. Recheck active account and current security version on protected command. Role/account reset/deactivation increments version in same DB transaction; session then requires fresh login. No plaintext password cache or bearer token written to draft state. Multiple POS roles combine explicit granted capabilities; deny if capability absent. Changes to role grants invalidate affected sessions.

Recommended inactivity lock ten minutes, plus explicit Ctrl+L/Windows lock response. Do not abort an already-committing financial transaction on lock; resolve its persisted request on resume before new checkout. Lock clears sensitive modal data/password fields and hides report/receipt details; prepared request stays recoverable. On app restart require login, no remembered owner auto-login. Sensitive refund/void/tax/account/restore actions require same-session password reauthentication within five minutes and mandatory reason as applicable; no permanent privileged approval cache.

## Capability matrix (recommended default templates)

All internal administration is Surely's responsibility; this matrix supports future business users without creating staff requirements. `Y` grants, `—` denies. Owner permissions include all capabilities, with explicit authorization/audit still required. Administrator is technical permission set and receives no financial correction authority by default. Actual accounts/capabilities are confirmed before production (D-14).

| Capability | Cashier | Manager | Administrator | Owner | Sensitive control / FR |
|---|---|---|---|---|---|
| pos.sell | Y | Y | — | Y | Authenticated active user; FR-009–015 |
| orders.view.own | Y | Y | — | Y | Cashier's own transactions only; FR-017 |
| orders.view.all | — | Y | — | Y | All history/report personal data minimized; FR-017 |
| receipts.print | Y | Y | — | Y | Original receipt from authorized sale; FR-016 |
| receipts.reprint | — | Y | — | Y | Reason + REPRINT marking/audit; FR-016,024 |
| products.manage | — | Y | — | Y | Price before/after audit; FR-004–006 |
| inventory.adjust | — | Y | — | Y | Mandatory reason; FR-008 |
| inventory.negative.override | — | — | — | Y | Explicit extra action/reason, not ordinary stock check bypass; initial policy can disable completely |
| discounts.apply | — | Y | — | Y | Only configured eligible generic policy; approval snapshot; FR-011 |
| corrections.refund | — | Y | — | Y | Reauth/reason/tender evidence and limits; FR-018 |
| corrections.void | — | — | — | Y | Full linked reversal; FR-018 |
| reports.view.export | — | Y | — | Y | Scope-limited queries/local files, CSV protections; FR-021–023 |
| shifts.own | Y | Y | — | Y | Own open/close/count; capability has no effect if feature deferred; FR-019 |
| shifts.manage | — | Y | — | Y | Resolve abandoned shift with reason; FR-019 |
| cash.movements | — | Y | — | Y | Reason/open shift; FR-020 |
| audit.view | — | — | Y | Y | Safe audit only, no secret values; FR-024 |
| sync.view | Y | Y | Y | Y | Health summaries available; reference/payment payload details restricted |
| sync.retry | — | Y | Y | Y | Never changes remote identity/content; FR-027 |
| sync.configure | — | — | Y | Y | Reauth/reason, OAuth ownership; FR-029 |
| backup.create.verify | — | — | Y | Y | Data confidentiality and file ACL; FR-028 |
| backup.restore | — | — | Y | Y | Reauth, maintenance, explicit Surely disposition before real reopen; FR-028 |
| users.manage | — | — | Y | Y | Cannot grant capabilities outside own delegable set; Owner role assignment owner-only; FR-002–003 |
| roles.manage | — | — | — | Y | Prevent last-owner removal; security version invalidation |
| settings.operational | — | Y | Y | Y | Printer/UI only; no implicit tax/security permission |
| settings.financial | — | — | — | Y | Approved versioned profiles and evidence; FR-029 |
| production.activate | — | — | — | Y | Readiness evidence/external confirmation; cannot bypass Unknown tax profile |

Scope validation occurs in application service queries, not just navigation hiding. A cashier cannot fetch another user's order via direct ID. Background services run a bounded system context only for predefined jobs, not a universal UI login. System writes record actor System and source request; no synthetic owner attribution for automated activity.

Reauth actor and approval actor stored separately even when Surely is both. No second engineer or mandatory second person is assumed. If business later requires separation of duties, introduce that rule explicitly; do not falsify dual approval now.

## Bootstrap, reset and last-owner recovery

First run on confirmed new installation prompts Surely to create owner credentials, then permanently marks bootstrap complete before enabling business operations. No shipped/default credentials, embedded master password or automatic owner account when DB missing. Missing expected DB invokes RecoveryRequired.

Owner may reset another account after reauth, sets temporary random password presented once, forces change, invalidates old sessions and audits actor/target/reason without password. Administrator may reset only users within granted capability delegation and cannot take over Owner via reset. Prevent deactivating/removing the last active owner or removing own sole recovery capability without an alternate approved path.

Last-owner lost-password recovery: documented local maintenance procedure by Surely with Windows administrative control; back up/preserve DB first; create recovery audit record and offline one-time reset using future dedicated maintenance command. The command is not a hidden UI bypass or hard-coded key; it requires explicit maintenance invocation, filesystem access and proof of backup, and invalidates all sessions. Loss of both OS access and backups is not recoverable by an application magic password. Controlled recovery tool is a backlog item, not implemented here.

## Secret, file and cloud protection

Store token material only in a user-DPAPI protected file under `secrets`, ACL for designated Windows user and required system/admin access. Database keeps a file reference, not token bytes. OAuth authorization codes/state/verifiers/resumable session URLs are sensitive, excluded from logs and general backup manifests. Treat system-browser callback as untrusted input: validate state/issuer/redirect flow, bind local listener appropriately, close listener after use. Installed-app client ID is configuration, not a trusted hidden secret.

DPAPI CurrentUser binds to Windows identity, not POS username. Any process with same Windows credentials may be able to use DPAPI; therefore file/process hygiene matters. Reauthorize on replacement hardware. Backup includes business DB/snapshots/assets but excludes token secret; recovery does not require decrypting old token. TLS validation remains standard, never disabled for connectivity troubleshooting.

App data/private exports/backups/logs use restrictive inherited ACLs validated on startup/installer; no Everyone-write/share folder. User-initiated CSV/PDF outside private folder shows chosen destination and warns that normal file permissions apply, without leaking implementation details into cashier flow. Canonicalize all artifact paths, reject traversal/absolute external paths from DB/payload/manifest and use fixed allowed extensions. Do not execute scripts embedded in reports or archives.

Restore archives are untrusted input until validated: enforce member path containment, no links/reparse escapes, max member count/size, expected manifest/schema and checksums. Extract candidate under controlled temporary directory; no arbitrary overwrite from archive paths. Review disk headroom before extracting large backup. Use maintained archive/library formats rather than custom encryption.

Baseline initial data-at-rest decision: OS/device encryption where available + private local ACL + business Drive access, no public links or anonymous sharing; no custom application backup encryption. This does not make uploaded backups end-to-end encrypted. Surely must accept the exposure or choose a maintained portable encrypted archive/key-custody solution before production; inability to recover a key is a disaster-recovery failure. Record retention/deletion requirements after professional confirmation, not guessed legal durations.

## Audit, diagnostics and data minimization

Audit successful/failed sign-in, lock/reset/deactivation/role changes, prices/catalog activation, stock changes/overrides, prepared-request reconciliation, cash movements/shift close, sale/correction, reprint, financial/calendar/tax settings, Drive connection/account/root changes, backup/restore/migration and production activation. Include actor, safe target, UTC, request/correlation, reason/approver, safe before/after and result. Financial changes/audit commit together; login failures audit in independent security operation. Audit cannot depend on network success.

Structured diagnostics record operation/IDs/duration/exception category, not serialized input objects. Redact passwords, password hashes, OAuth tokens/codes, URLs with credential/session query params and sensitive payment notes. GCash reference may appear in authorized transaction/report output as required, but avoid full reference in general logs (last four characters only where useful). No storage of customer GCash credentials or unnecessary payer screenshots/identity. Data access/export is capability-controlled and audited; crash reports are reviewed/redacted before sharing externally.

Audit rows are append-only via guards and service policy; backups preserve records. Without an independently trusted log service, hostile local-admin tampering is not detectable in every case. Hashes/checksums detect accidental corruption but do not authenticate an attacker-controlled database/manifest. No enterprise log service introduced just to claim stronger assurance.

## Security acceptance and incidents

Automated negative tests invoke every protected service directly with absent/wrong capability and scoped IDs, not only UI buttons. Test reset/deactivation session revocation, role escalation, owner retention, password format/rehash/cooldown, lock during checkout and safe error redaction. Filesystem ACL/DPAPI replacement-user tests require actual Windows environment; fake-adapter tests do not establish them.

If token revoked/leaked: stop cloud claims, clear/revoke token through authorized procedure, preserve queue identities, review account sharing and reconnect. If account compromised: disable app user/invalidate sessions and review audit/corrections, do not edit sale history. If DB integrity/security uncertain: enter maintenance, preserve evidence and follow restore/reconciliation; no silent factory reset. Surely owns incident disposition and support; external tax/accounting/business advice is consulted only where needed.
