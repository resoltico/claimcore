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
                "triage assignment excludes forbidden roles for installation and scoped visibility"
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
