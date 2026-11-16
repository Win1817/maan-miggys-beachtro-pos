# Technical feasibility assessment

Date: November 16, 2026. Baseline: [BRD v1.2](Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md), sections 9–21, 23–27 and Appendices A–B. Status: documentary assessment; no builds, benchmarks, installs, printer tests or account integrations performed. Proposed technologies remain subject to design review.

## Verdict and evidence limits

The single-terminal native Windows POS is feasible using the baseline stack. No fundamental conflict exists between local checkout, finished-product stock and asynchronous Drive copies. The main risks are incomplete policy and failure handling rather than technology capability. Feasibility is conditional on financial/tax decisions, exact data mappings, real hardware and recovery validation. Effort or delivery dates cannot be responsibly fixed without those inputs (D-01–20).

| Component | Assessment | Constraint / future evidence | BRD reference |
|---|---|---|---|
| .NET 10/WPF/MVVM | Suitable for the native Windows constraint. Retain proposed .NET 10 LTS; no upgrade solely because a newer major exists. | Pin compatible SDK/runtime/dependencies and supported Windows build. Microsoft's current policy lists .NET 10 LTS through November 14, 2028; patch servicing and upgrade ownership needed. [Microsoft policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). | Section 10,21; FR-025 |
| EF Core/SQLite | Suitable for one local terminal and transactional stock/sales. | Validate selected provider's exact money/date mappings, aggregate translations, migrations and busy handling. SQLite has no native decimal type; provider behavior is version-sensitive. Never convert financial values to double as a workaround. [EF SQLite limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations). | Sections 8–11; FR-007,011–012 |
| Local concurrency | UI plus background workers can operate with short transactions and controlled retry. | SQLite WAL requires same-host access; it is unsuitable for the proposed future network-sharing shortcut. Test locks, power loss and backup load. [SQLite WAL](https://www.sqlite.org/wal.html). | Section 10.2; FR-025,030 |
| BackgroundService | Suitable for asynchronous work during application lifetime. | Must wire start/stop/error handling into desktop host; app closed means in-process worker stopped. Choose catch-up versus external scheduling explicitly. | Sections 10,15,19; FR-026–028 |
| Drive API v3 | Suitable for off-site artifact uploads; not a transactional shared ledger. | Stable artifact identity, ambiguous-success recovery, resumable large uploads, quota and revoked-token behavior need integration tests. [Uploads](https://developers.google.com/workspace/drive/api/guides/manage-uploads), [retry identity](https://developers.google.com/workspace/drive/api/guides/create-file). | Section 15; FR-026–027 |
| Desktop OAuth | Feasible with business-controlled client/project and installed-app browser authorization. | Use appropriate desktop flow with PKCE and approved redirect; minimum scope must match folder creation/selection. App distribution/consent configuration and reconnect ownership pending. [Desktop OAuth](https://developers.google.com/identity/protocols/oauth2/native-app), [Drive scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth). | Sections 15,18; FR-029 |
| Windows DPAPI | Appropriate proposed token protection on Windows. | CurrentUser is tied to Windows user context; LocalMachine has different access scope. Do not assume portable ciphertext or separation between POS users sharing one OS account. [Microsoft DPAPI scope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope). | Sections 15,18–19 |
| Safe backup | Feasible via a supported consistent API rather than uncontrolled live-file copying. | Include manifest, validate/checksum and replacement-device restore. Online Backup API can copy incrementally while briefly locking reads. [SQLite backup](https://www.sqlite.org/backup.html). | Section 19; FR-028 |
| Receipt printing | Feasible through Windows drivers, with model-dependent layout. | Obtain exact printer and physically test 58/80mm as applicable, long names, peso, margins, offline/paper-out and reprints. 80mm is provisional. | Sections 16,21,24; FR-016 |
| CSV/PDF | Feasible locally without internet. | Select licensed, maintainable PDF/print library after layout needs known; embed/support fonts, consistent calculations and CSV formula safeguards. No package selected or installed here. | Sections 13,17; FR-023 |
| Password/RBAC/audit | Standard local design, feasible offline. | Choose maintained salted password-hash implementation/parameters and reset/session procedures. Windows admins can bypass ordinary app file controls; document assurance limits. | Sections 6,11,18; FR-001–003,024 |
| MSIX/Windows Installer | Both plausible baseline options. | Confirm printer access, data-directory ACLs, bundled runtime, signing, update and migration recovery before selection. | Sections 10,21,28 |
| Future API/PostgreSQL | Credible expansion path, not part of initial deployment. | Reuse domain/application boundaries; budget separately for central concurrency, cutover, authorization and offline conflict design. | Sections 10.2,27; FR-030 |

## Financial and operational feasibility

Exact persisted minor-unit amounts with C# decimal calculations are recommended for rounded money, subject to currency/scale approval. Fractional product quantities or tax rates need their own exact representation. Stored originals and correction records must reconcile line, order, payment and reports. A valid aggregate requires approved definitions, not merely a database SUM (G-02–06).

Offline operation means no dependency on Drive, remote login or payment gateway to access initialized local POS functionality. It cannot guarantee local operation with disk failure/full storage, nor prove a GCash transfer without the business's verification evidence. Approve an unavailable-verification procedure while retaining manual verification (FR-015,025; D-07).

Daily backup and seven generations are a proposed initial target, not an achieved recovery objective. Same-device copies protect against some logical failures but not disk theft/loss. Cloud copies may lag indefinitely during outages. RPO/RTO requires the owner to choose acceptable exposure and a tested restore path (sections 19,25; G-11–12).

## Philippine tax and regulatory assessment

The BRD appropriately leaves tax treatment and compliance unconfirmed (sections 4,16,20,29). No tax rate, exemption, certification or production legality is inferred here. Owner/accountant must assess the actual business classification, registration, statutory discounts, invoice fields/series, records retention and POS obligations, including current issuances at go-live.

BIR's RMC 98-2026 digest addresses electronic invoicing for specified taxpayer groups and distinguishes electronic-invoice issuance from electronic sales reporting. Its applicability to Beachtro cannot be determined from the BRD. This warrants an explicit review; uploading CSV/PDF files to Drive does not establish compliance. [Official BIR digest](https://bir-cdn.bir.gov.ph/BIR/pdf/RMC%20No.%2098-2026%20Digest.pdf).

BIR's October 22, 2026 release also discusses separate creditable withholding tax presentation where applicable. This review does not assume Beachtro has such transactions or can use transition provisions for existing registered systems. Accountant review must determine relevance to a newly deployed POS. [Official BIR release](https://bir-cdn.bir.gov.ph/BIR/pdf/PR122OCT2226.pdf).

The external references are primary-source engineering context reviewed on November 16, 2026; this is not an exhaustive legal audit. Any required capability outside current scope needs a documented impact assessment and product-owner approval, rather than silent implementation or dismissal as out of scope (G-01; D-01–02).

## Proposed validation after authorization

1. Verify pinned EF/SQLite versions with exact amount/quantity round trips, tax/discount/refund fixtures, UTC ranges and aggregate results; inspect schema constraints and migrations (T-04, T-06, T-09).
2. Inject interruption before/during/after checkout commit; repeat request; verify one sale, number, payment, stock effect and sync intent (T-07, T-13).
3. Simulate offline backlog, stalled workers and remote-success/local-ack loss; retry same identity and verify one intended artifact (T-14–15).
4. Test actual printer and representative catalog/history on approved Windows device, including backup/report activity (T-08, T-18).
5. Restore to a replacement environment, verify last sale, avoid number reuse, recover payloads, reconcile queue and reauthorize cloud access (T-16).

These are future engineering experiments, not work performed. Legal review, owner workflow decisions and hardware access are critical-path dependencies; no runtime or integration capability has been established by this documentary review alone.
