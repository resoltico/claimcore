module internal ClaimCore.IntegrationTests.AcceptedHistoryFixtures

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.IntegrationTests.VerificationSupport

let private snapshot reference claimant : CaseSnapshot =
    {
        Reference = reference
        Revision = 1L
        View =
            CaseView.Live(
                {
                    CaseReference = reference
                    IncidentDate = registration.IncidentDate
                    IncidentNotificationDate = registration.IncidentNotificationDate
                    IncidentCountry = "LV"
                    ClaimantName = claimant
                    InsurerName = registration.InsurerName
                    ClaimedAmount = "1000"
                    ClaimedCurrency = "EUR"
                    PaymentDecisionDate = None
                    PayableAmount = None
                    PayableCurrency = None
                    PaymentDate = None
                    Status = CaseStatus.Opened
                },
                Disposition.Active
            )
    }

/// Represents an acceptance under an earlier text policy. It still appends the whole canonical
/// ledger entry and projection atomically; current admission is deliberately not the fixture oracle.
let seed (database: PostgresFixture.Database) claimant =
    let fixture = database.Fixture

    let request =
        request
            10
            "HISTORICAL-1"
            0L
            (BusinessCommand.Open
                { registration with
                    ClaimantName = claimant
                })

    let snapshot = snapshot request.CaseReference claimant

    Case.ofAccepted snapshot
    |> Result.defaultWith (fun _ -> failtest "Historical fixture must obey permanent format")
    |> ignore

    let decide (view: CaseLockedView) =
        Expect.isNone view.Case "Fixture reference is fresh"
        Expect.isNone view.ExistingOperation "Fixture operation is fresh"
        let id = CaseReferenceId.encode fixture.InstallationId snapshot.Reference

        let actor =
            view.Actor
            |> Option.defaultWith (fun () -> failtest "Fixture owner is provisioned")

        let scope =
            StoredScope.Case(id, 1L, CaseActKind.Open, fixture.Calendar.DateAt view.ObservedAt)

        let entry =
            LedgerEntries.create
                fixture.InstallationId
                view.Head
                view.ObservedAt
                view.PayloadNonce
                actor.ActorId
                request.OperationId
                scope
                (RequestRecord.encode request)
                (ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot)

        let current: StoredCase = { CaseId = id; Snapshot = snapshot }

        let receipt =
            LedgerEntries.validate fixture.InstallationId (LedgerEntries.stored entry)
            |> Result.bind (LedgerEntries.receipt fixture.Owner view.Actor (Some current))
            |> Result.defaultWith (fun _ ->
                failtest "Historical fixture receipt is structurally valid")

        LedgerDecision.Append(entry, CaseProjection.Insert(id, snapshot), receipt)

    task {
        let! result =
            fixture.Store.WriteCase(
                fixture.Owner,
                request.OperationId,
                request.CaseReference,
                decide,
                token
            )

        match result with
        | Ok(LedgerWriteResult.Appended _) -> ()
        | _ -> failtest "Historical fixture append failed"
    }

let grandfathering (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let name = String.replicate 201 "A"
        do! seed database name
        let! current = actor.Get("historical-1", token)

        match current with
        | QueryOutcome.Succeeded {
                                     Snapshot = { View = CaseView.Live(fields, _) }
                                 } ->
            Expect.equal fields.ClaimantName name "Retained record renders without admission"
        | _ -> failtest "Historical record could not be read"

        let values =
            { registration with
                ClaimantName = name
                InsurerName = "Corrected Insurer"
            }

        let! reaffirm =
            actor.Execute(
                request 11 "HISTORICAL-1" 1L (BusinessCommand.AmendRegistration values),
                token
            )

        accepted reaffirm |> ignore

        let! changed =
            actor.Execute(
                request
                    12
                    "HISTORICAL-1"
                    2L
                    (BusinessCommand.AmendRegistration
                        { values with
                            ClaimantName = name + "B"
                        }),
                token
            )

        Expect.equal
            changed
            (ExecuteOutcome.Rejected(
                Rejection.Case(
                    CaseRefusal.InvalidInput(
                        InputTarget.ClaimantName,
                        InputViolation.Text(TextViolation.TooLong 200)
                    )
                )
            ))
            "Changed assertion follows current policy"

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testCaseAsync
        "historical format-valid text reads reaffirms and verifies without current admission while changed text refuses"
        (run grandfathering)
