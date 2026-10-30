module ClaimCore.Tests.ProjectionIndexChecks

open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.Tests.Fixtures

let private unitResult result = Result.map (fun _ -> ()) result

let checks installation instant =
    let snapshot = Case.snapshot (decided ())

    let caseId =
        CaseReferenceId.encode installation (CaseReference.format snapshot.Reference)

    let index = CaseIndex.create instant snapshot

    let record: StoredCase =
        {
            CaseId = caseId
            Snapshot = snapshot
            SnapshotBytes = CaseRecord.encodeSnapshot snapshot
            Index = index
        }

    let caseHistory: CaseHistory =
        {
            Revision = snapshot.Revision
            ChangedAt = instant
            Snapshot = Some snapshot
            Reference = Some snapshot.Reference
            State = CaseState.map ignore snapshot.State
            MissingPayloadAt = None
            PresentPayloadAt = Some(CaseFixtures.sequence 1L)
            ErasedAt = None
        }

    let histories = Map.ofList [ caseId, caseHistory ]

    let check mutate () =
        Expect.isOk
            (ProjectionVerification.caseProjection installation histories Set.empty record)
            "Valid independent projection control"

        ProjectionVerification.caseProjection
            installation
            histories
            Set.empty
            { record with Index = mutate index }
        |> unitResult

    [
        VerificationFinding.CaseStatusProjection,
        check (fun index ->
            { index with
                Status = Some CaseStatus.Closed
            })
        VerificationFinding.CaseProgressProjection,
        check (fun index ->
            { index with
                Progress = Some CaseProgress.Paid
            })
        VerificationFinding.CaseChangedAtProjection,
        check (fun index ->
            { index with
                ChangedAt = instant.AddSeconds 1.
            })
        VerificationFinding.CaseSearchProjection,
        check (fun index ->
            { index with
                SearchText = Some "tampered"
            })
        VerificationFinding.CaseNotifiedOnProjection,
        check (fun index ->
            { index with
                NotifiedOn = Some(Inputs.date "2026-08-04")
            })
        VerificationFinding.CaseClaimedAmountProjection,
        check (fun index ->
            { index with
                Claimed = Some(Inputs.money "1" "EUR")
            })
        VerificationFinding.CaseClaimedCurrencyProjection,
        check (fun index ->
            { index with
                Claimed = Some(Inputs.money "1000" "USD")
            })
        VerificationFinding.CasePayableAmountProjection,
        check (fun index ->
            { index with
                Payable = Some(Inputs.money "1" "EUR")
            })
        VerificationFinding.CasePayableCurrencyProjection,
        check (fun index ->
            { index with
                Payable = Some(Inputs.money "750" "USD")
            })
    ]
