module ClaimCore.IntegrationTests.ReceiptDisclosureTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private required =
    function
    | Ok value -> value
    | Error error -> failtestf "%A" error

[<Tests>]
let tests =
    testCaseAsync
        "receipt disclosure uses current roles, revisions and actual compartment erasure"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                let fixture = database.Fixture
                let! worker = caseWorker fixture |> Async.AwaitTask

                let! steward =
                    grant
                        worker
                        (id fixture 910000)
                        fixture.Owner
                        {
                            Role = Role.DataSteward
                            Scope = GrantTarget.Installation
                        }
                        true
                    |> Async.AwaitTask

                acceptedPeople steward

                let opening =
                    request
                        fixture
                        10
                        "RECEIPT-DISCLOSURE"
                        0L
                        "case.open"
                        (Submissions.fields (RegistrationInput.values registration))

                let! first = Service.executeSubmission worker opening token |> Async.AwaitTask
                let receipt = accepted first
                let parent = CaseId.ofOperationId receipt.OperationId
                let readers = ResizeArray<ServiceContext * bool * bool>()

                for number, role, readable, full in
                    [
                        910001, Role.Owner, false, false
                        910002, Role.CaseReader, true, false
                        910003, Role.CaseRegistrar, true, false
                        910004, Role.CaseHandler, true, true
                        910005, Role.PaymentRecorder, true, false
                        910006, Role.DataSteward, true, false
                        910007, Role.Auditor, true, true
                    ] do
                    let principal =
                        PrincipalKey.human "https://issuer.example/" (Role.token role) |> required

                    let submission =
                        Submissions.create
                            "people.admit"
                            (id fixture number)
                            (Binding.PeopleNew Revision.zero)
                            (Submissions.principal principal
                             |> Map.fold
                                 (fun values key value -> Map.add key value values)
                                 (Submissions.profile
                                     {
                                         DisplayName = Role.token role
                                         DigestEmail = None
                                         Contact = None
                                     })
                             |> Map.add
                                 (Submissions.input InputTarget.Grants)
                                 (SubmissionValue.Rows
                                     [
                                         Submissions.grant
                                             {
                                                 Role = role
                                                 Scope = GrantTarget.Installation
                                             }
                                     ]))

                    let! admitted =
                        Service.executeSubmission worker submission token |> Async.AwaitTask

                    acceptedPeople admitted
                    readers.Add(core fixture principal, readable, full)

                let check superseded erased =
                    async {
                        for service, readable, full in readers do
                            let! observed =
                                Service.operationGet
                                    service
                                    { OperationId = opening.OperationId }
                                    token
                                |> Async.AwaitTask

                            match observed.Outcome with
                            | Outcome.Succeeded(OperationLookupResult.Accepted(value, _)) when
                                readable
                                ->
                                let snapshot = (caseReceipt value).Snapshot

                                if erased then
                                    Expect.equal
                                        snapshot
                                        SnapshotDisclosure.Erased
                                        "Actual deleted payload differs from withheld"
                                else
                                    Expect.equal
                                        (match snapshot with
                                         | SnapshotDisclosure.Disclosed _ -> true
                                         | _ -> false)
                                        (not superseded || full)
                                        "Superseded values need full history"
                            | Outcome.Succeeded(OperationLookupResult.Pending _) when not readable ->
                                ()
                            | other -> failtestf "Unexpected disclosure: %A" other
                    }

                do! check false false

                let decision =
                    Submissions.create
                        "case.decide"
                        (id fixture 20)
                        (Binding.Case(CaseSelector.Id parent, Revision.first))
                        (Submissions.fields
                            [
                                BusinessField.PaymentDecisionDate, "2026-08-15"
                                BusinessField.PayableAmount, "750"
                                BusinessField.PayableCurrency, "EUR"
                            ])

                let! decided = Service.executeSubmission worker decision token |> Async.AwaitTask
                accepted decided |> ignore
                do! check true false

                let stewardService = readers[5] |> fun (service, _, _) -> service

                let voiding =
                    Submissions.create
                        "case.void"
                        (id fixture 21)
                        (Binding.Case(CaseSelector.Id parent, Revision.create 2L |> Option.get))
                        (Map.ofList
                            [
                                Submissions.input InputTarget.Reason,
                                SubmissionValue.Text "Entered in error"
                            ])

                let! voided =
                    Service.executeSubmission stewardService voiding token |> Async.AwaitTask

                let firstVoid = accepted voided

                let restoring =
                    Submissions.create
                        "case.reinstate"
                        (id fixture 22)
                        (Binding.Case(CaseSelector.Id parent, Revision.create 3L |> Option.get))
                        (Map.ofList
                            [
                                Submissions.input InputTarget.Reason,
                                SubmissionValue.Text "Confirmed"
                            ])

                let! restored =
                    Service.executeSubmission stewardService restoring token |> Async.AwaitTask

                accepted restored |> ignore

                let! repeated =
                    Service.executeSubmission stewardService voiding token |> Async.AwaitTask

                let repeated = replayReceipt repeated

                Expect.equal
                    (repeated.RecordedBy, repeated.Position, repeated.RecordedAt)
                    (firstVoid.RecordedBy, firstVoid.Position, firstVoid.RecordedAt)
                    "Replay keeps historical acceptance"

                Expect.equal
                    (caseReceipt repeated).Snapshot
                    SnapshotDisclosure.Withheld
                    "Current stewardship does not grant full history"

                let! previewed =
                    Service.previewSubmission stewardService voiding token |> Async.AwaitTask

                match previewed.Outcome with
                | Outcome.Succeeded(Previewed.AlreadyAccepted value) ->
                    Expect.equal
                        (caseReceipt value).Snapshot
                        SnapshotDisclosure.Withheld
                        "Preview replay shares current disclosure"

                    Expect.equal
                        value.Position
                        firstVoid.Position
                        "Preview keeps original acceptance"
                | other -> failtestf "Expected accepted preview: %A" other

                let sequence =
                    reviewedErasureToken fixture.Store fixture.Owner (CaseSelector.Id parent)
                    |> ErasureReview.Token

                let erase =
                    Submissions.create
                        "case.erase"
                        (id fixture 30)
                        (Binding.CaseErase(
                            CaseSelector.Id parent,
                            Revision.create 4L |> Option.get,
                            sequence
                        ))
                        Map.empty

                let! erased = Service.executeSubmission worker erase token |> Async.AwaitTask
                accepted erased |> ignore
                do! check true true
            finally
                database.Fixture.Dispose()
        })

