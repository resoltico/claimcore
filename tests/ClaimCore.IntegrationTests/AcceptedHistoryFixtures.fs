module ClaimCore.IntegrationTests.AcceptedHistoryFixtures

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

/// Seed accepted values without current admission, including intentionally invalid decision controls.
let internal seed (database: PostgresFixture.Database) claimant =
    task {
        let fixture = database.Fixture

        let submission =
            request
                fixture
                10
                "HISTORICAL-1"
                0L
                "case.open"
                (Submissions.fields (
                    RegistrationInput.values (
                        DomainInputs.registrationWith
                            [ BusinessField.PolicyNumber, claimant ]
                            registration
                    )
                ))

        let! result =
            fixture.Store.Write
                {
                    Work =
                        fun tx ->
                            task {
                                let command = Submissions.command submission

                                let reference = CaseFixtures.reference "HISTORICAL-1"
                                let! current = AggregateStorage.principal tx fixture.Owner
                                let actor = current |> Option.get |> _.Id
                                let operation = CaseFixtures.operationId submission.OperationId

                                let header =
                                    {
                                        OperationHeader.InstallationId = fixture.InstallationId
                                        Sequence = Sequence.successor tx.Head.Sequence
                                        PreviousHash = tx.Head.EntryHash
                                        OperationId = operation
                                        Authority = Authority.Actor actor
                                        RecordedAt = tx.ObservedAt
                                        BusinessDate = Some(fixture.Calendar.DateAt tx.ObservedAt)
                                    }

                                let change =
                                    {
                                        PartChange.Aggregate =
                                            AggregateReference.Family(
                                                CaseId.ofOperationId operation
                                            )
                                        PartId = OperationId.value operation
                                        Revision = Revision.first
                                        NaturalKey = Some "HISTORICAL-1"
                                        Erases = None
                                        Stamp =
                                            {
                                                Position =
                                                    EffectPosition.create header.Sequence 0
                                                    |> Option.get
                                                OperationId = operation
                                                Authority = Authority.Actor actor
                                                RecordedAt = tx.ObservedAt
                                            }
                                        Value =
                                            PartValue.Case
                                                {
                                                    Reference = Some reference
                                                    Lifecycle = Lifecycle.Active
                                                    Handling = Handling.Open
                                                    Disposition = None
                                                    ReopenReason = None
                                                    Content =
                                                        Some
                                                            {
                                                                Registration =
                                                                    DomainInputs
                                                                        .admittedRegistration (
                                                                            DomainInputs
                                                                                .registrationWith
                                                                                [
                                                                                    BusinessField
                                                                                        .PolicyNumber,
                                                                                    claimant
                                                                                ]
                                                                                registration
                                                                        )
                                                                Finance =
                                                                    FinancialPosition.undecided
                                                            }
                                                }
                                    }

                                let retained =
                                    RetainedOperation.create
                                        header
                                        submission.Task
                                        command.Binding
                                        command.Inputs
                                        [ change ]
                                        tx.NewPayloadNonce
                                    |> Result.defaultWith (fun error ->
                                        failtestf "Historical root: %A" error)

                                let family =
                                    CaseFamily.apply None change
                                    |> Result.defaultWith (fun error ->
                                        failtestf "Permanent family: %A" error)

                                let origins =
                                    Map.ofList [ (change.Kind, change.PartId), change.Stamp ]

                                let reportDates =
                                    CaseReportDates.record
                                        (CalendarDate.ofDate header.BusinessDate.Value)
                                        (Some CaseStatus.Opened)
                                        None

                                let rows =
                                    ProjectionRows.family family [ change ] origins None reportDates
                                    |> Result.defaultWith (fun error ->
                                        failtestf "Historical projection: %A" error)

                                do! tx.Append retained
                                do! tx.ProjectFamily rows
                                return Verdict.Proceed retained

                            }
                    CancellationToken = token
                }

        match result with
        | Ok(Verdict.Proceed _) -> ()
        | other -> failtestf "Historical fixture append: %A" other
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
                (Identifier.format (Registration.policyNumber (fields.Registration) |> Option.get))
                name
                "Retained record renders without admission"
        | _ -> failtest "Historical record could not be read"

        let values =
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [
                    BusinessField.PolicyNumber, (name)
                    BusinessField.InsurerClaimReference, ("Corrected Insurer")
                ]
                (registration))

        let! reaffirm =
            (Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (11)
                    ("HISTORICAL-1")
                    (1L)
                    "case.amendRegistration"
                    (ClaimCore.TestSupport.Submissions.updates (
                        RegistrationUpdateInput.values ((DomainInputs.registrationUpdate values))
                    )))))
                (token))

        accepted reaffirm |> ignore

        let! changed =
            (Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (12)
                    ("HISTORICAL-1")
                    (2L)
                    "case.amendRegistration"
                    (ClaimCore.TestSupport.Submissions.updates (
                        RegistrationUpdateInput.values (
                            (DomainInputs.registrationUpdate (
                                ClaimCore.TestSupport.DomainInputs.registrationWith
                                    [ BusinessField.PolicyNumber, (name + "B") ]
                                    (values)
                            ))
                        )
                    )))))
                (token))

        Expect.equal
            changed.Outcome
            (ExecuteResult.Rejected(
                (Refusal.invalidInput (
                    (InputTarget.Field BusinessField.PolicyNumber),
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
            let taskName, inputs =
                if revision % 2L = 1L then
                    "case.close", CaseTransitionInputs.closed
                else
                    "case.reopen", CaseTransitionInputs.reopened

            let! result =
                Service.executeSubmission
                    actor
                    (request fixture (100 + int revision) "HISTORICAL-1" revision taskName inputs)
                    token

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
                 Expect.equal
                     entry.Subject.PartKind
                     ClaimCore.Domain.Changes.PartKind.Case
                     "Fact filter selects case parts"

                 Revision.value entry.Subject.Revision))
            (List.rev [ 1L .. 60L ])
            "Retained revisions traverse newest-first without omissions or duplicates"

        let opening = entries |> List.last

        match opening.Changes with
        | TimelineChanges.Disclosed changes ->
            Expect.isTrue
                (changes.Items
                 |> List.exists (function
                     | TimelineChange.CaseField difference ->
                         difference.FieldName = BusinessField.PolicyNumber
                         && difference.Before.IsNone
                         && (match difference.After with
                             | Some(CaseFieldValue.Business(Admitted.Identifier value)) ->
                                 Identifier.format value = name
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

        acceptedPeople removed

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
