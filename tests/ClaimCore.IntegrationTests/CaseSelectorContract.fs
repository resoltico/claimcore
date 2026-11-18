module internal ClaimCore.TestSupport.CaseSelectorContract

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport
open ClaimCore.Domain.Changes
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let identities (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let reference = "80000000-0000-4000-8000-000000000609"
        let expected = CaseFixtures.caseId (id fixture 610)

        let openInput: OperationSubmission =
            ClaimCore.TestSupport.Submissions.createRaw
                "case.open"
                (id fixture 610)
                (ClaimCore.Application.Contracts.OperationBinding.CaseNew(0L))
                (ClaimCore.TestSupport.Submissions.fields (RegistrationInput.values (registration))
                 |> Map.add
                     (TaskInputId.Target(InputTarget.Field BusinessField.CaseReference))
                     (ClaimCore.Application.Contracts.SubmissionValue.Text(reference)))

        let! opened = Service.executeSubmission actor ((openInput)) token
        let opening = accepted opened

        Expect.equal
            (caseReceipt opening).CaseId
            expected
            "OPEN identity is its operation UUID, independently of its UUID-shaped reference"

        let! byReference =
            Service.get
                actor
                {
                    Target = CaseSelector.Reference reference
                }
                token

        let! byId = Service.get actor { Target = CaseSelector.Id expected } token

        match byId, byReference with
        | Outcome.Succeeded actual, Outcome.Succeeded expected ->
            for view in [ actual; expected ] do
                Expect.equal view.Work.Basis.TimeZoneId "Etc/UTC" "The fixture uses UTC"

                Expect.equal
                    view.Work.Basis.BusinessDate
                    (DateOnly.FromDateTime view.Work.Basis.ObservedAt.UtcDateTime)
                    "Each read reports its own observation basis"

            Expect.equal
                { actual with
                    Work =
                        { actual.Work with
                            Basis = expected.Work.Basis
                        }
                }
                expected
                "Both selectors return the same case facts and current people across distinct observations"
        | _ -> failtest "Both positive selectors must resolve"

        match byId with
        | Outcome.Succeeded view ->
            Expect.equal view.Snapshot.CaseId expected "Public read identity"
        | _ -> failtest "The independent positive ID selector must resolve"

        let! ambiguous =
            Service.get
                actor
                {
                    Target = CaseSelector.Id(CaseFixtures.caseId (id fixture 609))
                }
                token

        Expect.equal
            ambiguous
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "An ID selector never reinterprets its UUID as a reference"

        let close: OperationSubmission =
            ClaimCore.TestSupport.Submissions.createRaw
                "case.close"
                (id fixture 611)
                (ClaimCore.Application.Contracts.OperationBinding.Case(
                    (CaseSelector.Id expected),
                    (1L),
                    None,
                    (None)
                ))
                CaseTransitionInputs.closed

        let! closed = Service.executeSubmission actor ((close)) token
        let receipt = accepted closed

        Expect.equal
            (caseReceipt receipt).CaseId
            expected
            "Mutation receipt retains the creation ID"

        let! replay =
            Service.executeSubmission
                actor
                (Submissions.createRaw
                    "case.close"
                    close.OperationId
                    (OperationBinding.Case(CaseSelector.Reference reference, 1L, None, None))
                    close.Envelope.Inputs)
                token

        expectReceipt
            (replayReceipt replay)
            receipt
            "Resolved identity makes reference and ID retries equivalent"

        let query: CaseTimelineRequest =
            {
                Target = CaseSelector.Id expected
                AfterCursor = None
                Limit = 1
                Filter = Some TimelineFilter.Facts
            }

        let! history = Service.timeline actor query token

        let first =
            match history with
            | Outcome.Succeeded page -> page
            | _ -> failtest "Authorized ID history must resolve"

        Expect.equal
            (first.CaseId, CaseReference.format first.Reference)
            (expected, reference)
            "History publishes the retained identity"

        match first.Items with
        | [ {
                Changes = TimelineChanges.Disclosed changes
            } ] ->
            Expect.equal
                changes.Items
                [
                    TimelineChange.CaseField
                        {
                            FieldName = BusinessField.Outcome
                            Before = None
                            After =
                                Some(CaseFieldValue.Business(Admitted.Outcome ClosureOutcome.Other))
                        }
                    TimelineChange.CaseField
                        {
                            FieldName = BusinessField.Status
                            Before = Some(CaseFieldValue.Status CaseStatus.Opened)
                            After = Some(CaseFieldValue.Status CaseStatus.Closed)
                        }
                ]
                "The immediate predecessor supplies an independent bounded comparison"
        | _ -> failtest "Expected one disclosed timeline entry"

        Expect.hasLength first.Items 1 "Bounded live page"
        Expect.isSome first.NextCursor "Independent older acceptance remains"

        let! continuation =
            Service.timeline
                actor
                { query with
                    Target = CaseSelector.Reference reference
                    AfterCursor = first.NextCursor
                }
                token

        match continuation with
        | Outcome.Succeeded page ->
            Expect.hasLength
                page.Items
                1
                "A continuation follows the same resource through either selector"

            Expect.isNone page.NextCursor "The opening is the final live keyset row"
        | _ -> failtest "Equivalent selector continuation must remain valid"

        let! second =
            Service.executeSubmission
                actor
                (request
                    fixture
                    612
                    "SELECTOR-OTHER"
                    0L
                    "case.open"
                    (Map.remove
                        (Submissions.field BusinessField.CaseReference)
                        openInput.Envelope.Inputs))
                token

        Expect.equal
            (caseReceipt (accepted second)).CaseId
            (CaseFixtures.caseId (id fixture 612))
            "Another OPEN has a distinct creation identity"

        let! wrongTarget =
            Service.executeSubmission
                actor
                (Submissions.createRaw
                    "case.close"
                    close.OperationId
                    (OperationBinding.Case(
                        CaseSelector.Id(CaseFixtures.caseId (id fixture 612)),
                        1L,
                        None,
                        None
                    ))
                    close.Envelope.Inputs)
                token

        Expect.equal
            wrongTarget.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "An accepted operation cannot be redirected to another case"

        let! moved =
            Service.timeline
                actor
                { query with
                    Target = CaseSelector.Id(CaseFixtures.caseId (id fixture 612))
                    AfterCursor = first.NextCursor
                }
                token

        Expect.equal
            moved
            (Outcome.Rejected Refusal.InvalidCursor)
            "History continuations cannot be moved to another retained ID"

        let! stewardship =
            grant
                actor
                (id fixture 620)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople stewardship

        let! voided =
            Service.executeSubmission
                actor
                (Submissions.createRaw
                    "case.void"
                    (id fixture 621)
                    (OperationBinding.Case(CaseSelector.Id expected, 2L, None, None))
                    (Map.ofList
                        [
                            Submissions.input InputTarget.Reason,
                            SubmissionValue.Text "Synthetic retained identity check"
                        ]))
                token

        accepted voided |> ignore

        let! erased =
            Service.executeSubmission
                actor
                (Submissions.createRaw
                    "case.erase"
                    (id fixture 622)
                    (OperationBinding.Case(
                        CaseSelector.Id expected,
                        3L,
                        None,
                        Some(reviewedFamilySequence fixture.Store (CaseSelector.Id expected))
                    ))
                    Map.empty)
                token

        accepted erased |> ignore
        let! tombstone = Service.get actor { Target = CaseSelector.Id expected } token

        match tombstone with
        | Outcome.Succeeded view ->
            Expect.equal
                (view.Snapshot.CaseId, CaseReference.format view.Snapshot.Reference)
                (expected, reference)
                "Erasure retains immutable identity"

            Expect.equal view.Snapshot.State CaseState.Erased "Business content is erased"
        | _ -> failtest "Retained tombstone must remain addressable"

        let! reuse =
            Service.executeSubmission
                actor
                ((ClaimCore.TestSupport.Submissions.withOperationId (id fixture 623) (openInput)))
                token

        match reuse.Outcome with
        | ExecuteResult.Rejected _ -> ()
        | _ -> failtest "An erased case reference cannot be reused"

        let! fresh =
            Service.executeSubmission
                actor
                (request
                    fixture
                    624
                    "SELECTOR-FRESH"
                    0L
                    "case.open"
                    (Map.remove
                        (Submissions.field BusinessField.CaseReference)
                        openInput.Envelope.Inputs))
                token

        accepted fresh |> ignore
    }