[<Tests>]
let foreignScope =
    testCaseAsync
        "reused operation identity checks both requested and accepted case rights"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                let fixture = database.Fixture
                let! worker = caseWorker fixture |> Async.AwaitTask

                let opening number reference =
                    request
                        fixture
                        number
                        reference
                        0L
                        "case.open"
                        (Submissions.fields (RegistrationInput.values registration))

                let originalA = opening 10 "SCOPE-A"
                let originalB = opening 11 "SCOPE-B"
                let! acceptedA = Service.executeSubmission worker originalA token |> Async.AwaitTask
                let! acceptedB = Service.executeSubmission worker originalB token |> Async.AwaitTask
                let caseA = accepted acceptedA |> _.OperationId |> CaseId.ofOperationId
                let caseB = accepted acceptedB |> _.OperationId |> CaseId.ofOperationId

                let principal =
                    PrincipalKey.human "https://issuer.example/" "scoped-caller" |> required

                let actorId = id fixture 12

                let registered =
                    Submissions.create
                        "people.register"
                        actorId
                        (Binding.PeopleNew Revision.zero)
                        (Submissions.principal principal
                         |> Map.fold
                             (fun values key value -> Map.add key value values)
                             (Submissions.profile
                                 {
                                     DisplayName = "Scoped caller"
                                     DigestEmail = None
                                     Contact = None
                                 }))

                let! registered =
                    Service.executeSubmission worker registered token |> Async.AwaitTask

                acceptedPeople registered

                for number, parent, role in
                    [ 13, caseA, Role.CaseHandler; 14, caseB, Role.CaseReader ] do
                    let! assigned =
                        grant
                            worker
                            (id fixture number)
                            principal
                            {
                                Role = role
                                Scope = GrantTarget.CaseId parent
                            }
                            true
                        |> Async.AwaitTask

                    acceptedPeople assigned

                let scoped = core fixture principal

                let reused =
                    Submissions.create
                        "case.close"
                        originalB.OperationId
                        (Binding.Case(CaseSelector.Id caseA, Revision.first))
                        CaseTransitionInputs.closed

                let! visible = Service.executeSubmission scoped reused token |> Async.AwaitTask

                Expect.equal
                    visible.Outcome
                    (ExecuteResult.Rejected Refusal.IdempotencyConflict)
                    "Both visible subjects disclose only conflict"

                let! revoked =
                    grant
                        worker
                        (id fixture 15)
                        principal
                        {
                            Role = Role.CaseReader
                            Scope = GrantTarget.CaseId caseB
                        }
                        false
                    |> Async.AwaitTask

                acceptedPeople revoked
                let! privateReply = Service.executeSubmission scoped reused token |> Async.AwaitTask

                Expect.equal
                    privateReply.Outcome
                    (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                    "Another readable requested case cannot expose an inaccessible accepted operation"

                let disabled =
                    Submissions.create
                        "people.disable"
                        (id fixture 16)
                        (Binding.People(
                            ActorId.create actorId |> Option.get,
                            Revision.create 4L |> Option.get
                        ))
                        Map.empty

                let! disabled = Service.executeSubmission worker disabled token |> Async.AwaitTask
                acceptedPeople disabled
                let! inactive = Service.executeSubmission scoped reused token |> Async.AwaitTask

                Expect.equal
                    inactive.Outcome
                    (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                    "Disabled callers cannot recover operation identities"
            finally
                database.Fixture.Dispose()
        })
