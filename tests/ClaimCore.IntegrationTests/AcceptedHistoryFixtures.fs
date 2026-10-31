module ClaimCore.IntegrationTests.AcceptedHistoryFixtures

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private snapshot operationId (reference: string) claimant : CaseSnapshot =
    let reference = CaseFixtures.reference reference

    {
        CaseId = CaseFixtures.caseId operationId
        Reference = reference
        Revision = CaseFixtures.revision 1L
        State =
            CaseState.Active(
                {
                    CaseReference = reference
                    Status = CaseStatus.Opened
                    Registration =
                        CaseFixtures.registration (
                            ClaimCore.TestSupport.Inputs.registrationWith
                                [
                                    BusinessField.ClaimantName, (claimant)
                                    BusinessField.InsurerName,
                                    ((RegistrationInput.value
                                        BusinessField.InsurerName
                                        (registration)))
                                    BusinessField.ClaimedAmount, ("1000")
                                ]
                                ((Inputs.registrationOn
                                    (RegistrationInput.value
                                        BusinessField.IncidentDate
                                        (registration))
                                    (RegistrationInput.value
                                        BusinessField.IncidentNotificationDate
                                        (registration))))
                        )
                    Progress = ClaimCore.TestSupport.CaseFixtures.progress (None) (None)
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
            (AuthoredCaseAct.Open(
                ClaimCore.TestSupport.Inputs.registrationWith
                    [ BusinessField.ClaimantName, (claimant) ]
                    (registration)
            ))

    let snapshot = snapshot request.OperationId request.CaseReference claimant

    Case.ofAccepted snapshot
    |> Result.defaultWith (fun _ -> failtest "Historical fixture must obey permanent format")
    |> ignore

    let decide (tx: IWriteTx) (view: CaseFacts) =
        Expect.isNone view.Case "Fixture reference is fresh"
        Expect.isNone view.ExistingOperation "Fixture operation is fresh"

        let id = snapshot.CaseId

        let actor =
            view.Actor.Actor
            |> Option.defaultWith (fun () -> failtest "Fixture owner is provisioned")

        let scope =
            EntryTarget.Case(
                id,
                (CaseFixtures.revision (1L)),
                CaseActKind.Open,
                fixture.Calendar.DateAt view.ObservedAt
            )

        let entry =
            LedgerEntries.create
                {
                    InstallationId = fixture.InstallationId
                    Head = tx.Head
                    Instant = view.ObservedAt
                    Nonce = tx.Nonce
                    ActorId = actor.ActorId
                    OperationId = (CaseFixtures.operationId (request.OperationId))
                    Subject = scope
                    Request = CaseFixtures.encode request
                    Snapshot = ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot
                }

        let current: StoredCase =
            {
                CaseId = id
                Snapshot = snapshot
                Index = CaseIndex.create entry.RecordedAt snapshot
                SnapshotBytes = ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot
            }

        let receipt =
            LedgerEntries.validate fixture.InstallationId (LedgerEntries.stored entry)
            |> Result.bind (LedgerEntries.receipt fixture.Owner view.Actor (Some current))
            |> Result.defaultWith (fun _ ->
                failtest "Historical fixture receipt is structurally valid")

        Verdict.Proceed(
            entry,
            CaseProjection.Insert(id, snapshot, CaseIndex.create entry.RecordedAt snapshot),
            receipt
        )

    task {
        let! result =
            fixture.Store.Write(
                {
                    WriteOperation.Work =
                        (fun tx ->
                            task {
                                let! view =
                                    ExecutionFacts.case
                                        (core fixture fixture.Owner)
                                        tx
                                        (Some(CaseFixtures.operationId (request.OperationId)))
                                        request.CaseReference

                                return! decide tx view |> ExecutionFacts.persist tx tx.ProjectCase
                            })
                    CancellationToken = token
                }
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

        let! current = (Service.get actor ({ CaseReference = "historical-1" }) (token))

        match current with
        | Outcome.Succeeded {
                                Snapshot = {
                                               State = (CaseState.Active(fields) | CaseState.Voided(fields))
                                           }
                            } ->
            Expect.equal
                (PartyName.format (Registration.claimant (fields.Registration)))
                name
                "Retained record renders without admission"
        | _ -> failtest "Historical record could not be read"

        let values =
            (ClaimCore.TestSupport.Inputs.registrationWith
                [
                    BusinessField.ClaimantName, (name)
                    BusinessField.InsurerName, ("Corrected Insurer")
                ]
                (registration))

        let! reaffirm =
            (Service.execute
                actor
                (OperationInput.Case(
                    request 11 "HISTORICAL-1" 1L (AuthoredCaseAct.AmendRegistration values)
                ))
                (token))

        accepted reaffirm |> ignore

        let! changed =
            (Service.execute
                actor
                (OperationInput.Case(
                    request
                        12
                        "HISTORICAL-1"
                        2L
                        (AuthoredCaseAct.AmendRegistration(
                            ClaimCore.TestSupport.Inputs.registrationWith
                                [ BusinessField.ClaimantName, (name + "B") ]
                                (values)
                        ))
                ))
                (token))

        Expect.equal
            changed.Outcome
            (ExecuteResult.Rejected(
                (Refusal.invalidInput (
                    (InputTarget.Field BusinessField.ClaimantName),
                    (Violation.TooLong 200)
                ))
            ))
            "Changed assertion follows current policy"

        let! _ = verified database
        return ()
    }

let private appendTransitions (actor: ServiceContext) =
    task {
        for revision in 1L .. 59L do
            let act =
                if revision % 2L = 1L then
                    (AuthoredCaseAct.Bare BareAct.Close)
                else
                    (AuthoredCaseAct.Bare BareAct.Reopen)

            let! result =
                (Service.execute
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
                (PartyName.format (Registration.claimant (fields.Registration)))
                name
                "Accepted history is never re-admitted or truncated"
        | _ -> failtest "Expected disclosed retained fields"


let private historyPage =
    function
    | Outcome.Succeeded page -> page
    | _ -> failtest "Expected accepted history page"

let private remainingHistory (actor: ServiceContext) (query: HistoryRequest) =
    task {
        let mutable cursor = query.AfterCursor
        let mutable entries = []

        while cursor.IsSome do
            let! current = (Service.history actor ({ query with AfterCursor = cursor }) (token))

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
    (actor: ServiceContext)
    (query: HistoryRequest)
    (original: OperationReceipt)
    =
    task {
        let! summary =
            (Service.history
                actor
                ({ query with
                    Detail = HistoryDetail.Summary
                })
                (token))

        let summary = historyPage summary
        Expect.equal summary.Entries.Length 50 "Retained payment-recorder rights permit SUMMARY"

        let! observed =
            (Service.observe
                actor
                ({
                    OperationId = (OperationId.value (original.OperationId))
                })
                (token))

        Expect.equal
            observed
            (Outcome.Succeeded(withheldReceipt original))
            "Superseded OPEN keeps every acceptance metadata field and withholds its snapshot"
    }

let private revokedFullHistory
    (database: PostgresFixture.Database)
    (actor: ServiceContext)
    (query: HistoryRequest)
    (first: HistoryResultPage)
    (original: OperationReceipt)
    =
    task {

        Expect.equal
            original.OperationId
            (CaseFixtures.operationId (ClaimCore.TestSupport.StoreFixtures.id 10))
            "Original OPEN acceptance"

        Expect.isSome first.NextCursor "Retain the genuine first FULL continuation"

        let! removed =
            grant
                actor
                (ClaimCore.TestSupport.StoreFixtures.id 200)
                database.Fixture.Owner
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                false

        acceptedAuthority removed

        for request in
            [
                { query with
                    AfterCursor = first.NextCursor
                }
                query
            ] do
            let! refused = (Service.history actor (request) (token))

            Expect.equal
                refused
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
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

        let! first = (Service.history actor (query) (token))

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
                | HistoryEntry.FullEntry { Subject = ReceiptSubject.Case c } ->
                    Revision.value c.Revision
                | _ -> failtest "Full case receipt")

        Expect.equal
            revisions
            (List.rev [ 1L .. 60L ])
            "Every retained revision traverses newest-first without omissions or duplicates"

        assertRetained name entries

        let! summary =
            (Service.history
                actor
                ({ query with
                    Detail = HistoryDetail.Summary
                })
                (token))

        match summary with
        | Outcome.Succeeded p -> Expect.equal p.Entries.Length 50 "SUMMARY retains capacity fifty"
        | _ -> failtest "Retained summary page"

        let original =
            entries
            |> List.pick (function
                | HistoryEntry.FullEntry({ Subject = ReceiptSubject.Case c } as receipt) when
                    Revision.value c.Revision = 1L
                    ->
                    Some receipt
                | _ -> None)

        do! revokedFullHistory database actor query first original

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
