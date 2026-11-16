# Financial processing, checkout and reporting design

Version 1.0 · November 16, 2026 · POS-DEC-001 · Design only. Surely Win Dilag owns engineering and internal acceptance. [Decision register](DECISION_REGISTER.md) D-01–12 distinguishes selected engineering mechanisms from pending business/professional policy. Baseline: [BRD](../Maan_and_Miggys_Beachtro_POS_BRD_v1.2.md) §§7–8,11,13–14,17,20; FR-006–020,021–024,026,029.

## Money and policy contract

Persist rounded money as signed 64-bit integer minor units; calculate with checked C# decimal and explicit rounding only. For selected generic calculator, `RoundM(x)` rounds to an integer minor unit, MidpointRounding.AwayFromZero. Prices use currency exponent; rates use integer parts per million; quantities Q use 1000 per product unit. Terminal has one currency at a time; mixed-currency cart rejected. Historical snapshots carry currency/exponent independent of current settings. PHP/2 and Asia/Manila are proposed business defaults, not professionally confirmed settings.

No rate, tax status, statutory discount, invoice wording or electronic-invoice obligation is inferred. A calculation profile contains `Id`, immutable revision, effective UTC range, approval state Test/Approved, price mode, supported treatment codes, rate components, discount eligibility/order/allocation, rounding strategy/version and professional evidence reference where relevant. Unknown treatment is not equivalent to Exempt/ZeroRated. Unknown profile blocks production financial completion but permits explicit synthetic test profiles in a separate non-production installation. Production activation is a deliberate readiness action; test sale records never become production history.

The generic strategy below supports no-tax test cases and one simple ad-valorem component per line, inclusive or exclusive pricing, and one generic non-stacking order discount. Data can snapshot multiple components, but the generic strategy rejects compound/multiple statutory configurations until an explicit strategy and fixtures support them. This bounds design without silently claiming support for every future regulation. Statutory senior/PWD eligibility, required evidence and exact formulas remain external confirmation; isolate them behind the calculation strategy port.

## Deterministic generic algorithms

All algorithms are selected engineering behavior for synthetic fixtures and recommended business behavior; approved statutory profiles may specify different ordering. Persist strategy/version so historic documents never recalculate under a new algorithm.

### A. Extended price

Sort cart into persistent LineNo order. Each line has stable product/version, unit price `U` minor units, quantity `Q` and step. Compute `B = RoundM(U × Q / 1000)`. `B` is in the product's configured price basis: tax-inclusive customer price or tax-exclusive base. Do not round unit fractions early. Quantity must align with step, be >0 and within bounds. Historical unit price and exact Q both retained.

### B. One generic order discount

Select eligible lines explicitly from profile; exclude unsupported/mixed discount-basis combinations. Profile chooses FixedM or PercentPpm; `E = sum(B eligible)`. Fixed target `D` must satisfy 0<=D<=E; percentage `D=RoundM(E×rate/1000000)` with allowed rate 0..1000000 and cap E. No implicit stacking or automatic senior/PWD treatment.

Allocate D by largest remainder: provisional `a_i = floor(D×B_i/E)`; remainder numerator `r_i=(D×B_i) mod E`. Distribute `D-sum(a)` additional minor units to eligible lines sorted by r descending, then LineNo ascending. If E=0, only D=0 is valid. Set `DiscountBasisM_i=a_i` and discounted price-basis `P_i=B_i-a_i`. Noneligible lines get zero. This preserves exact order discount and deterministic tie-breaks. Use checked integer/decimal operations within declared bounds; never floating-point division.

For a mixed inclusive/exclusive catalog, do not silently allocate one fixed discount over unlike bases: generic profile requires consistent basis among discount-eligible lines, or separate approved allocation strategy. Ordinary no-discount mixed carts may still be calculated line by line. Repricing/eligibility requires new quote acknowledgment.

### C. Tax and financial components per line

For rate `r=RatePpm/1000000`:

| Treatment / mode | Selected calculation |
|---|---|
| Explicit no-tax synthetic/approved Exempt or ZeroRated | NetBefore=B, Net=P, Tax=0; retain distinct treatment code, never infer status from zero rate. |
| Taxable / Exclusive | NetBefore=B; Net=P; Tax=RoundM(Net×r); Payable=Net+Tax. |
| Taxable / Inclusive | NetBefore=RoundM(B/(1+r)); Net=RoundM(P/(1+r)); Tax=P-Net; Payable=P. |

`DiscountExTax=NetBefore-Net`; `GrossExTax=Net+DiscountExTax=NetBefore`; `Payable=Net+Tax`. Validate all components nonnegative. Inclusive tax is the residual so payable remains exact; do not independently round net and tax and then allow their sum to drift. `TaxableBaseM=Net` for the simple single-component profile; approved strategies can define other bases explicitly. No computation from displayed rounded rate strings.

