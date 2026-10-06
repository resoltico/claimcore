# Contract tests

This generated map names the tests in registered required suites that carry each contract token.
Read the contract heading in its owner document and the test assertions together: a token is a
navigation aid, not semantic proof. [Development](development.md) owns execution requirements.

<!-- generated:begin contract-tests -->
| Contract | Required suite | Named test |
| --- | --- | --- |
| CC-APP-001 | acceptance | published authenticated CLI v4 acceptance.[CC-APP-001] rejected published CLI-v4 command preserves current case and accepted history |
| CC-APP-001 | integration | ClaimCore PostgreSQL integration.rejected transactions.[CC-APP-001] rejected command changes neither case nor history |
| CC-APP-002 | integration | ClaimCore PostgreSQL integration.PostgreSQL accepted receipt identity.[CC-APP-002] PostgreSQL accepted replay survives owner pruning of preparation |
| CC-APP-002 | integration | ClaimCore PostgreSQL integration.PostgreSQL accepted receipt identity.[CC-APP-002] PostgreSQL concurrent pruned replay and conflicts keep one accepted revision |
| CC-APP-002 | integration | ClaimCore PostgreSQL integration.PostgreSQL accepted receipt identity.[CC-APP-002] PostgreSQL rejects wrong digest before parsing accepted snapshot |
| CC-APP-002 | integration | ClaimCore PostgreSQL integration.PostgreSQL transactions.persistence and replay.[CC-APP-002] exact replay returns original receipt, not current state |
| CC-APP-002 | integration | ClaimCore PostgreSQL integration.PostgreSQL transactions.persistence and replay.[CC-APP-002] same ID with different content fails |
| CC-APP-002 | integration | ClaimCore PostgreSQL integration.[CC-APP-002] PostgreSQL accepted history without preparation remains replayable |
| CC-APP-002 | unit | ClaimCore deterministic suite.[CC-APP-002] prepared canonical identity cannot be mutated through an accessor |
| CC-APP-002 | unit | ClaimCore deterministic suite.accepted receipt first.[CC-APP-002] accepted identity conflict refuses without recovery disclosure |
| CC-APP-002 | unit | ClaimCore deterministic suite.accepted receipt first.[CC-APP-002] accepted receipt wins after original commit confirmation is lost |
| CC-APP-002 | unit | ClaimCore deterministic suite.accepted receipt first.[CC-APP-002] accepted replay needs no retained preparation or recovery read |
| CC-APP-002 | unit | ClaimCore deterministic suite.accepted receipt first.[CC-APP-002] accepted-history read failure refuses before fresh preparation |
| CC-APP-002 | unit | ClaimCore deterministic suite.accepted receipt first.[CC-APP-002] cancellation after accepted observation preserves definite receipt |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.architecture manifest.[CC-ARCH-001] compiled internals grants match the manifest exactly |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.architecture manifest.[CC-ARCH-001] declared project edges match the manifest exactly |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.architecture manifest.[CC-ARCH-001] every repository project is classified |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.architecture manifest.[CC-ARCH-001] internals grants follow declared project edges |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.architecture manifest.[CC-ARCH-001] internals grants name classified components |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.architecture manifest.[CC-ARCH-001] product components depend only on product components |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.architecture manifest.[CC-ARCH-001] restore publication trust is invisible to ordinary consumers |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.architecture manifest.[CC-ARCH-001] stale permissions and empty grant sets are detected |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.architecture manifest.[CC-ARCH-001] the reviewed component graph is acyclic |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.component effect ownership.[CC-ARCH-001] Web bindings cannot decode ClaimCore JSON bytes |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.component effect ownership.[CC-ARCH-001] primary storage uses database time without ambient host clock reads |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.product graph evidence.[CC-ARCH-001] compiled model covers every type and emits a bounded graph |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.product graph evidence.[CC-ARCH-001] evaluated Debug and Release dependencies obey component policy |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.product graph evidence.[CC-ARCH-001] every permitted product edge is actually used |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.published component surface.[CC-ARCH-001] application storage ports stay private |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.published component surface.[CC-ARCH-001] lifecycle transitions remain internal to their decision owner |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.published component surface.[CC-ARCH-001] storage exports only schema-owner administration |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.published component surface.[CC-ARCH-001] the CLI protocol exposes only remote service seams |
| CC-ARCH-001 | architecture | ClaimCore architecture suite.published component surface.[CC-ARCH-001] the composition root exports one entry point |
| CC-ARCH-001 | fuzz | ClaimCore fuzz qualification.boundary decoding totality.[CC-ARCH-001] contract-owned HTTP codecs refuse hostile bytes without throwing |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.[CC-AUDIT-001] authority lease cleanup preserves an observed result and retires its connector |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.adopted external copy transitions.[CC-AUDIT-001] adopted external signed origin and transition replay |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.adopted product copy transitions.[CC-AUDIT-001] product export signed post-adoption transition replays through projection |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.authority operation fence.[CC-AUDIT-001] cancelled queued exclusive acquisition retires its connector and releases authority |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.authority operation fence.[CC-AUDIT-001] cancelling an audit after its fence releases all authority locks |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.authority operation fence.[CC-AUDIT-001] cancelling an audit queued behind authority does not strand its session lease |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.authority operation fence.[CC-AUDIT-001] full audit drains a queued transaction with both ordinary pool slots occupied |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.authority operation fence.[CC-AUDIT-001] full audit revalidates a same-name weaker check after runtime resources open |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.authority operation fence.[CC-AUDIT-001] full audit waits for actor authority settlement after primary commit |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.authority operation fence.[CC-AUDIT-001] full audit waits for owner provisioning settlement after primary commit |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.authority operation fence.[CC-AUDIT-001] two-slot runtime opens and settles case work |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.authority operation fence.[CC-AUDIT-001] witness advancement before read fencing is included in the audit cutoff |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.case erasure old artifact.[CC-AUDIT-001] postprune export length remains bound to witnessed evidence |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.case erasure purged-state audit.[CC-AUDIT-001] altered keyed denial root quarantines purged audit |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.case erasure purged-state audit.[CC-AUDIT-001] missing middle CASE intent quarantines purged audit |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.case erasure purged-state audit.[CC-AUDIT-001] missing retained steward proof quarantines purged audit |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.case erasure purged-state audit.[CC-AUDIT-001] missing suffix CASE purge quarantines audit |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.case erasure purged-state audit.[CC-AUDIT-001] restored claimant row beside purge proof quarantines audit |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.case erasure purged-state audit.[CC-AUDIT-001] restored primary missing purge tombstone quarantines audit |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.case lifecycle full audit.[CC-AUDIT-001] full audit merges disposition revisions and detects a tampered lifecycle tip |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.external copy publication.[CC-AUDIT-001] external publication verifies actual bytes and detects signature tampering |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.full primary data audit.[CC-AUDIT-001] audit rejects a false accepted business date |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.full primary data audit.[CC-AUDIT-001] audit rejects a stale current projection |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.full primary data audit.[CC-AUDIT-001] audit rejects noncanonical historical snapshot |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.full primary data audit.[CC-AUDIT-001] audit replays every retained accepted case |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.full primary data audit.[CC-AUDIT-001] independent witness detects a whole omitted case |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.owner backup capture fence.[CC-AUDIT-001] complete audit drains primary mutations and blocks new witness tickets |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.owner full data audit command.[CC-AUDIT-001] owner verify-data reports full counts and quarantines a changed case |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.postprune external publication audit.[CC-AUDIT-001] changed postprune publication marker breaks signed target digest |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.postprune external publication audit.[CC-AUDIT-001] pruned publication intent still requires immutable primary receipt |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.runtime I/O and diagnostics.[CC-AUDIT-001] failed audit emits one fixed quarantine signal |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.runtime full-audit cadence.[CC-AUDIT-001] overdue complete audit closes admission persistently |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.runtime full-audit cadence.[CC-AUDIT-001] runtime disposal cancels active scheduled audit |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.runtime full-audit cadence.[CC-AUDIT-001] scheduled audit quarantines post-opening row tamper |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.runtime full-audit cadence.[CC-AUDIT-001] scheduled complete audits repeat and remain healthy |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.runtime full-audit cadence.[CC-AUDIT-001] scheduled full-audit failure closes actor authority lanes |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.runtime scheduling and shutdown.[CC-AUDIT-001] long complete audits leave a completion-based interval |
| CC-AUDIT-001 | integration | ClaimCore PostgreSQL integration.witnessed capacity and competing work.[CC-AUDIT-001] paged witnessed volume export and actor work survive audit contention with small pools |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.[CC-AUTH-001] case-list cursor binds principal grant query and visible page |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.[CC-AUTH-001] voided and erasure-fenced identities share denial timing class |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor and grant storage.[CC-AUTH-001] exact authority replay detects a forged live actor projection |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor and grant storage.[CC-AUTH-001] exact initial-owner readback preserves receipt without restoring authority |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor and grant storage.[CC-AUTH-001] explicit grant revocation advances authority and invalidates stale snapshot |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor and grant storage.[CC-AUTH-001] initial owner is private and unknown login remains ungranted |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor and grant storage.[CC-AUTH-001] real-data roster needs a second distinct human owner or steward |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor and grant storage.[CC-AUTH-001] service principals cannot acquire human owner or steward grants |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor and grant storage.[CC-AUTH-001] stale primary cannot repeat initial owner against surviving witness |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor management.[CC-AUTH-001] management denies unknown actor and service stewardship |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor management.[CC-AUTH-001] orphan grant intent remains unknown on exact retry |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor management.[CC-AUTH-001] owner management exact retry keeps event and target identity |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound core.[CC-AUTH-001] actor-bound execute, read and exact replay enforce current grant |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound core.[CC-AUTH-001] case list filters grants before storage page window |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound core.[CC-AUTH-001] inaccessible and absent resources share one refusal |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound core.[CC-AUTH-001] revoking case edit after prepare fences submit |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound core.[CC-AUTH-001] unknown actor and ungranted owner cannot reach case work |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound recovery list.[CC-AUTH-001] recovery cursors bind principal, grant and expiry |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound recovery.[CC-AUTH-001] nonactive case state closes claimant reads and exact replay |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound recovery.[CC-AUTH-001] operation-bound recovery resolve rechecks grant before acceptance |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound recovery.[CC-AUTH-001] recovery inspection requires a current operation grant before payload read |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound replay.[CC-AUTH-001] a second editor cannot read another actor's retained draft |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor-bound replay.[CC-AUTH-001] current read grant permits receipt replay without duplicate effect |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor/grant resource lookup.[CC-AUTH-001] authority lock excludes a concurrent grant change |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.actor/grant resource lookup.[CC-AUTH-001] case and accepted-operation lookups do not reveal inaccessible identities |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.case-list capacity.[CC-AUTH-001] installation access keeps bounded ordered work at volume |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.case-list capacity.[CC-AUTH-001] sparse actor access selects indexed granted cases at volume |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.case-list cursor protection.[CC-AUTH-001] cursor tamper and runtime restart refuse without plaintext |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.case-list cursor protection.[CC-AUTH-001] encrypted cursor binds actor grant query and expiry |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.mutation result disclosure.[CC-AUTH-001] accepted command result is withheld after editor revocation |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.mutation result disclosure.[CC-AUTH-001] accepted command result is withheld after erasure fence |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.mutation result disclosure.[CC-AUTH-001] accepted recovery resolution is withheld after operator revocation |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.mutation result disclosure.[CC-AUTH-001] exact import result is withheld after importer revocation |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.mutation result disclosure.[CC-AUTH-001] retained preparation is withheld after editor revocation |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.mutation result disclosure.[CC-AUTH-001] revoked preparation details are withheld after operator revocation |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.mutation result disclosure.[CC-AUTH-001] witnessed export is withheld after exporter revocation |
| CC-AUTH-001 | integration | ClaimCore PostgreSQL integration.signed recovery artifact import preview.[CC-AUTH-001] signed import preview requires current actor authority |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.[CC-AUTH-001] CLI discovery compares the complete issuer identity |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.[CC-AUTH-001] every Domain command maps to edit capability |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.actor and grant authorization.[CC-AUTH-001] absent and inaccessible cases disclose identically |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.actor and grant authorization.[CC-AUTH-001] case grants stay scoped |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.actor and grant authorization.[CC-AUTH-001] every semantic endpoint has one scope and capability |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.actor and grant authorization.[CC-AUTH-001] grants default-deny and owner does not read claims |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.actor and grant authorization.[CC-AUTH-001] mutation authority rejects stale grants and disabled actors |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.actor and grant authorization.[CC-AUTH-001] operation access and export are separately granted |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.actor and grant authorization.[CC-AUTH-001] principal identities are typed and exact |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.actor and grant authorization.[CC-AUTH-001] real-data activation review and approval require a human installation owner |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.case-list cursor contract.[CC-AUTH-001] invalid list cursor has one typed diagnostic |
| CC-AUTH-001 | unit | ClaimCore deterministic suite.case-list cursor contract.[CC-AUTH-001] list cursor binds caller grant query and expiry |
| CC-BACKUP-001 | backup-qualification | ClaimCore managed backup qualification.[CC-BACKUP-001] deploy-time custody gate refuses same-host and self-certified evidence |
| CC-BACKUP-001 | backup-qualification | ClaimCore managed backup qualification.[CC-BACKUP-001] encrypted dual-cluster backup is verified by isolated restores and rejects altered evidence |
| CC-BACKUP-001 | backup-qualification | ClaimCore managed backup qualification.operator evidence recovery.[CC-BACKUP-001] capture interruptions and checkpoint replay preserve exact evidence |
| CC-BACKUP-001 | backup-qualification | ClaimCore managed backup qualification.operator evidence recovery.[CC-BACKUP-001] independent observer aggregate rejects key reuse and forged evidence |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.Restore report owner roster.[CC-BACKUP-001] owner W1 commands refuse absent publication before authority access |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.Restore report owner roster.[CC-BACKUP-001] owner fenced-tail and activation grammar binds seven private files |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.Restore report owner roster.[CC-BACKUP-001] restore report owner roster binds current witnessed human grants |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.Restore report owner roster.[CC-BACKUP-001] restore report refuses unregistered owner key claims |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.Restore report owner roster.[CC-BACKUP-001] source checkout cannot self-authorize a restore report |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.Restore report pair binding.[CC-BACKUP-001] stale or divergent restore pair facts refuse recheck |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.[CC-BACKUP-001] intermediate restore reports cannot claim deployment readiness |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.advanced physical restored pair.[CC-BACKUP-001] original BASE pair replays later synthetic WAL before audit |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.authority operation fence.[CC-BACKUP-001] backup capture drains actor settlement and retains the complete authority fence |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup capture private files.[CC-BACKUP-001] owner hashes only private exact-root capture files |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup capture signed claims.[CC-BACKUP-001] signed capture binds owner cutoff and both physical copies |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup control lifetimes.[CC-BACKUP-001] capture cancellation releases blocked control input |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup control lifetimes.[CC-BACKUP-001] trickled control input cannot extend a held frame deadline |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup health publication readback.[CC-BACKUP-001] expired historical health remains signed but cannot grant freshness |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup health publication readback.[CC-BACKUP-001] generic build refuses historical health readback without input access |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup health publication readback.[CC-BACKUP-001] historical health readback distinguishes complete partial missing and changed output |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup health publication readback.[CC-BACKUP-001] historical health readback reports safe state-specific actions |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup health source.[CC-BACKUP-001] activation plan holds physical minima while live authority advances |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup health source.[CC-BACKUP-001] backup health publication reconciles exact partial and complete bytes |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup health source.[CC-BACKUP-001] generic source preview refuses full backup health before private input |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup health source.[CC-BACKUP-001] health expiry is exclusive and historical readback grants no current validity |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.backup health source.[CC-BACKUP-001] three independently signed current health source roles bind physical WAL and restore facts |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.fenced recovery tail.[CC-BACKUP-001] W1 supplement and independent fence require exact canonical signed shapes |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.fenced recovery tail.[CC-BACKUP-001] W1 tail refuses a missing segment or changed timeline |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.fenced-tail owner private inputs.[CC-BACKUP-001] owner final-tail evidence refuses a symlink and missing signature |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.independent archive host proof.[CC-BACKUP-001] forged or stale archive observation is refused |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.independent archive host proof.[CC-BACKUP-001] signed archive observation binds every final WAL object |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.independent archive host proof.[CC-BACKUP-001] signed archive observation missing one final WAL object is refused |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.independent topology signing keys.[CC-BACKUP-001] PEM rewrapping cannot establish independent native signer keys |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.independent topology signing keys.[CC-BACKUP-001] root-signed topology refuses an aggregate key reused by a host |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.installation loss crash boundaries.[CC-BACKUP-001] W1 response loss reconciles only the historical terminal ticket |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.installation loss crash boundaries.[CC-BACKUP-001] primary COMMIT response loss before W1 preserves exact pending receipt |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.installation loss crash boundaries.[CC-BACKUP-001] terminated in-COMMIT primary transaction keeps W0 pending |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.installation loss owner process.[CC-BACKUP-001] published owner process binds private loss files and exact W0/W1 reconciliation |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.installation loss retirement.[CC-BACKUP-001] changed private denial list refuses before W0 |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.installation loss retirement.[CC-BACKUP-001] missing-evidence unknown-set W0 stays pending until reconciliation |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.installation loss retirement.[CC-BACKUP-001] two owners permanently fence an old installation with known-operation denial |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.installation loss stale pair.[CC-BACKUP-001] one-cluster stale primary or witness cannot reopen accepted operation and artifact |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy cryptography.[CC-BACKUP-001] registered Ed25519 verifier accepts RFC 8032 vector and rejects tampering |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy event hash.[CC-BACKUP-001] managed-copy hash binds exact bytes and signature |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy event hash.[CC-BACKUP-001] managed-copy hash rejects malformed evidence |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy historical witness.[CC-BACKUP-001] historical witness tip rejects wrong hash, epoch and truncation |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy hold.[CC-BACKUP-001] active case hold blocks deletion intent for global and case-linked copies |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy hold.[CC-BACKUP-001] postprune full audit verifies sealed case-linked owner copy |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy inventory.[CC-BACKUP-001] independently signed exact known-copy inventory seals live purge but not deletion |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy owner ingest.[CC-BACKUP-001] orphan managed-copy intent remains unknown without primary row |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy owner ingest.[CC-BACKUP-001] registered signer alone can ingest exact copy and retirement preserves old proof |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy owner ingest.[CC-BACKUP-001] signed custody uncertainty replays and unproven deletion stays closed |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy owner ingest.[CC-BACKUP-001] signed non-backup managed-copy kinds retain exact case scope |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy physical verification.[CC-BACKUP-001] synthetic typed physical proof drives witnessed VERIFY without claiming pair readiness |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy signer authority.[CC-BACKUP-001] orphan signer approval intent stays unknown |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy signer authority.[CC-BACKUP-001] two authenticated human approvals register and retire one witnessed signer |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy signer authority.[CC-BACKUP-001] unknown or service actors cannot approve signer trust |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy signer purpose.[CC-BACKUP-001] deletion approval gate admits only current human verifier grants |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy signer purpose.[CC-BACKUP-001] signer key holder cannot self-approve as owner |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy signer purpose.[CC-BACKUP-001] signer owner approval refuses swapped purpose holder and revoked grant |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.managed-copy verified deletion.[CC-BACKUP-001] independent absence and witnessed one-use approval co-commit verified deletion |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.owner backup capture evidence.[CC-BACKUP-001] owner seals exact distinct signed capture files and detects changed bytes |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.owner backup capture fence.[CC-BACKUP-001] witness read fence holds accepted and owner authority stable during capture |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.owner backup capture frames.[CC-BACKUP-001] owner backup pipe admits only exact bounded private frames |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.owner backup capture frames.[CC-BACKUP-001] owner backup replies remain canonical and non-retained |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.owner backup capture frames.[CC-BACKUP-001] private backup pipe frames are bounded and complete |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.owner backup capture session.[CC-BACKUP-001] owner backup session holds capture fence through sealing and releases before readback |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.owner fenced physical backup capture.[CC-BACKUP-001] real dual BASE capture seals exact owner receipt and changed ciphertext stays uncertain |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.owner physical copy process.[CC-BACKUP-001] both-cluster BASE and WAL copies receive physical verification |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.owner physical copy process.[CC-BACKUP-001] real signed BASE proof drives owner verify-managed-copy and full audit |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.owner physical copy process.[CC-BACKUP-001] retained-copy transition wins authority lock before actor commit health |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.physical fenced recovery tail.[CC-BACKUP-001] signed post-W1 final WAL tail rechecks without readiness |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.physical restored W1 owner handoff.[CC-BACKUP-001] signed physical report and post-isolation fence drive owner W1 |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.physical restored pair.[CC-BACKUP-001] two physical PostgreSQL restores pass full audit and missing WAL refuses |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.product export copy event chain.[CC-BACKUP-001] extra product-export revision cannot evade global copy audit |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.production core boundary.runtime lifecycle.[CC-BACKUP-001] ended commit-health scope refuses a late child mutation |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.production core boundary.runtime lifecycle.[CC-BACKUP-001] real-data bootstrap admits authority setup but no case work |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.production core boundary.runtime lifecycle.[CC-BACKUP-001] real-data outcome without commit-side health is withheld |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.production core boundary.runtime lifecycle.[CC-BACKUP-001] stale backup health stops new mutation but not fenced reads |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.real-data activation approval uncertainty.[CC-BACKUP-001] intent-only owner approval retry preserves exact canonical and DB time |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.real-data activation approval uncertainty.[CC-BACKUP-001] post-primary approval retry reconciles one witness settlement |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.real-data activation owner approvals.[CC-BACKUP-001] isolated witnessed plan review and two distinct human-owner approvals |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.real-data activation owner approvals.[CC-BACKUP-001] stale activation review tip and changed published plan bytes refuse |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore WAL coverage.[CC-BACKUP-001] WAL coverage refuses missing duplicate and changed timeline |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore WAL coverage.[CC-BACKUP-001] WAL segment rollover is contiguous only on one timeline |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore audit barrier.[CC-BACKUP-001] signed audit barrier refuses future cutover flag substitution |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore producer audit role.[CC-BACKUP-001] restored-pair producer audits two clusters without writer capability |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore producer canonical bytes.[CC-BACKUP-001] audit report refuses cutover fence flag substitution |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore producer canonical bytes.[CC-BACKUP-001] restore index refuses path escape and archive object substitution |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore producer canonical bytes.[CC-BACKUP-001] restore producer emits exact canonical signed payload inputs |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore producer canonical bytes.[CC-BACKUP-001] signed backup capture cannot equal later audited cutoff |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore producer output.[CC-BACKUP-001] preexisting restore signature prevents partial report publication |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore producer output.[CC-BACKUP-001] report delivery loss after index and report stays unconfirmed |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore producer output.[CC-BACKUP-001] restore report output is private create-only with signature last |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore producer output.[CC-BACKUP-001] signed checkpoint custody key digest is exact |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore producer output.[CC-BACKUP-001] synthetic report cannot cross production recheck |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restore publication.[CC-BACKUP-001] independent publication root pins binary generation and witness tip |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restored archive custody.[CC-BACKUP-001] restore archive refuses broad-mode or linked root |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restored archive custody.[CC-BACKUP-001] restore consumer rehashes changed missing and swapped private archives |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restored archive custody.[CC-BACKUP-001] signed archive and checkpoint roots cannot overlap |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restored report signer authority.[CC-BACKUP-001] revoked or disabled report-key holder refuses fresh qualification |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restored writer activation reconciliation.[CC-BACKUP-001] witness-only W2 reconciles after proof expiry without new authority |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restored writer activation.[CC-BACKUP-001] W1 settlement alone leaves restored case work quarantined |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restored writer activation.[CC-BACKUP-001] W1 stays quarantined until exact witnessed W2 activation |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.restored writer activation.[CC-BACKUP-001] test-only verifier exercises W2 activation and exact retry |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.runtime signed backup health.[CC-BACKUP-001] signed current health cannot authorize actor Execute after retained-copy loss |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.signed physical restored pair.[CC-BACKUP-001] four verified copies yield a signed synthetic pre-W1 report only |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.writer handoff abort.[CC-BACKUP-001] two owner-held signatures abort pending handoff in three quarantined steps |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.writer handoff approval.[CC-BACKUP-001] owner approval is actor-bound witnessed and exact-retry safe |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.writer handoff holder.[CC-BACKUP-001] revoked custodian or retired checkpoint key cannot back approval |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.writer handoff protocol.[CC-BACKUP-001] owner reconciles witnessed COMMIT after lost primary response without a second settlement |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.writer handoff protocol.[CC-BACKUP-001] postcommit handoff fence withholds claimant-bearing mutation outcome |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.writer handoff protocol.[CC-BACKUP-001] witnessed handoff fences old runtime and audits exact two-cluster cutoff |
| CC-BACKUP-001 | integration | ClaimCore PostgreSQL integration.writer handoff revocation.[CC-BACKUP-001] revoked owner cannot repeat approval but history remains audited |
| CC-BACKUP-001 | unit | ClaimCore deterministic suite.administration diagnostic boundaries.[CC-BACKUP-001] adopted copy transitions accept only signed private evidence paths |
| CC-BACKUP-001 | unit | ClaimCore deterministic suite.administration diagnostic boundaries.[CC-BACKUP-001] external copy publication accepts only one private proposal path |
| CC-BACKUP-001 | unit | ClaimCore deterministic suite.administration diagnostic boundaries.[CC-BACKUP-001] historical health readback accepts only owner-private evidence paths |
| CC-BACKUP-001 | unit | ClaimCore deterministic suite.administration diagnostic boundaries.[CC-BACKUP-001] installation loss retirement accepts only exact private evidence paths |
| CC-BACKUP-001 | unit | ClaimCore deterministic suite.administration diagnostic boundaries.[CC-BACKUP-001] owner abort accepts only signed private evidence paths |
| CC-BACKUP-001 | unit | ClaimCore deterministic suite.administration diagnostic boundaries.[CC-BACKUP-001] owner adoption accepts only one private evidence path |
| CC-BACKUP-001 | unit | ClaimCore deterministic suite.administration diagnostic boundaries.[CC-BACKUP-001] owner backup capture accepts no path or caller-selected executable |
| CC-BACKUP-001 | unit | ClaimCore deterministic suite.private archive hashing.[CC-BACKUP-001] private archive hash is bounded and rejects aliases |
| CC-CLI-001 | acceptance | published authenticated CLI v4 acceptance.[CC-CLI-001] published malformed frames retain exact typed refusals |
| CC-CLI-001 | acceptance | published authenticated CLI v4 acceptance.[CC-CLI-001] published operation and recovery identities reject invalid UUID and digest |
| CC-CLI-001 | acceptance | published authenticated CLI v4 acceptance.[CC-CLI-001] published six-request witnessed lifecycle retains exact history |
| CC-CLI-001 | acceptance | published authenticated CLI v4 acceptance.[CC-CLI-001] witnessed accepted command and exact replay |
| CC-CLI-001 | unit | ClaimCore deterministic suite.HTTP response deadlines.[CC-CLI-001] discovery deadline covers a suspended body after headers |
| CC-CLI-001 | unit | ClaimCore deterministic suite.HTTP response deadlines.[CC-CLI-001] token deadline covers a suspended body after headers |
| CC-CLI-001 | unit | ClaimCore deterministic suite.endpoint extension ownership.[CC-CLI-001] generated responses refuse an unreviewed endpoint family |
| CC-CLI-001 | unit | ClaimCore deterministic suite.endpoint extension ownership.[CC-CLI-001] generated responses refuse duplicate endpoint identifiers |
| CC-CLI-001 | unit | ClaimCore deterministic suite.service response bounds.[CC-CLI-001] CLI bounds service JSON buffering before parsing |
| CC-CLI-001 | unit | ClaimCore deterministic suite.service response bounds.[CC-CLI-001] CLI reads a full history with maximum-length Unicode fields |
| CC-CLI-002 | acceptance | published authenticated CLI v4 acceptance.[CC-CLI-002] malformed owner-private envelope preview retains no preparation |
| CC-CLI-002 | acceptance | published authenticated CLI v4 acceptance.[CC-CLI-002] private recovery export and preview |
| CC-CLI-002 | acceptance | published authenticated CLI v4 acceptance.[CC-CLI-002] unsafe private source refuses without path disclosure |
| CC-CLI-002 | unit | ClaimCore deterministic suite.CLI private-file security.[CC-CLI-002] concurrent source replacement yields one complete inode or a safe refusal |
| CC-CLI-002 | unit | ClaimCore deterministic suite.CLI private-file security.[CC-CLI-002] private export is exclusive, owner-only, and removes its failed partial file |
| CC-CLI-002 | unit | ClaimCore deterministic suite.CLI private-file security.[CC-CLI-002] private export rejects leaf and ancestor links without changing targets |
| CC-CLI-002 | unit | ClaimCore deterministic suite.CLI private-file security.[CC-CLI-002] private source enforces exact size, mode, ACL, and strict UTF-8 |
| CC-CLI-002 | unit | ClaimCore deterministic suite.CLI private-file security.[CC-CLI-002] private source rejects symlinks, ancestor links, directories, and relative paths |
| CC-CLI-002 | unit | ClaimCore deterministic suite.export effect and delivery ownership.[CC-CLI-002] FAILED export preserves core-owned uncertainty guidance |
| CC-CLI-002 | unit | ClaimCore deterministic suite.export effect and delivery ownership.[CC-CLI-002] private destination failure retains observed service completion |
| CC-CLI-002 | unit | ClaimCore deterministic suite.export effect and delivery ownership.[CC-CLI-002] witnessed export is noncancellable and lost completion stays uncertain |
| CC-CLI-002 | unit | ClaimCore deterministic suite.native file boundary.[CC-CLI-002] malformed ancestors refuse while valid Unicode filenames remain exact |
| CC-CLI-002 | unit | ClaimCore deterministic suite.native file boundary.[CC-CLI-002] malformed filename scalars cannot read write hash delete or lock encoded aliases |
| CC-CLI-002 | unit | ClaimCore deterministic suite.native file boundary.[CC-CLI-002] native metadata failure releases an acquired file lock before refusal |
| CC-CLI-002 | unit | ClaimCore deterministic suite.native private-file ancestor race.[CC-CLI-002] descriptor-relative create and lock remain in pinned parent after ancestor swap |
| CC-CLI-003 | integration | ClaimCore PostgreSQL integration.CLI process interruption.[CC-CLI-003] SIGINT after real TLS mutation dispatch exits uncertain |
| CC-CLI-003 | integration | ClaimCore PostgreSQL integration.CLI process interruption.[CC-CLI-003] SIGINT ends input wait before EOF and mutation admission |
| CC-CLI-003 | unit | ClaimCore deterministic suite.CLI token lifetimes.[CC-CLI-003] delayed token delivery cannot create fresh reuse lifetime |
| CC-CLI-003 | unit | ClaimCore deterministic suite.CLI token lifetimes.[CC-CLI-003] forward wall jumps retain conservative UTC expiry |
| CC-CLI-003 | unit | ClaimCore deterministic suite.CLI token lifetimes.[CC-CLI-003] rollback cannot extend elapsed token reuse |
| CC-CLI-003 | unit | ClaimCore deterministic suite.CLI v4 child-process contract.CLI v4 call and session.CLI v4 session and hard break.[CC-CLI-003] oversized session refuses while its input remains open |
| CC-CLI-003 | unit | ClaimCore deterministic suite.CLI-v4 service exit contract.[CC-CLI-003] a refused dismissal preserves earlier submission uncertainty |
| CC-CLI-003 | unit | ClaimCore deterministic suite.CLI-v4 service exit contract.[CC-CLI-003] nested faults preserve earlier exact-operation uncertainty |
| CC-CLI-003 | unit | ClaimCore deterministic suite.CLI-v4 service exit contract.[CC-CLI-003] typed host phases preserve read and mutation delivery knowledge |
| CC-CLI-003 | unit | ClaimCore deterministic suite.endpoint extension ownership.[CC-CLI-003] new case query derives response ownership without mutation authority |
| CC-CLI-003 | unit | ClaimCore deterministic suite.remote frame-local delivery diagnostics.[CC-CLI-003] every generated CLI mutation retains delivery uncertainty |
| CC-CLI-003 | unit | ClaimCore deterministic suite.remote frame-local delivery diagnostics.[CC-CLI-003] interruption after possible mutation preserves uncertainty |
| CC-CLI-003 | unit | ClaimCore deterministic suite.remote frame-local delivery diagnostics.[CC-CLI-003] interruption and mutation dispatch share one atomic gate |
| CC-CLI-003 | unit | ClaimCore deterministic suite.remote frame-local delivery diagnostics.[CC-CLI-003] interruption before dispatch seals future work |
| CC-CLI-003 | unit | ClaimCore deterministic suite.remote invocation contract.[CC-CLI-003] CLI v4 inputs bind exact noncancellable endpoints |
| CC-CLI-003 | unit | ClaimCore deterministic suite.typed protocol diagnostic boundaries.[CC-CLI-003] oversized NDJSON frames stop at the first excess byte |
| CC-CLI-003 | unit | ClaimCore deterministic suite.typed protocol diagnostic boundaries.[CC-CLI-003] oversized session frames terminate without admitting their tail |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.fresh installation qualification.[CC-DB-001] a different calendar is refused without rewriting installation identity |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.fresh installation qualification.[CC-DB-001] baseline DDL failure rolls back the entire namespace before commit |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.fresh installation qualification.[CC-DB-001] concurrent different calendars cannot both win initialization |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.fresh installation qualification.[CC-DB-001] concurrent identical initializers serialize to one complete installation |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.fresh installation qualification.[CC-DB-001] fresh initialization atomically installs the current identity and calendar |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.fresh installation qualification.[CC-DB-001] identical initialization and read-only verification preserve every stored byte |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.fresh installation qualification.[CC-DB-001] verification and maintenance refuse an absent baseline without creating it |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.unsupported installation refusal.[CC-DB-001] a matching marker does not conceal missing current integrity constraints |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.unsupported installation refusal.[CC-DB-001] all old installation markers are refused without touching evidence or history |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.unsupported installation refusal.[CC-DB-001] an unmarked occupied namespace is refused without deleting its contents |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.unsupported installation refusal.[CC-DB-001] current-looking tables without a baseline marker remain unsupported |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.unsupported installation refusal.[CC-DB-001] even an empty pre-existing namespace is not silently adopted |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.unsupported installation refusal.[CC-DB-001] malformed or executable baseline lookalikes are not queried or repaired |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.unsupported installation refusal.[CC-DB-001] mixing a current marker with historical metadata cannot authorize old storage |
| CC-DB-001 | fresh-baseline-qualification | ClaimCore fresh baseline qualification.unsupported installation refusal.[CC-DB-001] unknown identities and altered digests are refused with no repair |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog admission.[CC-DB-001] catalog drift refuses extra and altered objects |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog admission.[CC-DB-001] same-name weaker enforced check is refused |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token admission.[CC-DB-001] a changed catalog is verified again and so is its reversal |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token admission.[CC-DB-001] a failed verification is never remembered |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token admission.[CC-DB-001] a verified unchanged catalog is not verified again |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token admission.[CC-DB-001] an elapsed interval verifies again |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token admission.[CC-DB-001] an unreadable token means unknown, never unchanged |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token admission.[CC-DB-001] concurrent first checkouts all succeed |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token admission.[CC-DB-001] verification follows the connection identity, not a shared entry |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token.[CC-DB-001] comments, statistics, temporary and unrelated objects keep the token |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token.[CC-DB-001] every structural or privilege change moves the primary catalog token |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token.[CC-DB-001] repeated reads of an unchanged catalog agree |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token.[CC-DB-001] the token names every catalog the guarded checks read |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.guarded runtime admission.[CC-DB-001] a privilege drift is refused on the guarded path |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.guarded runtime admission.[CC-DB-001] the cancellable runtime admission is vouched for and refuses the same drift |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.guarded runtime admission.[CC-DB-001] the runtime refuses drift the catalog token vouched for before, then admits its reversal |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.fresh installation qualification.[CC-DB-001] a different calendar is refused without rewriting installation identity |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.fresh installation qualification.[CC-DB-001] baseline DDL failure rolls back the entire namespace before commit |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.fresh installation qualification.[CC-DB-001] concurrent different calendars cannot both win initialization |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.fresh installation qualification.[CC-DB-001] concurrent identical initializers serialize to one complete installation |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.fresh installation qualification.[CC-DB-001] fresh initialization atomically installs the current identity and calendar |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.fresh installation qualification.[CC-DB-001] identical initialization and read-only verification preserve every stored byte |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.fresh installation qualification.[CC-DB-001] verification and maintenance refuse an absent baseline without creating it |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.real-data activation mechanics.[CC-DB-001] settled witness activation repairs a missing primary projection |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.real-data activation mechanics.[CC-DB-001] witnessed owner activation is one-way and exactly reconcilable |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.unsupported installation refusal.[CC-DB-001] a matching marker does not conceal missing current integrity constraints |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.unsupported installation refusal.[CC-DB-001] all old installation markers are refused without touching evidence or history |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.unsupported installation refusal.[CC-DB-001] an unmarked occupied namespace is refused without deleting its contents |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.unsupported installation refusal.[CC-DB-001] current-looking tables without a baseline marker remain unsupported |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.unsupported installation refusal.[CC-DB-001] even an empty pre-existing namespace is not silently adopted |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.unsupported installation refusal.[CC-DB-001] malformed or executable baseline lookalikes are not queried or repaired |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.unsupported installation refusal.[CC-DB-001] mixing a current marker with historical metadata cannot authorize old storage |
| CC-DB-001 | integration | ClaimCore PostgreSQL integration.unsupported installation refusal.[CC-DB-001] unknown identities and altered digests are refused with no repair |
| CC-DB-001 | unit | ClaimCore deterministic suite.administration diagnostic boundaries.[CC-DB-001] real-data owner commands require exact plan and approvals |
| CC-DB-002 | unit | ClaimCore deterministic suite.Database admin private-file boundary.[CC-DB-002] schema-owner credential rejects unsafe mode, links, UTF-8, and size before database access |
| CC-DOM-001 | integration | ClaimCore PostgreSQL integration.domain durable evidence.[CC-DOM-001] fresh SQL refuses terminal business revisions and unsupported rule evidence |
| CC-DOM-001 | unit | ClaimCore deterministic suite.correction group invariants.[CC-DOM-001] explicit paid reaffirmation cannot retain a zero replacement decision |
| CC-DOM-001 | unit | ClaimCore deterministic suite.domain accepted-state admission.[CC-DOM-001] restoration refuses invalid paid and chronological states |
| CC-DOM-001 | unit | ClaimCore deterministic suite.domain validation.[CC-DOM-001] business Unicode preserves joining and authored display controls through restoration |
| CC-DOM-001 | unit | ClaimCore deterministic suite.generated semantic field contract.semantic field schema.[CC-DOM-001] semantic contract renders exactly thirteen ordered field descriptors |
| CC-DOM-002 | integration | ClaimCore PostgreSQL integration.business calendar capture.[CC-DOM-002] stored-zone capture pairs one instant across leap midnight and DST boundaries |
| CC-DOM-002 | integration | ClaimCore PostgreSQL integration.domain durable evidence.[CC-DOM-002] clock rollback amendments persist and retain the current rule revision |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.correction assertions.[CC-DOM-002] a closed paid correction reaffirms accepted dates after rollback |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.correction assertions.[CC-DOM-002] a correction cannot hide a new future incident behind accepted dates |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.correction assertions.[CC-DOM-002] equivalent amount spelling remains a correction no-op after rollback |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.ordinary assertions.[CC-DOM-002] amendment retains accepted dates after clock rollback |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.ordinary assertions.[CC-DOM-002] an unchanged notification cannot hide a new future incident |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.ordinary assertions.[CC-DOM-002] changing an accepted decision date asserts a new date |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.ordinary assertions.[CC-DOM-002] clearing payment removes its date reaffirmation authority |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.ordinary assertions.[CC-DOM-002] decision amount replacement retains its accepted date after rollback |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.ordinary assertions.[CC-DOM-002] initial decisions cannot reuse a future historical date |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.ordinary assertions.[CC-DOM-002] newly replaced notification dates are checked independently |
| CC-DOM-002 | unit | ClaimCore deterministic suite.business date assertions.ordinary assertions.[CC-DOM-002] withdrawing a decision removes its date reaffirmation authority |
| CC-DOM-002 | unit | ClaimCore deterministic suite.correction group invariants.[CC-DOM-002] all correction choices match an independent eight-state matrix |
| CC-DOM-002 | unit | ClaimCore deterministic suite.domain accepted-state admission.[CC-DOM-002] malformed payload admission precedes stale revision and eligibility |
| CC-DOM-002 | unit | ClaimCore deterministic suite.state guard and advertised actions.[CC-DOM-002] each state advertises and enforces the specified command set |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.adopted external copy transitions.[CC-ERASE-001] adopted external exact signed absence completes deletion |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.case erasure authority fences.[CC-ERASE-001] case-linked copy registration refuses a witnessed erasure fence |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.case erasure authority fences.[CC-ERASE-001] erasure fence rejects new case grants but permits revocation |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.case erasure fence.[CC-ERASE-001] erasure request co-commits keyed reference and operation denials |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.case erasure fence.[CC-ERASE-001] full audit rejects a missing erasure operation denial |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.case erasure live purge.[CC-ERASE-001] owner purge removes live claimant rows but retains keyed pending proof |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.case erasure old artifact.[CC-ERASE-001] signed old recovery artifact cannot revive a live-purged case |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.case erasure owner admission.[CC-ERASE-001] missing complete copy inventory refuses live purge before intent |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.case erasure owner admission.[CC-ERASE-001] orphan CASE intent keeps owner purge unknowable |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.case erasure owner admission.[CC-ERASE-001] witnessed hold after approvals still fences owner purge |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.case tombstone stewardship.[CC-ERASE-001] tombstone prune approvals and holds remain witnessed and auditable |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.copy adoption owner approval.[CC-ERASE-001] product export adoption approval and tamper audit remain pending |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.external copy publication owner process.[CC-ERASE-001] owner process publishes one signed pre-fence external copy |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.external copy publication.[CC-ERASE-001] signed PRESENT publication replays exactly and erasure fences new copies |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.owner managed-copy adoption process.[CC-ERASE-001] owner process reads exact private signed adoption evidence |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.owner signed copy adoption.[CC-ERASE-001] signed product export ADOPT retains original receipt and pending state |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.owner witness prune process.[CC-ERASE-001] owner process prunes from private proposal and signed inventory with exact retry |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.owner witness prune process.[CC-ERASE-001] owner process retries committed intent and refuses changed proposal |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.postprune evidence audit.[CC-ERASE-001] changed postprune target root quarantines full audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.postprune evidence audit.[CC-ERASE-001] missing middle CASE witness metadata quarantines full audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.postprune evidence audit.[CC-ERASE-001] missing postprune target row quarantines full audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.postprune evidence audit.[CC-ERASE-001] missing prune approval receipt quarantines full audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.postprune evidence audit.[CC-ERASE-001] missing retained prune settlement ciphertext quarantines full audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.postprune evidence audit.[CC-ERASE-001] reintroduced CASE ciphertext quarantines full audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.settled erasure fence.[CC-ERASE-001] a mismatched request tombstone cannot mint pending proof |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.settled erasure fence.[CC-ERASE-001] complete case witness scan stages bounded HMAC denials |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.settled erasure fence.[CC-ERASE-001] only a settled erasure fence advances to pending |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.settled erasure fence.[CC-ERASE-001] two witnessed erasure approvals cannot impersonate owner purge |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.settled erasure fence.[CC-ERASE-001] unlinked case intent cannot certify operation coverage |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal approval audit tamper.[CC-ERASE-001] terminal approval actor tamper quarantines audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal approval audit tamper.[CC-ERASE-001] terminal approval canonical tamper quarantines audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal approval audit tamper.[CC-ERASE-001] terminal approval grant revision tamper quarantines audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal approval audit tamper.[CC-ERASE-001] terminal approval witness scope tamper quarantines audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal erasure draft approvals.[CC-ERASE-001] active hold blocks new terminal approval without rewriting old one |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal erasure draft approvals.[CC-ERASE-001] changed terminal approval proof quarantines full audit |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal erasure draft approvals.[CC-ERASE-001] revoked steward cannot add a terminal approval |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal erasure draft approvals.[CC-ERASE-001] terminal approval hides ungranted case and refuses expiry |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal erasure draft approvals.[CC-ERASE-001] terminal draft has two witnessed stewards without phase promotion |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal erasure uncertainty.[CC-ERASE-001] terminal approval lost settlement retries exact without duplicate |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal owner execution.[CC-ERASE-001] missing all-absent issuer leaves erasure pending |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal owner execution.[CC-ERASE-001] synthetic owner copy phase co-commits witnessed event and uses |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal signed copy absence owner process.[CC-ERASE-001] owner process certifies signed zero-copy absence without private output |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal signed copy absence.[CC-ERASE-001] a copy registered after signed inventory blocks terminal certification |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal signed copy absence.[CC-ERASE-001] owner signed all-ABSENT zero-copy certificate advances pending privacy phase |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.terminal signed copy absence.[CC-ERASE-001] signed known unmanaged copy remains a terminal liability |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.witness payload prune export.[CC-ERASE-001] postprune audit retains unknown export liability and denies old artifact |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.witness payload prune.[CC-ERASE-001] owner prune removes only sealed CASE ciphertext atomically |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.witness prune owner SQL.[CC-ERASE-001] owner prune function rejects NULL and zero UUID identities |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.witness prune uncertainty.[CC-ERASE-001] exact settled prune replay survives witness key rotation |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.witness prune uncertainty.[CC-ERASE-001] partial prune retries exact intent and refuses changed proposal |
| CC-ERASE-001 | integration | ClaimCore PostgreSQL integration.witness prune uncertainty.[CC-ERASE-001] unexpected postcutoff CASE intent prevents witness ciphertext deletion |
| CC-ERASE-001 | unit | ClaimCore deterministic suite.Database admin private-file boundary.[CC-ERASE-001] database-free help exposes the separate witness payload prune command |
| CC-ERASE-001 | unit | ClaimCore deterministic suite.Database admin private-file boundary.[CC-ERASE-001] owner witness prune refuses unsafe private inputs before database access |
| CC-ERASE-001 | unit | ClaimCore deterministic suite.administration diagnostic boundaries.[CC-ERASE-001] owner purge and prune accept only private proposal path |
| CC-ERASE-001 | unit | ClaimCore deterministic suite.administration diagnostic boundaries.[CC-ERASE-001] terminal copy absence accepts only one private proposal path |
| CC-ERASE-001 | unit | ClaimCore deterministic suite.owner live purge authorization.[CC-ERASE-001] hold and tombstone prevent a second live purge |
| CC-ERASE-001 | unit | ClaimCore deterministic suite.owner live purge authorization.[CC-ERASE-001] owner live purge authorization keeps privacy pending |
| CC-ERASE-001 | unit | ClaimCore deterministic suite.owner live purge authorization.[CC-ERASE-001] owner purge uses technical execution and two distinct stewards |
| CC-LIFE-001 | integration | ClaimCore PostgreSQL integration.[CC-LIFE-001] [CC-REC-001] a witnessed hold retains terminal preparation evidence until release |
| CC-LIFE-001 | integration | ClaimCore PostgreSQL integration.authority expiration under lock.[CC-LIFE-001] approval issuance uses current database time after lock delay |
| CC-LIFE-001 | integration | ClaimCore PostgreSQL integration.authority expiration under lock.[CC-LIFE-001] delayed approval consumption refuses while expired exact readback survives |
| CC-LIFE-001 | integration | ClaimCore PostgreSQL integration.case lifecycle capacity.[CC-LIFE-001] 257th active hold is refused before a witness intent |
| CC-LIFE-001 | integration | ClaimCore PostgreSQL integration.case lifecycle capacity.[CC-LIFE-001] concurrent stewards cannot consume a third approval slot |
| CC-LIFE-001 | integration | ClaimCore PostgreSQL integration.case lifecycle reconciliation.[CC-LIFE-001] owner reconciles committed lifecycle intent without inventing an orphan outcome |
| CC-LIFE-001 | integration | ClaimCore PostgreSQL integration.case lifecycle storage.[CC-LIFE-001] historical payment assertion requires two witnessed void approvals |
| CC-LIFE-001 | integration | ClaimCore PostgreSQL integration.case lifecycle storage.[CC-LIFE-001] witnessed erasure request and hold fence ordinary work without claiming purge |
| CC-LIFE-001 | integration | ClaimCore PostgreSQL integration.case lifecycle storage.[CC-LIFE-001] witnessed void preserves history and reinstatement requires distinct stewards |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.Case disposition.[CC-LIFE-001] duplicate, self, expired and stale approvals fail closed |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.Case disposition.[CC-LIFE-001] explicit reinstatement is a new revision; erasure dominates |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.Case disposition.[CC-LIFE-001] malformed reason, time and identity are refused |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.Case disposition.[CC-LIFE-001] payment assertion requires two bound approvals and is not reversed |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.Case disposition.[CC-LIFE-001] void advances revision without changing business fields and fences ordinary access |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.Case erasure.[CC-LIFE-001] copy count and writer generation bind the terminal proof |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.Case erasure.[CC-LIFE-001] final requires reviewed horizon and exact recovery fence |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.Case erasure.[CC-LIFE-001] holds remain orthogonal and block owner certification |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.Case erasure.[CC-LIFE-001] structural copy evidence and distinct stewards gate later phase |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.lifecycle projection.[CC-LIFE-001] a case has at most 256 simultaneous active holds |
| CC-LIFE-001 | unit | ClaimCore deterministic suite.lifecycle projection.[CC-LIFE-001] lifecycle SQL projection loader refuses duplicate and terminal holds |
| CC-REC-001 | concurrency-qualification | ClaimCore concurrency qualification.PostgreSQL recovery races.[CC-REC-001] simultaneous exact resolves retain one accepted revision |
| CC-REC-001 | concurrency-qualification | ClaimCore concurrency qualification.PostgreSQL recovery races.[CC-REC-001] simultaneous resolve and dismiss have one lifecycle winner |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL operation authority lifecycle.[CC-REC-001] a revoked worker cannot execute or lose unsettled preparation evidence |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL operation authority lifecycle.[CC-REC-001] attempt 65 is refused while bounded evidence pages remain available |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL preparation boundary.[CC-REC-001] concurrent same-ID retain reports one creator and one existing replay |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery cancellation.[CC-REC-001] caller cancellation after commit start cannot relabel outcome |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery cancellation.[CC-REC-001] cancellation immediately before technical commit rolls back synthetic marker |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery cancellation.[CC-REC-001] cancelled attempt admission creates no marker or attempt |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery cancellation.[CC-REC-001] cancelled dismissal leaves preparation actionable |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery cancellation.[CC-REC-001] cancelled recovery reads return cancellation without disclosure |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery cancellation.[CC-REC-001] cancelled retain has no durable preparation |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery cancellation.[CC-REC-001] cancelled settlement leaves admitted attempt unsettled |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery cancellation.[CC-REC-001] technical commit-start failure remains uncertain |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery evidence.[CC-REC-001] exact replay preserves first producer provenance |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery evidence.[CC-REC-001] inspect preserves an earlier unsettled attempt |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery evidence.[CC-REC-001] inspect projects the accepted attempt and settlement |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery evidence.[CC-REC-001] recovery workflow exposes no raw canonical import |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery races.[CC-REC-001] simultaneous exact resolves retain one accepted revision |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery races.[CC-REC-001] simultaneous resolve and dismiss have one lifecycle winner |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery state table.[CC-REC-001] accepted receipt permits replay and export but never dismissal |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery state table.[CC-REC-001] dismissed preparation is idempotent and cannot be exported |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL recovery state table.[CC-REC-001] missing preparation has explicit read and action outcomes |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.PostgreSQL terminal capacity.[CC-REC-001] 1,024 recent accepted operations leave capacity for a distinct fresh preparation |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.[CC-LIFE-001] [CC-REC-001] a witnessed hold retains terminal preparation evidence until release |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.[CC-REC-001] [CC-WIT-001] revocation fences future authority without rewriting an orphan acceptance |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.[CC-REC-001] a settled rejection cannot be re-executed or issue an orphan intent |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.[CC-REC-001] recovery inspection uses one snapshot across concurrent owner pruning |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.actor-bound v3 recovery import.[CC-REC-001] import requires current grant, authenticated artifact and active case |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.owner-private recovery artifact key policy.[CC-REC-001] artifact key policy rejects unsafe bounds |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.owner-private recovery artifact key policy.[CC-REC-001] artifact keys rotate with bounded issue and verify horizons |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.technical witness phases.[CC-REC-001] unaccepted unrevoked PREPARE is not owner-prunable |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.typed retained-request recovery.[CC-REC-001] accepted receipt blocks dismissal without a lifecycle marker |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.typed retained-request recovery.[CC-REC-001] export and resolve preserve the exact retained request |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.typed retained-request recovery.[CC-REC-001] foreign installation lineage is rejected before submission |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.typed retained-request recovery.[CC-REC-001] incompatible request fingerprint is rejected before submission |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.witnessed recovery artifact export.[CC-REC-001] export retries reuse settled ciphertext and managed copy |
| CC-REC-001 | integration | ClaimCore PostgreSQL integration.witnessed recovery artifact export.[CC-REC-001] owner pruning and export serialize on one operation lock |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery cancellation.[CC-REC-001] caller cancellation after commit start cannot relabel outcome |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery cancellation.[CC-REC-001] cancellation immediately before technical commit rolls back synthetic marker |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery cancellation.[CC-REC-001] cancelled attempt admission creates no marker or attempt |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery cancellation.[CC-REC-001] cancelled dismissal leaves preparation actionable |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery cancellation.[CC-REC-001] cancelled recovery reads return cancellation without disclosure |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery cancellation.[CC-REC-001] cancelled retain has no durable preparation |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery cancellation.[CC-REC-001] cancelled settlement leaves admitted attempt unsettled |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery cancellation.[CC-REC-001] technical commit-start failure remains uncertain |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery evidence.[CC-REC-001] exact replay preserves first producer provenance |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery evidence.[CC-REC-001] inspect preserves an earlier unsettled attempt |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery evidence.[CC-REC-001] inspect projects the accepted attempt and settlement |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery evidence.[CC-REC-001] recovery workflow exposes no raw canonical import |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery state table.[CC-REC-001] accepted receipt permits replay and export but never dismissal |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery state table.[CC-REC-001] dismissed preparation is idempotent and cannot be exported |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.PostgreSQL recovery state table.[CC-REC-001] missing preparation has explicit read and action outcomes |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.typed retained-request recovery.[CC-REC-001] accepted receipt blocks dismissal without a lifecycle marker |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.typed retained-request recovery.[CC-REC-001] export and resolve preserve the exact retained request |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.typed retained-request recovery.[CC-REC-001] foreign installation lineage is rejected before submission |
| CC-REC-001 | recovery-qualification | ClaimCore recovery qualification.typed retained-request recovery.[CC-REC-001] incompatible request fingerprint is rejected before submission |
| CC-REC-001 | unit | ClaimCore deterministic suite.CLI-v4 service exit contract.[CC-REC-001] exact prepare replay wires observed acceptance and retained recovery distinctly |
| CC-REC-001 | unit | ClaimCore deterministic suite.accepted receipt first.[CC-REC-001] mismatched retained identity refuses without preparation metadata |
| CC-REC-001 | unit | ClaimCore deterministic suite.encrypted recovery artifact v3.[CC-REC-001] v3 encrypts exact request and binds attribution |
| CC-REC-001 | unit | ClaimCore deterministic suite.encrypted recovery artifact v3.[CC-REC-001] v3 enforces epoch expiry and key policy |
| CC-REC-001 | unit | ClaimCore deterministic suite.encrypted recovery artifact v3.[CC-REC-001] v3 rejects tampering and v2 |
| CC-REC-001 | unit | ClaimCore deterministic suite.exact retained identity.[CC-REC-001] Execute rejects a same-ID same-digest record with different canonical bytes |
| CC-REC-001 | unit | ClaimCore deterministic suite.exact retained identity.[CC-REC-001] concurrent exact retained admission classifies creator and replay |
| CC-REC-001 | unit | ClaimCore deterministic suite.exact retained identity.[CC-REC-001] exact Prepare retry after acceptance returns an observed receipt |
| CC-REC-001 | unit | ClaimCore deterministic suite.exact retained identity.[CC-REC-001] exact retained Prepare retry after another commit directs Recovery |
| CC-REC-001 | unit | ClaimCore deterministic suite.export effect and delivery ownership.[CC-REC-001] signed export completion cannot become a late cancellation |
| CC-REC-001 | unit | ClaimCore deterministic suite.fresh recovery format boundary.[CC-REC-001] current request and signed artifact preserve exact identity |
| CC-REC-001 | unit | ClaimCore deterministic suite.fresh recovery format boundary.[CC-REC-001] historical canonical requests are refused |
| CC-REC-001 | unit | ClaimCore deterministic suite.fresh recovery format boundary.[CC-REC-001] plaintext v2 and unsigned records are refused |
| CC-REC-001 | unit | ClaimCore deterministic suite.fresh recovery format boundary.[CC-REC-001] raw noncanonical bytes are not imported |
| CC-REC-001 | unit | ClaimCore deterministic suite.fresh recovery format boundary.[CC-REC-001] signed envelope metadata and layout tampering are refused |
| CC-REC-001 | unit | ClaimCore deterministic suite.preparation attribution.[CC-REC-001] retained OPEN preserves case and preparer authority |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery authority closure.[CC-REC-001] durable revocation closes exact identity before and after preparation pruning |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery authority closure.[CC-REC-001] recovery list and attempt cursors reject a different view or operation |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery uncertainty boundaries.[CC-REC-001] cancellation after admitted attempt preserves definite acceptance |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery uncertainty boundaries.[CC-REC-001] cancellation before attempt admission leaves the claim untouched |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery uncertainty boundaries.[CC-REC-001] combined commit uncertainty never fabricates accepted settlement |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery uncertainty boundaries.[CC-REC-001] commit-unknown execution is never settled |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery uncertainty boundaries.[CC-REC-001] definite pre-commit failure is settled as a failure |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery uncertainty boundaries.[CC-REC-001] definite stale rejection is settled without another receipt |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery uncertainty boundaries.[CC-REC-001] receipt between observation and attempt returns exact replay |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery uncertainty boundaries.[CC-REC-001] thrown combined commit is unresolved |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery uncertainty boundaries.[CC-REC-001] thrown execution after admission remains unresolved |
| CC-REC-001 | unit | ClaimCore deterministic suite.recovery uncertainty boundaries.[CC-REC-001] unknown attempt admission never executes the claim |
| CC-REC-001 | unit | ClaimCore deterministic suite.signed recovery import attribution.[CC-REC-001] signed import preserves A and records importer B |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed core facade.recovery.[CC-REC-001] existing receipt with different bytes cannot masquerade as replay |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed recovery cancellation.[CC-REC-001] cancellation during receipt observation returns cancelled read |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed recovery cancellation.[CC-REC-001] cancellation during recovery inspect hides receipt result |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed recovery cancellation.[CC-REC-001] cancellation during resolve observation prevents attempt |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed recovery cancellation.[CC-REC-001] cancelled combined execution is definite before commit |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed recovery cancellation.[CC-REC-001] commit-start dismissal failure remains state unknown |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed recovery cancellation.[CC-REC-001] commit-start import retention failure remains state unknown |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed recovery cancellation.[CC-REC-001] definite attempt cancellation prevents claim execution |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed recovery cancellation.[CC-REC-001] definite dismiss cancellation remains actionable |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed recovery cancellation.[CC-REC-001] definite import retention cancellation does not submit |
| CC-REC-001 | unit | ClaimCore deterministic suite.typed recovery cancellation.[CC-REC-001] definite retain cancellation becomes cancelled before admission |
| CC-REC-001 | unit | encrypted recovery artifact v3.[CC-REC-001] v3 encrypts exact request and binds attribution |
| CC-REC-001 | unit | encrypted recovery artifact v3.[CC-REC-001] v3 enforces epoch expiry and key policy |
| CC-REC-001 | unit | encrypted recovery artifact v3.[CC-REC-001] v3 rejects tampering and v2 |
| CC-REC-001 | unit | fresh recovery format boundary.[CC-REC-001] current request and signed artifact preserve exact identity |
| CC-REC-001 | unit | fresh recovery format boundary.[CC-REC-001] historical canonical requests are refused |
| CC-REC-001 | unit | fresh recovery format boundary.[CC-REC-001] plaintext v2 and unsigned records are refused |
| CC-REC-001 | unit | fresh recovery format boundary.[CC-REC-001] raw noncanonical bytes are not imported |
| CC-REC-001 | unit | fresh recovery format boundary.[CC-REC-001] signed envelope metadata and layout tampering are refused |
| CC-REC-001 | unit | preparation attribution.[CC-REC-001] retained OPEN preserves case and preparer authority |
| CC-REC-001 | unit | signed recovery import attribution.[CC-REC-001] signed import preserves A and records importer B |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.production core boundary.runtime lifecycle.[CC-RUN-001] bounded disposal defers source cleanup until the final lease |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.production core boundary.runtime lifecycle.[CC-RUN-001] cancelled opening returns a safe typed fault |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.production core boundary.runtime lifecycle.[CC-RUN-001] disposal closes admission and drains a held operation |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.production core boundary.runtime lifecycle.[CC-RUN-001] disposed runtime refuses retained normal and recovery facades |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime cleanup ownership.[CC-RUN-001] asynchronous shutdown waits for admitted leases and cleanup |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime cleanup ownership.[CC-RUN-001] deferred cleanup failure preserves admitted outcomes and safe completion |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime cleanup ownership.[CC-RUN-001] runtime cleanup attempts every resource despite disposal faults |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime cleanup ownership.[CC-RUN-001] runtime closes admission before cleanup completion |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime construction ownership.[CC-RUN-001] partial construction unwinds every child despite cleanup faults |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime construction ownership.[CC-RUN-001] successful construction transfers children to their parent |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime construction ownership.[CC-RUN-001] witness adoption transfers store and custody together |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime construction ownership.[CC-RUN-001] witness custody failure closes the actual store |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime lifecycle under PostgreSQL.[CC-RUN-001] admitted mutation remains accepted while runtime drains |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime lifecycle under PostgreSQL.[CC-RUN-001] admitted query keeps its typed outcome while runtime drains |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime lifecycle under PostgreSQL.[CC-RUN-001] mid-open cancellation interrupts schema inspection |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime lifecycle under PostgreSQL.[CC-RUN-001] thrown opening closes its owned source |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime scheduling and shutdown.[CC-RUN-001] audit stop is nonblocking and cleanup joins cancellation callbacks |
| CC-RUN-001 | integration | ClaimCore PostgreSQL integration.runtime scheduling and shutdown.[CC-RUN-001] shutdown stops audit before draining admitted actor work |
| CC-WEB-001 | unit | ClaimCore deterministic suite.CLI TLS trust.[CC-WEB-001] issuer TLS leaf requires server purpose |
| CC-WEB-001 | unit | ClaimCore deterministic suite.CLI TLS trust.[CC-WEB-001] private TLS root validity has exact boundaries |
| CC-WEB-001 | web | ClaimCore.Web.OIDC authentication foundation.[CC-WEB-001] OIDC cookie uses revocable server-side ticket |
| CC-WEB-001 | web | ClaimCore.Web.OIDC authentication foundation.[CC-WEB-001] OIDC mode closes legacy case work until grants exist |
| CC-WEB-001 | web | ClaimCore.Web.OIDC authentication foundation.[CC-WEB-001] bearer principal uses explicit client classification |
| CC-WEB-001 | web | ClaimCore.Web.OIDC authentication foundation.[CC-WEB-001] browser and bearer credentials never mix |
| CC-WEB-001 | web | ClaimCore.Web.OIDC authentication foundation.[CC-WEB-001] human and service principals remain distinct |
| CC-WEB-001 | web | ClaimCore.Web.OIDC authentication foundation.[CC-WEB-001] metadata pins issuer and secure endpoints |
| CC-WEB-001 | web | ClaimCore.Web.OIDC authentication foundation.[CC-WEB-001] middleware pins OIDC and JWT validation policy |
| CC-WEB-001 | web | ClaimCore.Web.OIDC authentication foundation.[CC-WEB-001] production issuer requires HTTPS and exact URI |
| CC-WEB-001 | web | ClaimCore.Web.OIDC configuration.[CC-WEB-001] OIDC configuration rejects HTTP issuer |
| CC-WEB-001 | web | ClaimCore.Web.OIDC configuration.[CC-WEB-001] private CA rejects malformed and broad-permission files |
| CC-WEB-001 | web | ClaimCore.Web.OIDC configuration.[CC-WEB-001] private issuer CA rejects wrong purpose and validity window |
| CC-WEB-001 | web | ClaimCore.Web.OIDC session route boundaries.[CC-WEB-001] OIDC challenge returns locally after PKCE callback |
| CC-WEB-001 | web | ClaimCore.Web.OIDC session route boundaries.[CC-WEB-001] OIDC cookie session logs out through CSRF admission |
| CC-WEB-001 | web | ClaimCore.Web.OIDC session route boundaries.[CC-WEB-001] definition refuses a core denial without disclosure |
| CC-WEB-001 | web | ClaimCore.Web.OIDC session route boundaries.[CC-WEB-001] shared bootstrap route is physically absent |
| CC-WEB-001 | web | ClaimCore.Web.OIDC startup requests.[CC-WEB-001] startup deadline covers a suspended metadata body |
| CC-WEB-001 | web | ClaimCore.Web.OIDC startup requests.[CC-WEB-001] startup requests one exact discovery path for root and path issuers |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] HTTP failure statuses and execution phases use exact generated host bodies |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] OIDC logout revokes the browser ticket before disclosure |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] case and operation routes preserve found, absent, rejected, failed, and cancelled core outcomes |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint authority.approveCopyAdoption dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint authority.approveCopyDeletion dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint authority.approveCopySigner dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint authority.approveRealDataActivation dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint authority.approveWriterHandoff dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint authority.observe dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint authority.register dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint authority.reviewRealDataActivation dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint authority.setEnabled dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint authority.setGrant dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint lifecycle.apply dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint lifecycle.approve dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint lifecycle.review dispatches authenticated route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint tombstone.approvePrune dispatches authenticated tombstone route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint tombstone.approveTerminal dispatches authenticated tombstone route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint tombstone.changeHold dispatches authenticated tombstone route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] endpoint tombstone.review dispatches authenticated tombstone route |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] every authenticated route rejects absent session, origin, and antiforgery before core dispatch |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] exact command draft transport accepts all eight semantic command variants |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] inaccessible and absent case or operation have identical public responses |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] invalid UTF-8 is refused before query decoding and core dispatch |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] preparation and submission routes preserve definite, unknown, and exact-digest outcomes |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] production route map dispatches the exact v3 endpoints |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] raw import routes enforce exact media, body bounds, and source digest |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] recovery list cursor round-trips opaquely and malformed cursors are typed refusals |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] recovery list, inspect, resolve, dismiss, and export preserve typed lifecycle outcomes |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] recovery preview and retain routes preserve source digest, refusal, and uncertainty outcomes |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] retired and unknown API routes return typed no-store 404 |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 TestServer.[CC-WEB-001] session login and logout revoke admission through real cookies and antiforgery |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 input.[CC-WEB-001] decodes one semantic command-draft shape |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 input.[CC-WEB-001] distinguishes Boolean recovery confirmation and Domain command inputs |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 input.[CC-WEB-001] enforces streaming byte limits before decoding |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 input.[CC-WEB-001] gives session mutations closed body schemas |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 input.[CC-WEB-001] keeps endpoint request bodies exact and typed |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 input.[CC-WEB-001] rejects malformed case-target and page scalar shapes |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 input.[CC-WEB-001] validates canonical operation and source-digest scalars |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 wire projection.[CC-WEB-001] all JSON host wrappers deliver Contracts-owned codec bytes |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 wire projection.[CC-WEB-001] consumes HTTP routes and raw constraints from ClaimCore.Contracts |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 wire projection.[CC-WEB-001] exposes independently fingerprinted semantic and Web contracts |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 wire projection.[CC-WEB-001] keeps host admission errors outside application outcomes |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 wire projection.[CC-WEB-001] preserves command endpoint identities in typed outcome bodies |
| CC-WEB-001 | web | ClaimCore.Web.Web HTTP-v3 wire projection.[CC-WEB-001] serializes logout as an anonymous session snapshot |
| CC-WEB-001 | web | ClaimCore.Web.Web actor admission after synthetic authentication.[CC-WEB-001] bearer GET rejects unsafe transport before actor lookup |
| CC-WEB-001 | web | ClaimCore.Web.Web actor admission after synthetic authentication.[CC-WEB-001] bearer actor admission binds verified client and subject |
| CC-WEB-001 | web | ClaimCore.Web.Web actor admission after synthetic authentication.[CC-WEB-001] bearer transport refuses unsafe shapes before authentication |
| CC-WEB-001 | web | ClaimCore.Web.Web actor admission after synthetic authentication.[CC-WEB-001] browser cookie admission binds and rechecks a human actor |
| CC-WEB-001 | web | ClaimCore.Web.Web actor admission after synthetic authentication.[CC-WEB-001] browser cookie refuses unsafe transport before actor lookup |
| CC-WEB-001 | web | ClaimCore.Web.Web actor admission after synthetic authentication.[CC-WEB-001] failed or malformed bearer identities never select an actor |
| CC-WEB-001 | web | ClaimCore.Web.Web host private-file security.[CC-WEB-001] OIDC host preserves retained old credential evidence |
| CC-WEB-001 | web | ClaimCore.Web.Web host private-file security.[CC-WEB-001] existing state mode and links fail without chmod or redirected creation |
| CC-WEB-001 | web | ClaimCore.Web.Web host private-file security.[CC-WEB-001] state lock rejects linked or broad existing files without rewriting them |
| CC-WEB-001 | web | ClaimCore.Web.Web process entry.[CC-WEB-001] startup accepts only configuration-free introspection verbs |
| CC-WEB-001 | web | ClaimCore.Web.Web security boundaries.[CC-WEB-001] OIDC ticket enforces idle and absolute expiry |
| CC-WEB-001 | web | ClaimCore.Web.Web security boundaries.[CC-WEB-001] emits privacy headers and no-store |
| CC-WEB-001 | web | ClaimCore.Web.Web security boundaries.[CC-WEB-001] limits requests to exact loopback browser admission |
| CC-WEB-001 | web | ClaimCore.Web.Web security boundaries.[CC-WEB-001] private host lease creates no bootstrap credential |
| CC-WEB-001 | web | ClaimCore.Web.Web security boundaries.[CC-WEB-001] renewal cannot extend elapsed absolute session life |
| CC-WEB-001 | web | ClaimCore.Web.Web security boundaries.[CC-WEB-001] ticket expiry bounds elapsed life and forward wall jumps refuse |
| CC-WEB-001 | web | ClaimCore.Web.Web security boundaries.[CC-WEB-001] wall rollback cannot extend idle session life |
| CC-WEB-001 | web | ClaimCore.Web.Web session refusals through TestServer.[CC-WEB-001] logout rejects absent session and malformed or unverified bodies |
| CC-WEB-001 | web | ClaimCore.Web.Web session refusals through TestServer.[CC-WEB-001] mixed bearer and browser credentials are refused |
| CC-WEB-001 | web | ClaimCore.Web.Web session refusals through TestServer.[CC-WEB-001] retired bootstrap route refuses all request bodies |
| CC-WEB-001 | web | ClaimCore.Web.Web synthetic issuer transport trust.[CC-WEB-001] synthetic issuer TLS refuses absent wrong-host and unrelated certificates |
| CC-WEB-001 | web | ClaimCore.Web.Web typed core routes.[CC-WEB-001] delegates case lookup to the endpoint-specific typed core method |
| CC-WEB-001 | web | ClaimCore.Web.Web typed core routes.[CC-WEB-001] delegates recovery resolution only through IClaimsCore.Recovery |
| CC-WEB-001 | web | ClaimCore.Web.Web typed core routes.[CC-WEB-001] keeps management refusal non-disclosing and uncertain event identity exact |
| CC-WEB-001 | web | ClaimCore.Web.Web typed core routes.[CC-WEB-001] maps every admission refusal and bounded body before dispatch |
| CC-WEB-001 | web | ClaimCore.Web.Web typed core routes.[CC-WEB-001] refuses raw retention without its exact source digest header |
| CC-WEB-001 | web | ClaimCore.Web.Web typed core routes.[CC-WEB-001] rejects malformed transport before core invocation |
| CC-WEB-001 | web | ClaimCore.Web.Web v3 admission.[CC-WEB-001] admits only the exact local JSON shape |
| CC-WEB-001 | web | ClaimCore.Web.Web v3 admission.[CC-WEB-001] distinguishes media and exact declared body-size boundaries |
| CC-WEB-001 | web | ClaimCore.Web.Web v3 admission.[CC-WEB-001] gives raw recovery artifacts independent exact media bounds |
| CC-WEB-001 | web | ClaimCore.Web.Web v3 admission.[CC-WEB-001] keeps the mutable browser session registry server-side |
| CC-WEB-001 | web | ClaimCore.Web.Web v3 admission.[CC-WEB-001] rejects absent and remote peers or wrong host authority |
| CC-WEB-001 | web | ClaimCore.Web.Web v3 admission.[CC-WEB-001] requires one exact browser origin and same-origin metadata |
| CC-WEB-001 | web | ClaimCore.Web.Web v3 admission.[CC-WEB-001] returns typed CSRF admission refusal |
| CC-WEB-001 | web | ClaimCore.Web.[CC-WEB-001] controlled stop cancels blocked OIDC startup |
| CC-WEB-001 | web | ClaimCore.Web.asynchronous HTTP transport boundaries.[CC-WEB-001] suspended I/O failure retains safe refusal |
| CC-WEB-001 | web | ClaimCore.Web.asynchronous HTTP transport boundaries.[CC-WEB-001] suspended Kestrel size refusal retains transport classification |
| CC-WEB-001 | web | ClaimCore.Web.asynchronous HTTP transport boundaries.[CC-WEB-001] suspended cancellation retains safe refusal |
| CC-WEB-001 | web | ClaimCore.Web.asynchronous HTTP transport boundaries.[CC-WEB-001] suspended dispatch phase 0 preserves failure knowledge |
| CC-WEB-001 | web | ClaimCore.Web.asynchronous HTTP transport boundaries.[CC-WEB-001] suspended dispatch phase 1 preserves failure knowledge |
| CC-WEB-001 | web | ClaimCore.Web.asynchronous HTTP transport boundaries.[CC-WEB-001] suspended dispatch phase 2 preserves failure knowledge |
| CC-WEB-001 | web | ClaimCore.Web.asynchronous HTTP transport boundaries.[CC-WEB-001] suspended exact-size request is accepted |
| CC-WEB-001 | web | ClaimCore.Web.asynchronous HTTP transport boundaries.[CC-WEB-001] suspended oversized request is refused |
| CC-WEB-001 | web | ClaimCore.Web.configuration.Web configuration boundaries.[CC-WEB-001] enforces session lifetime ordering |
| CC-WEB-001 | web | ClaimCore.Web.configuration.Web configuration boundaries.[CC-WEB-001] loads defaults and rejects every out-of-range admission limit |
| CC-WEB-001 | web | ClaimCore.Web.configuration.Web configuration boundaries.[CC-WEB-001] rejects linked credentials and certificates without private keys |
| CC-WEB-001 | web | ClaimCore.Web.configuration.[CC-WEB-001] accepts one explicit HTTPS authority and rejects ambiguous origins |
| CC-WEB-001 | web | ClaimCore.Web.configuration.[CC-WEB-001] witness key and connection paths fail closed |
| CC-WEB-001 | web | ClaimCore.Web.configuration.listener and public authority.[CC-WEB-001] explicit container binding retains Host and origin refusal |
| CC-WEB-001 | web | ClaimCore.Web.configuration.listener and public authority.[CC-WEB-001] listener rejects invalid address and port |
| CC-WEB-001 | web | ClaimCore.Web.configuration.startup resource lifetimes.[CC-WEB-001] failed OIDC verification closes both startup certificates |
| CC-WEB-001 | web | ClaimCore.Web.copy adoption approval input.[CC-WEB-001] adoption approval refuses invented provenance and actor fields |
| CC-WEB-001 | web | ClaimCore.Web.copy adoption approval input.[CC-WEB-001] adoption origin binds exact witnessed provenance |
| CC-WEB-001 | web | ClaimCore.Web.copy deletion approval input.[CC-WEB-001] deletion approval binds exact report verifier and cutoff |
| CC-WEB-001 | web | ClaimCore.Web.copy deletion approval input.[CC-WEB-001] deletion approval refuses forged or malformed metadata |
| CC-WEB-001 | web | ClaimCore.Web.exact principal admission.[CC-WEB-001] claim names select identity with ordinal case sensitivity |
| CC-WEB-001 | web | ClaimCore.Web.exact principal admission.[CC-WEB-001] composite and unauthenticated principals cannot select an actor |
| CC-WEB-001 | web | ClaimCore.Web.exact principal admission.[CC-WEB-001] discovery refuses nonobjects and duplicate members |
| CC-WEB-001 | web | ClaimCore.Web.exact principal admission.[CC-WEB-001] repeated subject and client claims are refused in every order |
| CC-WEB-001 | web | ClaimCore.Web.exact principal admission.[CC-WEB-001] root and slash-ended issuer metadata preserve exact identity |
| CC-WEB-001 | web | ClaimCore.Web.lifecycle input.[CC-WEB-001] lifecycle drafts and approvals retain exact caller identities |
| CC-WEB-001 | web | ClaimCore.Web.lifecycle input.[CC-WEB-001] lifecycle input refuses forged or malformed fields |
| CC-WEB-001 | web | ClaimCore.Web.management input.[CC-WEB-001] binds exact authority event, principal, role and scope before dispatch |
| CC-WEB-001 | web | ClaimCore.Web.queued HTTP capacity.[CC-WEB-001] bounded admission refuses overload and cancels queued reads before dispatch |
| CC-WEB-001 | web | ClaimCore.Web.real-data activation input.[CC-WEB-001] activation requests refuse forged actor or key metadata |
| CC-WEB-001 | web | ClaimCore.Web.real-data activation input.[CC-WEB-001] activation review and approval bind exact published plan fields |
| CC-WEB-001 | web | ClaimCore.Web.real-data activation wire.[CC-WEB-001] owner plan review projects exact canonical bytes and typed nonclaimant facts |
| CC-WEB-001 | web | ClaimCore.Web.signer approval input.[CC-WEB-001] signer approval binds exact key and expiry |
| CC-WEB-001 | web | ClaimCore.Web.signer approval input.[CC-WEB-001] signer approval refuses forged wire fields |
| CC-WEB-001 | web | ClaimCore.Web.terminal steward approval input.[CC-WEB-001] terminal approval rejects missing proof and forged fields |
| CC-WEB-001 | web | ClaimCore.Web.terminal steward approval input.[CC-WEB-001] terminal proposal tags bind exact owner evidence draft |
| CC-WEB-001 | web | ClaimCore.Web.tombstone input.[CC-WEB-001] opaque review and prune approval retain exact evidence |
| CC-WEB-001 | web | ClaimCore.Web.tombstone input.[CC-WEB-001] tombstone holds accept only closed nonpayload codes |
| CC-WEB-001 | web | ClaimCore.Web.tombstone input.[CC-WEB-001] tombstone inputs reject private and malformed fields |
| CC-WEB-001 | web | ClaimCore.Web.writer handoff approval input.[CC-WEB-001] handoff approval binds exact fence candidate |
| CC-WEB-001 | web | ClaimCore.Web.writer handoff approval input.[CC-WEB-001] handoff approval refuses forged or malformed metadata |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.Storage boundary qualification.catalog change token behavior.catalog change token.[CC-WIT-001] structural and privilege changes move the witness catalog token |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.[CC-REC-001] [CC-WIT-001] revocation fences future authority without rewriting an orphan acceptance |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.owner-private witness key custody.[CC-WIT-001] owner-private key-ring file admits and broad mode refuses |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.runtime I/O and diagnostics.[CC-WIT-001] blocked witness admission cancels before dispatch |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.runtime I/O and diagnostics.[CC-WIT-001] late cancellation preserves exact settlement readback |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.runtime I/O and diagnostics.[CC-WIT-001] throwing observer preserves uncertain settlement and exact intent |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.technical witness phases.[CC-WIT-001] PREPARE commit without settlement stays unknown then reconciles |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.technical witness phases.[CC-WIT-001] START commit without settlement returns same attempt once |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.technical witness phases.[CC-WIT-001] forked primary PREPARE ticket refuses exact replay |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.technical witness phases.[CC-WIT-001] orphan PREPARE intent never invents a primary preparation |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.technical witness phases.[CC-WIT-001] orphan START intent never invents an attempt |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.witness auditor.[CC-WIT-001] audit-only credential reads evidence but cannot append or admit case work |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.witnessed accepted observation.[CC-WIT-001] accepted replay requires exact independent settlement |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.witnessed accepted observation.[CC-WIT-001] attempt admission cannot bypass accepted settlement |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.witnessed accepted observation.[CC-WIT-001] read-only case and receipt disclosures require settled evidence |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.witnessed accepted observation.[CC-WIT-001] retention cannot bypass accepted settlement |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.witnessed primary commit.[CC-WIT-001] orphan intent stays unknown without primary effect |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.witnessed primary commit.[CC-WIT-001] postcommit witness outage stays unknown then exact retry settles |
| CC-WIT-001 | integration | ClaimCore PostgreSQL integration.witnessed primary commit.[CC-WIT-001] wrong witness key refuses runtime opening before case work |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] altered function or ACL closes admission |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] authority settlement binds one exact intent |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] catalog fingerprint |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] encrypted envelopes bind identity and reject tampering |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] function definition fingerprint |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] gapless concurrent append and exact retry |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] missing encrypted evidence fails readback |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] owner rotation advances journal and fences old key |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] paged scan verifies contiguous hash chain |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] rollback does not consume sequence |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] rotating active key retains older decryptability |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] runtime role cannot mutate or administer |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] scope and unique-index changes close admission |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] settlement requires exact unsettled intent |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] stale epoch and altered tip close admission |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] subject scan rejects intervening tamper and scope weakening |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] subject scan retains orphan intents after payload pruning |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] unsafe durability fails before append |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] weaker same-name constraint closes admission |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] writer cannot invoke owner-only witness functions |
| CC-WIT-001 | witness-qualification | ClaimCore witness PostgreSQL.[CC-WIT-001] writer capability and pending handoff fence every append |
| CC-WIT-001 | witness-qualification | [CC-WIT-001] witness rotation rejects reused material and ambiguous key files |
<!-- generated:end contract-tests -->
