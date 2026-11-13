module ClaimCore.Tests.ProjectionIndexChecks

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.Tests.Fixtures

let private unitResult result = Result.map (fun _ -> ()) result

let checks installation instant =
    let snapshot = Case.snapshot (decided ())

    let caseId = snapshot.CaseId

    let index = CaseIndex.create instant snapshot None []

    let record: StoredCase =
        {
            CaseId = caseId
            Snapshot = snapshot
            SnapshotBytes = CaseRecord.encodeSnapshot snapshot
            Index = index
        }

    let caseHistory: CaseHistory =
        {
            PaymentProjections = Map.empty
            Revision = snapshot.Revision
            ChangedAt = instant
            Snapshot = Some snapshot
            Reference = Some snapshot.Reference
            State = CaseState.map ignore snapshot.State
            Status =
                CaseState.fields snapshot.State
                |> Option.map _.Status
                |> Option.defaultValue CaseStatus.Opened
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

    let root =
        {
            CaseWorkSnapshot.CaseId = caseId
            Revision = Revision.zero
            Work = CaseWork.empty
        }

    let work: Map<CaseId, WorkHistory> =
        Map.ofList
            [
                (caseId,
                 {
                     Revision = Revision.zero
                     Snapshot = Some root
                     ChangedAt = None
                     RecordedBy = None
                     SummaryChangedAt = None
                     SummaryRecordedBy = None
                     MissingPayloadAt = None
                     PresentPayloadAt = None
                 })
            ]

    let checkWork mutate () =
        Expect.isOk
            (ProjectionVerification.workIndex work Map.empty record)
            "Valid independent work-index control"

        Expect.isError
            (ProjectionVerification.workIndex
                work
                Map.empty
                { record with
                    Index =
                        { index with
                            WorkSearchText = Some "tampered"
                        }
                })
            "Summary-search corruption remains independently detectable"

        ProjectionVerification.workIndex work Map.empty { record with Index = mutate index }
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
        VerificationFinding.CaseWorkProjection,
        checkWork (fun index ->
            { index with
                FollowUp = Some(Inputs.date "2027-12-31")
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