Order totals are sums of rounded line components, never a second rounding of raw grand totals. Store PriceBasisSubtotal=sum(B), DiscountBasis=sum(a), Net=sum(Net), DiscountExTax=sum(DiscountExTax), Tax=sum(Tax), Total=sum(Payable). The generic inclusive discount basis includes tax reduction; it is not the same measure as tax-exclusive discount on reports. Receipts must label whichever measure they show; final legal labels remain D-02.

### D. Synthetic worked fixtures (not Philippine tax instructions)

Amounts below display two decimals; rate 12% is solely an arithmetic test input, not an approved business tax setting.

| Fixture | Inputs | Expected stored outputs |
|---|---|---|
| F-01 no-tax cash | 2×100.00 and 1×50.00, no discount; cash 300.00 | Net/Total=250.00, Tax=0, Tendered=300.00, Change=50.00; tracked units -2 and -1 only on commit. |
| F-02 half-minor rounding | 0.5 unit ×0.01, fractional step enabled | Raw 0.005 → basis/Net/Total=0.01. QuantityQ=500. |
| F-03 inclusive | B=112.00, rate12%, no discount | Net=100.00, Tax=12.00, Total=112.00. |
| F-04 inclusive discount | B=112.00, 10% discount | DiscountBasis=11.20, P=100.80, NetBefore=100.00, Net=90.00, DiscountExTax=10.00, Tax=10.80, Total=100.80. |
| F-05 exclusive discount | B=100.00, rate12%, 10% discount | DiscountBasis/DiscountExTax=10.00, Net=90.00, Tax=10.80, Total=100.80. |
| F-06 allocation tie | Three eligible 1.00 lines, fixed order discount 0.01, no tax | LineNo1 gets 0.01 discount, others 0; Total=2.99; allocation repeats identically after restart. |
| F-07 original inclusive residual | Three units ×35.00 inclusive, rate12%, fixed order discount5.00: B=105.00, P=100.00 | NetBefore=93.75, DiscountExTax=4.46, Net=89.29, Tax=10.71, Total=100.00; original QuantityQ=3000. Used for correction example below. |
| F-08 invalid | D>E, negative price/quantity, mixed currency, unsupported compound policy or nonaligned step | Rejected before payment completion, no financial/stock/export side effects. |

## Immutable transaction snapshot

At quote, capture referenced versions and expected values; at commit revalidate active status/stock/permissions/effective policy. If master price/config changed, return StaleQuote with newly calculated totals and require operator acknowledgment and new prepared request. No silent post-payment repricing. If an external GCash transfer already occurred, hold the request for reconciliation rather than changing the charged amount automatically.

Complete snapshot schema `sale.v1` contains:

- Identity: SaleId, RequestId, TransactionNumber, installation ID, completion UTC, business date/timezone/cutoff, cashier ID and display name, optional shift, order type and optional legal invoice identity.
- Business/receipt: registered identity/contact/tax identifiers as actually approved, logo content reference/hash, template version; no live lookup that changes historical receipt identity.
- Each line: ProductId, SKU/name, original unit price, QuantityQ/step, TrackStock flag, currency/exponent, configured price mode, extended basis, discount allocation/authority and rule inputs; NetBefore, DiscountExTax, Net, each tax component key/code/rate/base/amount, payable.
- Policy: exact calculation/calendar/profile revisions, immutable canonical config or its preserved content/hash, generic/approved statutory strategy version and safe approval/evidence references.
- Payment: method, applied amount, tender/change where cash, manual-verification state/actor/time, optional reference/note, recording actor/time. Never payment passwords, tokens or unnecessary customer identity.
- Totals: all sums, schema version and deterministic snapshot checksum. Store the complete canonical snapshot plus searchable relational fields; assert both agree in tests.

Relational columns and JSON are two representations of one immutable write, not independent editable ledgers. Canonical serialization uses stable property order, invariant strings, integer amounts, UTF-8 without BOM and UTC milliseconds; preserve schema-version serializer for regeneration. Receipt/export generation reads these facts, never current catalog/tax settings. For binary PDFs/logo/fonts preserve template/assets by content hash or store immutable rendered bytes to guarantee regeneration. Backup manifest includes required assets.

## Checkout protocol and crash recovery

