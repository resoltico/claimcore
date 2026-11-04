module ClaimCore.TestSupport.StoreContractTests

open ClaimCore.TestSupport

open ClaimCore.Protocol
open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport.StoreContractReads

let internal tests factory =
    testList
        "ledger store contract"
        [
            testCaseAsync
                "admission preserves typed effects names authority scopes and erased replay"
                (withFixture factory AdmitContract.lifecycle)
            testCaseAsync
                "admission rejects normalized duplicates and preserves role admission"
                (withFixture factory AdmitContract.boundary)
            testCaseAsync
                "admission accepts exactly48 distinct grants with50 independent effects"
                (withFixture factory AdmitContract.maximum)
            testCaseAsync
                "profile groups and erasure projections roll back atomically"
                (withFixture factory ActorProfilesContract.atomicWrites)
            testCaseAsync
                "profile erasure preserves identity grants accepted existence and later re-profile"
                (withFixture factory ActorProfilesContract.lifecycle)
            testCaseAsync
                "registration separates identity and authored profile with independent salts"
                (withFixture factory ActorProfilesContract.registrationIsolation)
            testCaseAsync
                "state refusals report current state after capability and disclosure gates"
                (withFixture factory ReminderConcurrencyContract.notOffered)
            testCaseAsync
                "different and same reminder revisions retain financial preview isolation"
                (withFixture factory ReminderConcurrencyContract.independentRevisions)
            testCaseAsync
                "close and compound races have complete valid effects"
                (withFixture factory ReminderConcurrencyContract.closeAndCompound)
            testCaseAsync
                "erase and reminder races leave no readable child or payload"
                (withFixture factory ReminderConcurrencyContract.erasureRaces)
            testCaseAsync
                "revocation after reminder preview is enforced in the action snapshot"
                (withFixture factory ReminderConcurrencyContract.revocationAndCompound)
            testCaseAsync
                "case pages pair independent work and apply parent limits before reminder fan-out"
                (withFixture factory CaseWorkContract.pairedPage)
            testCaseAsync
                "case work assignment is scoped and rechecked without granting visibility"
                (withFixture factory CaseWorkContract.assignmentAndDisclosure)
            testCaseAsync
                "case work due filters summaries and keysets use the snapshot business date"
                (withFixture factory CaseWorkContract.dueFiltersAndOrder)
            testCaseAsync
                "current reminder timing follows observation date state and parent lifecycle without writes"
                (withFixture factory CaseWorkContract.reminderTiming)
            testCaseAsync
                "case work lifecycle and erasure remove every assignment relationship"
                (withFixture factory CaseWorkContract.lifecycleAndErasure)
            testCaseAsync
                "Gregorian reminder endpoints and keysets preserve finite date extremes"
                (withFixture factory CaseWorkContract.finiteDateRange)
            testCaseAsync
                "targeted partial updates preserve facts and require authorized payment reaffirmation"
                (withFixture factory CaseWorkContract.partialUpdatesAndReaffirmation)
            testCaseAsync
                "ID-scoped grants retain their case identity through review replay revocation and erasure"
                (withFixture factory CaseSelectorContract.grantScopes)
            testCase
                "case selectors have a closed unambiguous grammar"
                CaseSelectorContract.closedSelectorGrammar
            testCaseAsync
                "ID and reference selectors preserve retained identity and live history"
                (withFixture factory CaseSelectorContract.identities)
            testCaseAsync
                "triage summary counts and exact sums share the selected scope"
                (withFixture factory WorklistTriageContract.exactTotals)
            testCaseAsync
                "triage notification order pages null dates last and binds assignment"
                (withFixture factory WorklistTriageContract.oldestPaging)
            testCaseAsync
                "triage visibility excludes forbidden roles independently of responsibility"
                (withFixture factory WorklistTriageContract.assignmentExcludesForbiddenRoles)
            testCaseAsync
                "authority history preserves typed facts and filtered newest-first pages"
                (withFixture factory AuthorityHistoryContract.factsAndPaging)
            testCaseAsync
                "authority history requires installation read authority on every snapshot"
                (withFixture factory AuthorityHistoryContract.permissions)
            testCaseAsync
                "authority history rejects tampered payloads and malformed store pages"
                (withFixture factory AuthorityHistoryContract.integrity)
            testCaseAsync
                "passive actor-scoped recovery preserves roleless authorship without writes or implicit promises"
                (withFixture factory OperationRecoveryContract.passiveStatusAndRolelessAuthorship)
            testCaseAsync
                "accepted and own fenced identities fail integrity before status settlement or replay"
                (withFixture factory OperationRecoveryContract.acceptanceAndFenceContradiction)
            testCaseAsync
                "fabricated fence projections cannot prove permanent nonacceptance"
                (withFixture factory OperationRecoveryContract.fabricatedFenceIsNotProof)
            testCaseAsync
                "meaningless scope refuses without append while meaningful dormant overlapping grants succeed"
                (withFixture factory AuthorityReviewContract.meaningfulScopeAdmission)
            testCaseAsync
                "authority grant review shows only touched facts and writes nothing"
                (withFixture factory AuthorityReviewContract.authorityGrantReview)
            testCaseAsync
                "authority execution rechecks a stale owner preview"
                (withFixture factory AuthorityReviewContract.authorityReviewRechecks)
            testCaseAsync
                "authority previews preserve last-owner and registered-principal refusals"
                (withFixture factory AuthorityReviewContract.authorityReviewRefusals)
            testCaseAsync
                "authority read capability cannot authorize mutation preview"
                (withFixture factory AuthorityReviewContract.authorityReadIsNotManage)
            testCaseAsync
                "authority preview writes nothing and exact replay precedes fresh-state refusal"
                (withFixture factory AuthorityReviewContract.authorityReviewReplay)
            testCaseAsync
                "authority loading stays bounded to the requested and accepted scopes"
                (withFixture factory ScopedAuthorityContract.scopedAuthority)
            testCaseAsync
                "scoped advice and preview never authorize execution after revocation"
                (withFixture factory ScopedAuthorityContract.revokedAdvice)
            testCaseAsync
                "genesis append loads one view and co-commits authority"
                (withFixture factory LedgerWriteContract.genesis)
            testCaseAsync
                "append replay and refusal preserve head and complete case projection"
                (withFixture factory LedgerWriteContract.replayAndRefusal)
            testCaseAsync
                "successive writes load the current head case and existing identity once"
                (withFixture factory LedgerWriteContract.lockedView)
            testCaseAsync
                "update advances revision and reads back complete fields"
                (withFixture factory LedgerWriteContract.caseUpdate)
            testCaseAsync
                "erasure clears every case payload and preserves other cases"
                (withFixture factory CaseDisclosureContract.caseErasure)
            testCaseAsync
                "visibility ordering and keyset limits are applied before paging"
                (withFixture factory CaseDisclosureContract.caseVisibility)
            testCaseAsync
                "ASCII reference folding and keyset continuation agree"
                (withFixture factory CaseDisclosureContract.foldedOrdering)
            testCaseAsync
                "authority changes and reads agree within snapshots"
                (withFixture factory ScopedAuthorityContract.authorityChanges)
        ]
