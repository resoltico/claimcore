module ClaimCore.IntegrationTests.AcceptedHistoryFixtures

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

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
                    Finance = FinancialPosition.undecided
                }
            )
    }

/// Writes a complete synthetic acceptance without current admission. Valid boundary fixtures exercise
/// reads; a separate negative control deliberately supplies an invalid current-format acceptance.
let internal seed (database: PostgresFixture.Database) claimant =
    let fixture = database.Fixture

    let request =
        request
            database.Fixture
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
                    Nonce = tx.NewPayloadNonce()
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
            |> Result.bind (
                LedgerEntries.receipt
                    (Inputs.workBasis)
                    fixture.Owner
                    view.Actor
                    (view.Actor.Actor |> Option.map Actor.currentReference)
                    (ReceiptCurrent.Case(Some current))
            )
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
                                        (CaseSelector.Reference request.CaseReference)
                                        (Some request.Act)

                                return! decide tx view |> ExecutionFacts.persist tx tx.ProjectCase
                            })
                    CancellationToken = token
                }
            )

        match result with
        | Ok(Verdict.Proceed _) -> ()
        | _ -> failtest "Historical fixture append failed"
    }

let private acceptedBoundary (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let name = String.replicate 200 "A"
        do! seed database name

        let! current =
            (Service.get
                actor
                ({
                    Target = CaseSelector.Reference("historical-1")
                })
                (token))

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
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request
                        database.Fixture
                        11
                        "HISTORICAL-1"
                        1L
                        (AuthoredCaseAct.AmendRegistration(Inputs.registrationUpdate values))
                ))
                (token))

        accepted reaffirm |> ignore

        let! changed =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request
                        database.Fixture
                        12
                        "HISTORICAL-1"
                        2L
                        (AuthoredCaseAct.AmendRegistration(
                            Inputs.registrationUpdate (
                                ClaimCore.TestSupport.Inputs.registrationWith
                                    [ BusinessField.ClaimantName, (name + "B") ]
                                    (values)
                            )
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

let private appendTransitions fixture (actor: ServiceContext) =
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
                    (ClaimCore.TestSupport.Inputs.caseOperation (
                        request fixture (100 + int revision) "HISTORICAL-1" revision act
                    ))
                    (token))

            accepted result |> ignore

    }

let private timelinePage =
    function
    | Outcome.Succeeded page -> page
    | other -> failtestf "Expected accepted timeline page: %A" other

let private retainedPages (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let name = String.replicate 200 "<"
        do! seed database name
        do! appendTransitions database.Fixture actor

        let query: CaseTimelineRequest =
            {
                Target = CaseSelector.Reference "HISTORICAL-1"
                Filter = Some TimelineFilter.Facts
                AfterCursor = None
                Limit = 50
            }

        let! first = Service.timeline actor query token
        let first = timelinePage first
        Expect.hasLength first.Items 10 "Full timeline views preserve the response envelope"
        Expect.isSome first.NextCursor "Older accepted revisions remain"
        let older = ResizeArray<CaseTimelineEntry>()
        let mutable cursor = first.NextCursor

        while cursor.IsSome do
            let! outcome = Service.timeline actor { query with AfterCursor = cursor } token
            let page = timelinePage outcome

            Expect.isLessThanOrEqual
                page.Items.Length
                10
                "Every continuation preserves the full-detail bound."

            older.AddRange page.Items
            cursor <- page.NextCursor

        Expect.hasLength older 50 "All fifty older revisions remain traversable across pages."
        let entries = first.Items @ List.ofSeq older

        Expect.equal
            (entries
             |> List.map (fun entry ->
                 match entry.Subject with
                 | TimelineSubject.CaseFact(revision, _) -> Revision.value revision
                 | _ -> failtest "The fact filter excludes work and reminders"))
            (List.rev [ 1L .. 60L ])
            "Retained revisions traverse newest-first without omissions or duplicates"

        let opening = entries |> List.last

        match opening.Changes with
        | TimelineChanges.Disclosed changes ->
            Expect.isTrue
                (changes.Items
                 |> List.exists (function
                     | TimelineChange.CaseField difference ->
                         difference.FieldName = BusinessField.ClaimantName
                         && difference.Before.IsNone
                         && (match difference.After with
                             | Some(CaseFieldValue.Business(Admitted.Name value)) ->
                                 PartyName.format value = name
                             | _ -> false)
                     | _ -> false))
                "Accepted text is compared without current authoring admission or truncation"
        | _ -> failtest "The authorized opening discloses its original facts"

        let operationId = id database.Fixture 10
        let! original = Service.operationGet actor { OperationId = operationId } token

        let original =
            match original.Outcome with
            | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, true)) -> receipt
            | other -> failtestf "Expected own opening receipt: %A" other

        let! removed =
            grant
                actor
                (id database.Fixture 200)
                database.Fixture.Owner
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                false

        acceptedAuthority removed

        for request in
            [
                query
                { query with
                    AfterCursor = first.NextCursor
                }
            ] do
            let! downgraded = Service.timeline actor request token

            Expect.isTrue
                ((timelinePage downgraded).Items
                 |> List.forall (fun entry -> entry.Changes = TimelineChanges.Withheld))
                "Current metadata access never retains revoked full disclosure, even through a cursor"

        let! observed = Service.operationGet actor { OperationId = operationId } token

        match observed.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted(actual, true)) ->
            let scope = caseReceipt original

            expectReceipt
                actual
                { original with
                    Subject =
                        ReceiptSubject.Case
                            { scope with
                                Snapshot = SnapshotDisclosure.Withheld
                            }
                }
                "Own superseded acceptance remains observable with facts withheld"
        | other -> failtestf "Expected own superseded acceptance: %A" other

        let! _ = verified database
        return ()
    }

let private invalidAcceptance (database: PostgresFixture.Database) =
    task {
        let! _ = caseWorker database.Fixture
        do! seed database (String.replicate 201 "A")
        do! finding VerificationFinding.StateSequence database
    }

[<Tests>]
let tests =
    testList
        "accepted historical pages"
        [
            testCaseAsync
                "replay refuses a fabricated acceptance outside current input policy"
                (run invalidAcceptance)
            testCaseAsync
                "retained text timeline pages are bounded, traverse all revisions and recheck disclosure"
                (run retainedPages)
            testCaseAsync
                "accepted boundary text reads reasserts and verifies while an overlong changed assertion refuses"
                (run acceptedBoundary)

        ]