1. Quote server-side in local application service. Require selected Dine-in/Takeout, valid active products/step, supported policy, authorized discount and available stock. No stock reservation yet.
2. `PrepareCheckout` saves stable RequestId, actor, frozen normalized quote/payment-intent fingerprint and preparation UTC in its own small transaction before cashier acknowledges external payment. Persist this request before presenting final payment instructions. No completed financial sale or stock deduction occurs yet.
3. Cash: require tender>=Total and manually acknowledge cash received; compute change. GCash: require explicit receipt verification by authorized actor, time and optional reference. Reference absence is allowed by baseline; repeated nonempty reference is a warning for reconciliation, not proof of settlement or unconditional uniqueness constraint.
4. `CompleteCheckout` acquires write gate. If request Completed return original result; if same key/different business payload reject. Revalidate session/capability, quote versions, effective policy, open required shift, stock and external verification. Reserve a number in flushed journal as below; then begin serialized local transaction and recheck mutable stock/policy/request state before writes.
5. Insert Order/items/tax/payment, stock balances/movements, financial audit, immutable artifact intent/queue and original receipt job together; mark request Completed; commit. All critical data fail together. No HTTP, printer or PDF work inside commit. A reserved number can become an audited gap if transaction fails; it must not be reassigned.
6. On success show persisted sale. Printer/upload workers run independently. If response lost, reopen a fresh connection and resolve RequestId. Completed returns original; Prepared confirms no committed sale but external cash/GCash may exist. If DB cannot determine outcome, show OutcomeUnknown/RecoveryRequired and prevent a new attempt for this cart until Surely reconciles.

Prepared requests can have transient verification data entered before commit; record safe manual evidence in request state before initiating the financial write. Crash between real-world money arrival and any local evidence is inherently possible. Recovery screen lists unresolved Prepared requests and quoted amounts, asks operator to check local history and real payment receipt, then either finalize the same frozen request if still valid or record an authorized reconciliatory outcome. No automatic second charge or invented gateway acknowledgment. Abandon requires reason when payment may have been received; do not silently expire such requests.

Cash/GCash payment means exactly one applied amount equal to Total. Cash Tendered and Change are distinct; cash ledger adds Total, not tendered. Positive-total-only is the recommended initial business rule. Zero-total/split tender/credit require an explicit policy change before production behavior; data conventions do not silently authorize them.

## Number allocation and restore safety

Internal stable identity is UUID, independent of displayed `MB-YYYYMMDD-000001`. Date uses immutable business date from approved calendar; sequence 1..999999 per prefix/date. No backdating. If sequence exhausted, stop issuing that series; never widen/change format silently. Internal sale number is not assumed to satisfy legal invoice-series obligations.

Under exclusive writer gate, compute next=max(DB series high-water, verified reservation-journal high-water)+1. Append journal record `{installation,series,sequence,requestId,reservedUtc,previousRecordHash}` with checksum, flush to durable file before attempting commit. Each journal append record has length/checksum so torn trailing writes are detectable; if durability cannot be verified, do not start commit. On startup reconcile DB/journal maxima before selling; DB may lag for failed transactions. Once a reservation is durably written it remains consumed even if local transaction rolls back. Do not reserve a number per UI preview.

The external journal closes the ordinary old-DB-restore gap on the same healthy disk. It is **not** a guarantee against losing the whole disk, malicious edits or restoring journal and DB together to stale copies. Backup includes its checkpoint/last hash as evidence; independent latest copies/cloud artifacts/printed records may establish later consumption. After restore, take maximum trustworthy consumed/reserved sequence; if there is no provable upper bound, block selling on the uncertain series. Surely must establish a safe boundary or explicitly approve a professionally valid fresh series/change. A new UUID or recovery-generation ID alone does not prevent reuse of printed numbers. Do not claim 'max restored orders +1' solves it.

Persist recovery action, skipped reservations and series disposition with reason/evidence. Sync failure never blocks ordinary allocation because journal is local; inability to safely write local financial/journal data does block checkout. Legal requirements about sequence gaps/series are professional-confirmation items (D-01/02/12), not established tax assumptions.

## Corrections and void accounting

Cancellation before completion removes/abandons a draft/prepared request with an audit reason when appropriate; it has no sale reversal or stock return. A completed sale is immutable. Proposed Void = full remaining sale reversal only if no prior partial correction; otherwise use full remaining Refund. No deletions, negative original lines or reset-to-Draft.

Each correction has stable request/correction UUID, visible `RF-<UUID>` audit reference (not a legal credit-document series), original order/line links, reason, actor, approving actor/time, event UTC/business date, quantity and positive reversal magnitudes. Derive displayed status from cumulative committed corrections. Self-approval by Surely is proposed and explicit; a different fictional supervisor is not required. Ordinary cashiers cannot self-approve without capability. A post-sale void requires real payment reversal or documented approved legal procedure; do not label money still held as refunded.

