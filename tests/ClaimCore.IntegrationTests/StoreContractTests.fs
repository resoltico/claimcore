module ClaimCore.TestSupport.StoreContractTests

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation
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
                "payment entries persist exact totals selection removal and erasure"
                (withFixture factory PaymentEntriesContract.lifecycle)
            testCaseAsync
                "input refusal ordering retains readable private and absent target policy"
                (withFixture factory RequesterAdmissionContract.targetPrivacy)
            testCaseAsync
                "unavailable callers cannot receive early business input refusals"
                (withFixture factory RequesterAdmissionContract.lifecycle)
            testCaseAsync
                "summary attribution follows text edits rather than assignment updates"
                (withFixture factory (fun fixture ->
                    task { let! _ = SummaryAttributionContract.lifecycle fixture in return () }))
            testCaseAsync
                "directory text contact and typed role filters share bounded paging"
                (withFixture factory ActorNameSearchContract.contactAndRole)
            testCaseAsync
                "current-name search is normalized literal bounded and cursor-bound"
                (withFixture factory ActorNameSearchContract.lookup)
            testCaseAsync
                "all7 role summaries stay bounded and deterministic"
                (withFixture factory PeopleDirectoryContract.maximumRoles)
            testCaseAsync
                "bounded People summaries preserve disabled review and scoped counts"
                (withFixture factory PeopleDirectoryContract.summaries)
            testCaseAsync
                "assignment state does not weaken corrupt service scope vetoes"
                (withFixture factory PeopleDirectoryContract.corruptAssignment)
            testCaseAsync
                "admission effect failures restore projections and exact waiting hints"
                (withFixture factory AdmitContract.atomicWrites)
            testCaseAsync
                "admission preserves typed effects names people scopes and erased replay"
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
                "targeted partial updates preserve facts and enforce payment removal people"
                (withFixture factory CaseWorkContract.partialUpdatesAndPaymentPeople)
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
                "people history preserves typed facts and filtered newest-first pages"
                (withFixture factory PeopleHistoryContract.factsAndPaging)
            testCaseAsync
                "people history requires installation read people on every snapshot"
                (withFixture factory PeopleHistoryContract.permissions)
            testCaseAsync
                "people history rejects tampered payloads and malformed store pages"
                (withFixture factory PeopleHistoryContract.integrity)
            testCaseAsync
                "lookup preserves roleless authorship and disclosed foreign receipts without writing before the deadline"
                (withFixture factory OperationRecoveryContract.rolelessAuthorship)
            testCaseAsync
                "meaningless scope refuses without append while meaningful dormant overlapping grants succeed"
                (withFixture factory PeopleReviewContract.meaningfulScopeAdmission)
            testCaseAsync
                "people grant review shows only touched facts and writes nothing"
                (withFixture factory PeopleReviewContract.peopleGrantReview)
            testCaseAsync
                "people execution rechecks a stale owner preview"
                (withFixture factory PeopleReviewContract.peopleReviewRechecks)
            testCaseAsync
                "people previews preserve last-owner and registered-principal refusals"
                (withFixture factory PeopleReviewContract.peopleReviewRefusals)
            testCaseAsync
                "people read capability cannot authorize mutation preview"
                (withFixture factory PeopleReviewContract.peopleReadIsNotManage)
            testCaseAsync
                "people preview writes nothing and exact replay precedes fresh-state refusal"
                (withFixture factory PeopleReviewContract.peopleReviewReplay)
            testCaseAsync
                "people loading stays bounded to the requested and accepted scopes"
                (withFixture factory ScopedPeopleContract.scopedPeople)
            testCaseAsync
                "scoped advice and preview never authorize execution after revocation"
                (withFixture factory ScopedPeopleContract.revokedAdvice)
            testCaseAsync
                "genesis append loads one view and co-commits people"
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
                "people changes and reads agree within snapshots"
                (withFixture factory ScopedPeopleContract.peopleChanges)
        ]
