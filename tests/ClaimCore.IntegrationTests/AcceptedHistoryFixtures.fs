module ClaimCore.IntegrationTests.AcceptedHistoryFixtures

open ClaimCore.TestSupport

open ClaimCore.Contract

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private snapshot (reference: string) claimant : CaseSnapshot =
    let reference = CaseFixtures.reference reference

    {
        Reference = reference
        Revision = 1L
        State =
            CaseState.Active(
                {
                    CaseReference = reference
                    Status = CaseStatus.Opened
                    Registration =
                        CaseFixtures.registration
                            {
                                IncidentDate = registration.IncidentDate
                                IncidentNotificationDate = registration.IncidentNotificationDate
                                IncidentCountry = "LV"
                                ClaimantName = claimant
                                InsurerName = registration.InsurerName
                                ClaimedAmount = "1000"
                                ClaimedCurrency = "EUR"
                            }
                    Decision = None
                    Payment = None
                }
            )
    }

/// Represents an acceptance under an earlier text policy. It still appends the whole canonical
/// ledger entry and projection atomically; current admission is deliberately not the fixture oracle.
let internal seed (database: PostgresFixture.Database) claimant =
    let fixture = database.Fixture

    let request =
        request
            10
            "HISTORICAL-1"
            0L
            (UnvalidatedCaseAct.Open
                { registration with
                    ClaimantName = claimant
                })

    let snapshot = snapshot request.CaseReference claimant

    Case.ofAccepted snapshot
    |> Result.defaultWith (fun _ -> failtest "Historical fixture must obey permanent format")
    |> ignore

    let decide (tx: IWriteTx) (view: CaseFacts) =
        Expect.isNone view.Case "Fixture reference is fresh"
        Expect.isNone view.ExistingOperation "Fixture operation is fresh"

        let id =
            CaseReferenceId.encode fixture.InstallationId (CaseReference.format snapshot.Reference)

        let actor =
            view.Actor.Actor
            |> Option.defaultWith (fun () -> failtest "Fixture owner is provisioned")

        let scope =
            EntryTarget.Case(
                id,
                1L,
                (CaseActKind.Business BusinessActKind.Open),
                fixture.Calendar.DateAt view.ObservedAt
            )

        let entry =
            LedgerEntries.create
                fixture.InstallationId
                tx.Head
                view.ObservedAt
                tx.Nonce
                actor.ActorId
                request.OperationId
                scope
                (CaseFixtures.encode request)
                (ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot)

        let current: StoredCase = { CaseId = id; Snapshot = snapshot }

        let receipt =
            LedgerEntries.validate fixture.InstallationId (LedgerEntries.stored entry)
            |> Result.bind (LedgerEntries.receipt fixture.Owner view.Actor (Some current))
            |> Result.defaultWith (fun _ ->
                failtest "Historical fixture receipt is structurally valid")

        Verdict.Proceed(entry, CaseProjection.Insert(id, snapshot), receipt)

    task {
        let! result =
            fixture.Store.Write(
                (fun tx ->
                    task {
                        let! view =
                            ExecutionFacts.case
                                tx
                                fixture.InstallationId
                                fixture.Owner
                                (Some request.OperationId)
                                request.CaseReference

                        return! decide tx view |> ExecutionFacts.persist tx tx.ProjectCase
                    }),
                token
            )

        match result with
        | Ok(Verdict.Proceed _) -> ()
        | _ -> failtest "Historical fixture append failed"
    }

let private grandfathering (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let name = String.replicate 201 "A"
        do! seed database name

        let! current = (Core.get actor ({ CaseReference = "historical-1" }) (token))

        match current with
        | ReadReply.Succeeded {
                                  Snapshot = {
                                                 State = (CaseState.Active(fields) | CaseState.Voided(fields))
                                             }
                              } ->
            Expect.equal
                (PartyName.format fields.Registration.ClaimantName)
                name
                "Retained record renders without admission"
        | _ -> failtest "Historical record could not be read"

        let values =
            { registration with
                ClaimantName = name
                InsurerName = "Corrected Insurer"
            }

        let! reaffirm =
            (Core.execute
                actor
                (OperationInput.Case(
                    request 11 "HISTORICAL-1" 1L (UnvalidatedCaseAct.AmendRegistration values)
                ))
                (token))

        accepted reaffirm |> ignore

        let! changed =
            (Core.execute
                actor
                (OperationInput.Case(
                    request
                        12
                        "HISTORICAL-1"
                        2L
                        (UnvalidatedCaseAct.AmendRegistration
                            { values with
                                ClaimantName = name + "B"
                            })
                ))
                (token))

        Expect.equal
            changed.Outcome
            (ExecuteResult.Rejected(
                (Refusal.InvalidInput(
                    (InputTarget.Field BusinessField.ClaimantName),
                    (Violation.TooLong 200)
                ))
            ))
            "Changed assertion follows current policy"

        let! _ = verified database
        return ()
    }