For each original line total component `C` and original QuantityQ `Q`, define cumulative allocated reversal at returned quantity `q` as `A_C(q)=floor(C×q/Q)` for 0<=q<Q, and `A_C(Q)=C`. For new correction from q0 to q1, reverse `A_C(q1)-A_C(q0)`. Apply independently to original NetM, DiscountExTaxM, each tax component and DiscountBasisM. Correction payable=Net reversal+sum tax reversals; correction gross ex-tax=Net reversal+DiscountExTax reversal. Do not recompute rates or discounts under today's profile. This cumulative rule is monotone, caps each component, and returns every original minor unit on full return. It intentionally gives final residual pennies to the last quantity; professional statutory rounding can override via a versioned approved correction strategy.

F-07 sequential unit returns: first Net=29.76, Tax=3.57, Total=33.33; second same; final Net=29.77, Tax=3.57, Total=33.34. Sum=89.29+10.71=100.00. A repeated correction RequestId returns original result; a new correction reads all already-returned quantities in the transaction. Reject cumulative quantity/value beyond original; full void after partial refund is rejected under proposed policy to avoid ambiguity. Zero-valued fractional return is possible from rounded allocations: quantity is still logged, no external refund claimed, final residual rule remains exact.

Stock disposition per returned line: Return (add stated returned Q, bounded by correction Q), Discard (no stock), NotTracked (no stock; source was untracked). Never restore all refunded quantity by default, and never make today's TrackStock setting rewrite original sale deduction. If originally tracked product is now untracked, require explicit inventory reconciliation rather than silently adding hidden stock. Adjustment/return movement and audit commit with correction.

Refund tender defaults to original tender. Cash requires actual paid-out acknowledgment. GCash requires manually verified external refund, actor/time/reference if available; no integrated transfer. Prepare correction request before external refund and recover unknown outcomes like checkout. Only after required evidence is recorded can financial correction commit. If transfer already happened but DB cannot finalize, retain Prepared request for reconciliation; repeated command does not send money. Statutory credit note or invoice-cancellation form remains D-01/02 confirmation.

## Reporting dictionary and shifts

Reports use committed original sale facts and committed correction events, each once, on its own immutable event business date. Query events with `[startDate,endExclusiveDate)`; account for later calendar-policy changes using snapshotted dates, not current timezone reinterpretation. Date/time filters over instants use UTC [start,end). Original sale remains in original period; a next-day refund subtracts on next day. Optional restated-period analysis would be a separate labeled report, not implicit correction of history.

| Metric | Exact definition |
|---|---|
| Original gross ex-tax sales | Sum(original Net+DiscountExTax) in selected event period. |
| Original discounts ex-tax | Sum(original DiscountExTax); do not mix with inclusive DiscountBasis. |
| Original net sales ex-tax | Sum(original Net). |
| Refund/void net ex-tax | Sum(committed correction Net); display as explicit positive reversal measure. |
| Net revenue ex-tax | Original Net minus correction Net for period. |
| Net tax | Original tax minus correction tax for period. |
| Net collected/settled amount | Original Total minus correction Total, partitioned by actual recorded tender. It is recorded verification, not a bank settlement audit. |
| Net discounts ex-tax | Original DiscountExTax minus correction DiscountExTax; companion adjusted gross = original gross minus correction gross, so adjusted gross - net discounts = net revenue. |
| Transaction count / average sale | Count original completed sale orders; average original Total/count (zero if none). Report correction count/value separately. Do not divide net revenue by refund events or remove fully refunded originals from count. |
| Product quantity/value | Original QuantityQ/Net/Tax/Total minus correction components; show gross sold and returned separately. |
| Cash expected in shift | Opening + cash sale Applied + cash-in - cash-out - cash refund Amount; excludes GCash and excludes tender/change double counting. Variance=counted-expected. |

Daily dashboard labels Net revenue ex-tax and Net collected separately, plus original sale count, refunds, cash/GCash and sync/stock health. All seven BRD §17 reports have CSV/PDF outputs with identical totals and watermark: Daily Sales, Payment Summary, Product Sales, Inventory Status, Cashier Shift (if retained), Monthly Sales, Transaction Detail. Inventory Status is an as-of-query balance snapshot, not a reconstructed past balance unless a future explicit historical report is added.

CloseBusinessDay records last included ledger event/inclusion hash and report version; it does not mutate/lock historical orders or prevent later dated corrections. Subsequent corrections produce their own period's data; regenerated report for an affected range gets a new revision. Store canonical report-result snapshot for byte/content regeneration. Monthly report catch-up generates missing completed-month artifacts when app next runs, not a promise that a powered-off machine executes schedules. Summary filename includes revision/content identity.

Shift closure attaches immutable snapshot of equation inputs, counted cash, variance, closer and reason. Open shift across midnight retains association but business-date reports still use sale event dates. A refund from an older shift paid during today's shift affects today's cash, not the closed shift's counted cash. Required-shift policy off by default pending business procedure; do not force a shift dependency into core checkout silently.
