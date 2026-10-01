# Persistence and recovery design QA

This separate skeptical pass reviews the [design](persistence-recovery-design.md) against code paths and failure counterexamples, before production changes.

| Challenge | Resolution |
| --- | --- |
| A primary receipt alone proves the entire outcome. | WitnessProtocol.ReconcileAccepted validates exact candidate bytes and independent settlement; the current Accepted/Retain/Start/Dismiss shortcuts omit it. Require that proof before definite replay, including after preparation pruning. |
| Reconcile from any read. | RuntimeAdmission.RunRead holds a witness shared fence. Appending settlement there can deadlock against the caller's own fence. Only mutation-bound observation reconciles; ordinary observations verify settled evidence without appending. Always acquire primary before witness. |
| Every business revision is a case command. | Void/reinstatement lifecycle events can advance the business revision while preserving all thirteen fields. Current reads must verify the latest accepted field snapshot and current witnessed lifecycle tip together, not demand a command receipt for every revision. Holds and privacy remain separate authority. |
| Limit witness proof to Execute retries. | A primary-only current projection or history receipt can also escape through Get/List/History/Observe. Verify the disclosed snapshot against its recorded accepted snapshot and read-only witness proof. Keep bounded pages and existing grant/fence ordering; do not append under a read fence. |
| Authorise after looking at receipt content. | Resolve case identity and recheck current actor/grant/disposition authority first. Check the candidate digest before snapshot parsing; wrong content cannot leak receipt bytes or corruption details. |
| Replaying through Transact is harmless. | That interface can execute when acceptance is absent and carries a Domain callback. Observation must have no execution capability. Remove it rather than add a third mutation abstraction; real persistence fixtures must then use the retained-attempt protocol. |
| All test changes are mechanical. | Former direct writes produced no preparation/attempt rows and used a fault hook at their sole settlement. Update setup and assertions to the actual protocol, target the accepted settlement specifically, and preserve independent count/byte/identity assertions. Do not silently weaken failed tests. |
| Dispose must throw whenever unlock fails. | The safety property is connector retirement, not changing known business evidence. Verify the connector is closed/retired and the known result survives. Do not swallow acquisition cancellation or allow an ambiguous session back into its pool. |
| The grant row makes all recovery reads stable. | PreparationPruning holds operation locks but no authority-tip row lock. Header and attempt evidence can therefore come from different READ COMMITTED snapshots. A repeatable read must preserve the whole response; a concurrency control must confirm the interleaving in PostgreSQL. |
| Later acceptance settles an earlier attempt. | Accepted history identifies the operation, not which prior unknown attempt committed it. The existing accepted shortcut calls settleExistingAttempt. Return an observed receipt separately from a new accepted execution and preserve older uncertainty/rejection. The public facade already has an observed-receipt outcome; no new wire variant is needed. |
| A settled attempt can simply execute again. | Its definite settlement is immutable. Refuse before creating a witness intent. A fresh attempt is allowed, while previous uncertainty and rejection remain historical evidence. Accepted replay and durable revocation still take precedence. |
| Clear old pending intents to make recovery easier. | Absence in a potentially restored primary does not establish non-commit. Retain explicit uncertainty and owner-evidence reconciliation; no automatic abort or new request identity. |
| Revocation can reuse acceptance's event ID. | BeginRevocation rejects any existing INTENT, including a crashed acceptance candidate. Separate the revocation authority event from the command operation and its prior acceptance intent. Bind both identities in primary evidence, journal lookup and purge coverage; keep original uncertainty, retention exclusions and restore quarantine. |
| Optional preparation means disposable during a hold. | Preparation also contains producer and historical attempt evidence. Hold protection must cover this irreversible deletion. A shared primary authority lock serialises pruning with new witnessed holds without blocking ordinary reads; recheck eligibility under the operation lock. Review dates do not release holds. |
| Tighten every schema while here. | Existing numeric columns do not use a rounding typmod, and the reviewed baseline/catalog constraints already enforce the model. Keep them and run their negative controls; change the fresh baseline only if concrete contrary evidence appears. |

Proceed with these repairs. First demonstrate the witnessed replay and cleanup failures with synthetic controls; qualify one production command writer through real PostgreSQL tests. Revisit this QA if implementation or test failures invalidate an assumption. Source review is not owner approval or complete verification.

Exploratory native MTP controls reproduced all three primary-only replay defects against isolated
PostgreSQL: Accepted, Retain and Start returned definite acceptance despite a committed intent with
no independent settlement. The three failures are retained in ignored diagnostic reports and do
not constitute complete suite verification. The separate revocation identity finding follows the
actual BeginRevocation INTENT guard and is added to the design before its implementation.

Implementation qualification: isolated PostgreSQL controls reproduce the primary-only accepted
shortcuts and orphan-intent/revocation collision. The repaired controls pass. Two negative controls
restore READ COMMITTED and throwing lease cleanup: both fail on the real pruning interleaving and
connector-termination checks, then the repaired source is restored byte-for-byte and rebuilt.
The current read proof also accounts for witnessed lifecycle revisions. The complete integration
rerun and all five separate PostgreSQL qualification suites pass; earlier failing/stale exploratory
runs are retained privately and are not complete verification.