let private appendTransitions (actor: HandlerContext) =
    task {
        for revision in 1L .. 59L do
            let act =
                if revision % 2L = 1L then
                    UnvalidatedCaseAct.Close
                else
                    UnvalidatedCaseAct.Reopen

            let! result =
                (Core.execute
                    actor
                    (OperationInput.Case(request (100 + int revision) "HISTORICAL-1" revision act))
                    (token))

            accepted result |> ignore

    }

let private assertRetained name entries =
    for entry in entries do
        match entry with
        | HistoryEntry.FullEntry {
                                     Subject = ReceiptSubject.Case {
                                                                       Snapshot = SnapshotDisclosure.Disclosed((LiveCase.Active fields | LiveCase.Voided fields))
                                                                   }
                                 } ->
            Expect.equal
                (PartyName.format fields.Registration.ClaimantName)
                name
                "Accepted history is never re-admitted or truncated"
        | _ -> failtest "Expected disclosed retained fields"


let private historyPage =
    function
    | ReadReply.Succeeded page -> page
    | _ -> failtest "Expected accepted history page"

let private remainingHistory (actor: HandlerContext) (query: HistoryRequest) =
    task {
        let mutable cursor = query.AfterCursor
        let mutable entries = []

        while cursor.IsSome do
            let! current = (Core.history actor ({ query with AfterCursor = cursor }) (token))

            let current = historyPage current

            Expect.equal
                current.Entries.Length
                10
                "The final FULL page includes the remaining ten revisions"

            entries <- entries @ current.Entries
            cursor <- current.NextCursor

        return entries
    }

let private withheldReceipt (receipt: OperationReceipt) =
    let scope = caseReceipt receipt

    { receipt with
        Subject =
            ReceiptSubject.Case
                { scope with
                    Snapshot = SnapshotDisclosure.Withheld
                }
    }

let private assertRemainingHistoryAccess
    (actor: HandlerContext)
    (query: HistoryRequest)
    (original: OperationReceipt)
    =
    task {
        let! summary =
            (Core.history
                actor
                ({ query with
                    Detail = HistoryDetail.Summary
                })
                (token))

        let summary = historyPage summary
        Expect.equal summary.Entries.Length 50 "Retained payment-recorder rights permit SUMMARY"

        let! observed = (Core.observe actor ({ OperationId = original.OperationId }) (token))

        Expect.equal
            observed
            (ReadReply.Succeeded(withheldReceipt original))
            "Superseded OPEN keeps every acceptance metadata field and withholds its snapshot"
    }

let private revokedFullHistory
    (database: PostgresFixture.Database)
    (actor: HandlerContext)
    (query: HistoryRequest)
    (first: HistoryResultPage)
    =
    task {
        let original =
            match List.head first.Entries with
            | HistoryEntry.FullEntry receipt -> receipt
            | _ -> failtest "Original FULL receipt"

        Expect.equal
            original.OperationId
            (ClaimCore.TestSupport.StoreFixtures.id 10)
            "Original OPEN acceptance"

        Expect.isSome first.NextCursor "Retain the genuine first FULL continuation"

        let! removed =
            grant
                actor
                (ClaimCore.TestSupport.StoreFixtures.id 200)
                database.Fixture.Owner
                Role.CaseHandler
                GrantTarget.Installation
                false

        acceptedAuthority removed

        for request in
            [
                { query with
                    AfterCursor = first.NextCursor
                }
                query
            ] do
            let! refused = (Core.history actor (request) (token))

            Expect.equal
                refused
                (ReadReply.Rejected Refusal.ResourceUnavailable)
                "FULL requires current rights before accepting or comparing a cursor"

        do! assertRemainingHistoryAccess actor query original
    }

let private retainedPages (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let name = String.replicate 1024 "<"
        do! seed database name

        do! appendTransitions actor

        let query =
            {
                CaseReference = "HISTORICAL-1"
                AfterCursor = None
                Limit = 50
                Detail = HistoryDetail.Full
            }

        let! first = (Core.history actor (query) (token))

        let first = historyPage first

        Expect.equal first.Entries.Length 50 "FULL shares the public page capacity"

        let! remaining =
            remainingHistory
                actor
                { query with
                    AfterCursor = first.NextCursor
                }

        let entries = first.Entries @ remaining

        let revisions =
            entries
            |> List.map (function
                | HistoryEntry.FullEntry { Subject = ReceiptSubject.Case c } -> c.Revision
                | _ -> failtest "Full case receipt")

        Expect.equal
            revisions
            [ 1L .. 60L ]
            "Every retained revision traverses without omissions or duplicates"

        assertRetained name entries

        let! summary =
            (Core.history
                actor
                ({ query with
                    Detail = HistoryDetail.Summary
                })
                (token))

        match summary with
        | ReadReply.Succeeded p -> Expect.equal p.Entries.Length 50 "SUMMARY retains capacity fifty"
        | _ -> failtest "Retained summary page"

        do! revokedFullHistory database actor query first

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testList
        "accepted historical pages"
        [
            testCaseAsync
                "retained text FULL pages are bounded and traverse all revisions"
                (run retainedPages)
            testCaseAsync
                "historical format-valid text reads reaffirms and verifies without current admission while changed text refuses"
                (run grandfathering)

        ]
