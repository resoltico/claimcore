module ClaimCore.Tests.ProjectionIndexChecks

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.Tests.Fixtures

let checks _installation instant =
    let snapshot = Case.snapshot (decided ())
    let index = CaseIndex.create instant snapshot None []

    let record =
        {
            StoredCase.CaseId = snapshot.CaseId
            Snapshot = snapshot
            SnapshotBytes = CaseRecord.encodeSnapshot snapshot
            Index = index
        }

    let check finding get mutate =
        finding,
        fun () ->
            Expect.isOk
                (ProjectionVerification.column finding get record record)
                "Positive column control"

            ProjectionVerification.column finding get record { record with Index = mutate index }

    [
        check VerificationFinding.CaseStatusProjection (fun row -> row.Index.Status) (fun index ->
            { index with
                Status = Some CaseStatus.Closed
            })
        check
            VerificationFinding.CaseProgressProjection
            (fun row -> row.Index.Progress)
            (fun index ->
                { index with
                    Progress = Some CaseProgress.Paid
                })
        check
            VerificationFinding.CaseChangedAtProjection
            (fun row -> row.Index.ChangedAt)
            (fun index ->
                { index with
                    ChangedAt = instant.AddSeconds 1.
                })
        check
            VerificationFinding.CaseSearchProjection
            (fun row -> row.Index.SearchText)
            (fun index ->
                { index with
                    SearchText = Some "tampered"
                })
        check
            VerificationFinding.CaseWorkProjection
            (fun row -> row.Index.Responsible, row.Index.FollowUp, row.Index.WorkSearchText)
            (fun index ->
                { index with
                    FollowUp = Some(Inputs.date "2027-12-31")
                })
        check
            VerificationFinding.CaseWorkProjection
            (fun row -> row.Index.WorkSearchText)
            (fun index ->
                { index with
                    WorkSearchText = Some "tampered"
                })
        check
            VerificationFinding.CaseNotifiedOnProjection
            (fun row -> row.Index.NotifiedOn)
            (fun index ->
                { index with
                    NotifiedOn = Some(Inputs.date "2026-08-04")
                })
        check
            VerificationFinding.CaseClaimedAmountProjection
            (fun row -> row.Index.Claimed |> Option.map Money.value)
            (fun index ->
                { index with
                    Claimed = Some(Inputs.money "1" "EUR")
                })
        check
            VerificationFinding.CaseClaimedCurrencyProjection
            (fun row -> row.Index.Claimed |> Option.map Money.currency)
            (fun index ->
                { index with
                    Claimed = Some(Inputs.money "1000" "USD")
                })
        check
            VerificationFinding.CasePayableAmountProjection
            (fun row -> row.Index.Payable |> Option.map Money.value)
            (fun index ->
                { index with
                    Payable = Some(Inputs.money "1" "EUR")
                })
        check
            VerificationFinding.CasePayableCurrencyProjection
            (fun row -> row.Index.Payable |> Option.map Money.currency)
            (fun index ->
                { index with
                    Payable = Some(Inputs.money "750" "USD")
                })
    ]