let closedSelectorGrammar () =
    let input =
        JsonValue.Object
            [
                "kind", JsonValue.String "ID"
                "caseId", JsonValue.String "80000000-0000-4000-8000-000000000610"
            ]

    Expect.equal
        (RequestCodecs.caseSelector.Read "$" input)
        (Ok(
            CaseSelector.Id(CaseFixtures.caseId (Guid.Parse "80000000-0000-4000-8000-000000000610"))
        ))
        "Independent ID grammar positive control"

    let mixed =
        match input with
        | JsonValue.Object fields ->
            JsonValue.Object(fields @ [ "caseReference", JsonValue.String "OTHER" ])
        | _ -> failtest "Object control"

    Expect.isError
        (RequestCodecs.caseSelector.Read "$" mixed)
        "An ID/reference pair cannot redirect a request"

let grantScopes (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let expectedId = CaseFixtures.caseId (id fixture 630)
        let reference = CaseFixtures.reference "Scope-A"

        let! opened =
            Service.executeSubmission
                actor
                (((request
                    (fixture)
                    (630)
                    ("Scope-A")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                token

        Expect.equal (caseReceipt (accepted opened)).CaseId expectedId "Independent creation ID"

        let reader =
            PrincipalKey.human "https://issuer.example/" "scope-reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (fixture.Store)
                        ((id fixture 631))
                        (reader)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission (token)
            })

        acceptedPeople registered

        let authored =
            {
                GrantInput.Role = Role.CaseReader
                Scope = GrantTarget.CaseId expectedId
            }

        let expected: ScopedGrant =
            {
                Role = Role.CaseReader
                Scope = GrantScope.Case(expectedId, reference)
            }

        let! input =
            personSubmission
                fixture.Store
                (id fixture 632)
                reader
                None
                "people.grant"
                (Submissions.grant authored)

        let! preview = Service.previewSubmission actor ((input)) token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed review) ->
            match review.Changes.Head.Description with
            | PartDescription.Person person ->
                let assignment = person.Grants |> List.exactlyOne

                Expect.equal
                    assignment.Grant
                    {
                        Role = Role.CaseReader
                        Scope = Scope.Case expectedId
                    }
                    "Review publishes the resolved immutable scope"

                Expect.equal assignment.After true "Review grants access"
            | _ -> failtest "Person part review required"
        | _ -> failtest "The independent ID-scoped grant must preview"

        let! granted = Service.executeSubmission actor ((input)) token
        let acceptedGrant = accepted granted

        let! replay =
            Service.executeSubmission
                actor
                (Submissions.createRaw
                    "people.grant"
                    input.OperationId
                    input.Envelope.Binding
                    (Submissions.grant
                        { authored with
                            Scope = GrantTarget.CaseReference "scope-a"
                        }))
                token

        expectReceipt
            (replayReceipt replay)
            acceptedGrant
            "Reference and ID grants share canonical accepted identity"

        let! stored =
            fixture.Store.Read
                {
                    Work =
                        (fun tx ->
                            LedgerReads.findOperation
                                tx
                                (CaseFixtures.operationId (id fixture 632)))
                    CancellationToken = token
                }

        let entry =
            match stored with
            | Ok(Some value) -> value
            | _ -> failtest "The real accepted people record must exist"

        match entry.Changes.Head.Value with
        | PartValue.Person person ->
            Expect.contains
                person.Grants
                {
                    Grant =
                        {
                            Role = Role.CaseReader
                            Scope = Scope.Case expectedId
                        }
                    Active = true
                }
                "Permanent identity uses the resolved ID, not selector spelling"
        | _ -> failtest "Expected a retained person assignment"

        let query: GrantsRequest =
            {
                ActorId = id fixture 631
                AfterCursor = None
                Limit = 50
            }

        let! page = Service.grants actor query token

        match page with
        | Outcome.Succeeded page ->
            Expect.equal
                (page.Items
                 |> List.map (fun row -> ({ Role = row.Role; Scope = row.Scope }: ScopedGrant)))
                [ expected ]
                "Grant reads publish the same retained identity"
        | _ -> failtest "Scoped grant page must succeed"

        let scopedReader = core fixture reader
        let! visible = Service.get scopedReader { Target = CaseSelector.Id expectedId } token

        Expect.isTrue
            (match visible with
             | Outcome.Succeeded _ -> true
             | _ -> false)
            "ID lookup enforces the granted case scope"

        let! revoked =
            grant
                actor
                (id fixture 633)
                reader
                { authored with
                    Scope = GrantTarget.CaseReference "scope-a"
                }
                false

        acceptedPeople revoked
        let! denied = Service.get scopedReader { Target = CaseSelector.Id expectedId } token

        Expect.equal
            denied
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "The next lookup reauthorizes after revocation"

        let! restored = grant actor (id fixture 634) reader authored true
        acceptedPeople restored

        let! stewardship =
            grant
                actor
                (id fixture 635)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople stewardship

        let! voided =
            Service.executeSubmission
                actor
                ((ClaimCore.TestSupport.Submissions.createRaw
                    "case.void"
                    (id fixture 636)
                    (ClaimCore.Application.Contracts.OperationBinding.Case(
                        (CaseSelector.Id expectedId),
                        (1L),
                        None,
                        (None)
                    ))
                    (Map.ofList
                        [
                            TaskInputId.Target InputTarget.Reason,
                            ClaimCore.Application.Contracts.SubmissionValue.Text(
                                "Synthetic scope retention"
                            )
                        ])))
                token

        accepted voided |> ignore

        let! erased =
            Service.executeSubmission
                actor
                ((ClaimCore.TestSupport.Submissions.createRaw
                    "case.erase"
                    (id fixture 637)
                    (ClaimCore.Application.Contracts.OperationBinding.Case(
                        (CaseSelector.Id expectedId),
                        (2L),
                        None,
                        (Some(reviewedFamilySequence fixture.Store (CaseSelector.Id expectedId)))
                    ))
                    (Map.empty)))
                token

        accepted erased |> ignore
        let! retained = Service.grants actor query token

        match retained with
        | Outcome.Succeeded page ->
            Expect.equal
                (page.Items
                 |> List.map (fun row -> ({ Role = row.Role; Scope = row.Scope }: ScopedGrant)))
                [ expected ]
                "Erasure cannot change the immutable grant identity"
        | _ -> failtest "Erased grant metadata remains readable"

        let! history =
            Service.peopleHistory
                actor
                {
                    TargetActorId = Some(id fixture 631)
                    AfterCursor = None
                    Limit = 50
                }
                token

        match history with
        | Outcome.Succeeded page ->
            let scopes =
                page.Entries
                |> List.collect (fun entry ->
                    entry.Changes.Grants
                    |> List.map (fun assignment ->
                        match assignment.Grant.Scope with
                        | Scope.Case id ->
                            {
                                ScopedGrant.Role = assignment.Grant.Role
                                Scope = GrantScope.Case(id, reference)
                            }
                        | Scope.Installation -> failtest "Case scoped history required"))

            Expect.equal
                scopes
                [ expected; expected; expected ]
                "History reads retained pairs without recovering erased case data"
        | _ -> failtest "People history remains readable after case erasure"
    }
