module ClaimCore.IntegrationTests.SubmissionServiceTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private required =
    function
    | Ok value -> value
    | Error issue -> failtestf "Expected a valid fixture value: %A" issue

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected successful current read: %A" other

let private run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private verified (database: PostgresFixture.Database) =
    task {
        let! result = Verification.verify database.OwnerSource database.MasterKey None token

        match result with
        | VerificationResult.Verified _ -> ()
        | other -> failtestf "The actual accepted history and projections did not verify: %A" other
    }

let private lifecycle (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! services = caseWorker fixture

        for number, role in [ 900001, Role.DataSteward; 900002, Role.Auditor ] do
            let! reply =
                grant
                    services
                    (id fixture number)
                    fixture.Owner
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople reply

        use! inspector = database.OwnerSource.OpenConnectionAsync()
        let! before = OperationStorage.position inspector token

        let opening =
            request
                fixture
                10
                "Service-Case"
                0L
                "case.open"
                (Submissions.fields (RegistrationInput.values registration))

        let! preview = Service.previewSubmission services opening token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed review) ->
            Expect.equal review.Task (TaskId.ofLiteral "case.open") "Preview uses the selected task"
            Expect.isEmpty review.Changes "Opening review has no accepted part changes"
            Expect.isSome review.Opening "The new case has an opening description"
        | other -> failtestf "Expected review: %A" other

        let! stillBefore = OperationStorage.position inspector token
        Expect.equal stillBefore before "Preview does not append"
        let! reply = Service.executeSubmission services opening token
        let opened = accepted reply
        let parent = CaseId.ofOperationId opened.OperationId
        let! current = Service.get services { Target = CaseSelector.Id parent } token
        let current = read current
        Expect.equal current.Snapshot.Revision Revision.first "Accepted first case revision"

        let laterOpening =
            Submissions.create
                "case.open"
                (id fixture 11)
                (Binding.CaseNew Revision.zero)
                opening.Envelope.Inputs

        let! distinct = Service.executeSubmission services laterOpening token

        Expect.notEqual
            ((accepted distinct |> caseReceipt).Reference)
            ((caseReceipt opened).Reference)
            "Identical business content can open a distinct case with a new issued reference"

        let alias =
            Submissions.create
                "case.open"
                opening.OperationId
                (Binding.CaseNew Revision.zero)
                (opening.Envelope.Inputs
                 |> Map.add
                     (Submissions.field BusinessField.ClaimedAmount)
                     (SubmissionValue.Text "1000"))

        let! replay = Service.executeSubmission services alias token

        expectReceipt
            (replayReceipt replay)
            opened
            "Equivalent authored intent replays its accepted metadata"

        let changed =
            Submissions.create
                "case.open"
                opening.OperationId
                (Binding.CaseNew Revision.zero)
                (opening.Envelope.Inputs
                 |> Map.add
                     (Submissions.field BusinessField.PolicyNumber)
                     (SubmissionValue.Text "Different claimant"))

        let! conflict = Service.executeSubmission services changed token

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "A different intent cannot reuse the operation"

        let work =
            Submissions.create
                "work.update"
                (id fixture 20)
                (Binding.Work(CaseSelector.Id parent, Revision.zero))
                (Map.ofList
                    [
                        Submissions.field BusinessField.HandlingSummary,
                        SubmissionValue.Text "Private summary"
                    ])

        let! workReply = Service.executeSubmission services work token
        accepted workReply |> ignore
        let! currentWork = Service.get services { Target = CaseSelector.Id parent } token
        let currentWork = read currentWork

        Expect.equal
            currentWork.Snapshot.Revision
            Revision.first
            "Work does not advance case revision"

        Expect.equal
            currentWork.ChangedAt
            opened.RecordedAt
            "Only a case part changes the case timestamp"

        let staleErase =
            Submissions.create
                "case.erase"
                (id fixture 21)
                (Binding.CaseErase(
                    CaseSelector.Id parent,
                    Revision.first,
                    ErasureReview.Token current.ErasureReviewToken.Value
                ))
                Map.empty

        let! stale = Service.executeSubmission services staleErase token

        match stale.Outcome with
        | ExecuteResult.Rejected _ -> ()
        | other -> failtestf "A stale family sequence was accepted: %A" other

        let close =
            Submissions.create
                "case.close"
                (id fixture 30)
                (Binding.Case(CaseSelector.Id parent, Revision.first))
                CaseTransitionInputs.closed

        let! closed = Service.executeSubmission services close token
        accepted closed |> ignore

        let staleClose =
            Submissions.create
                "case.close"
                (id fixture 31)
                (Binding.Case(CaseSelector.Id parent, Revision.first))
                CaseTransitionInputs.closed

        let! stale = Service.executeSubmission services staleClose token

        match stale.Outcome with
        | ExecuteResult.Rejected(Refusal.RevisionConflict conflict) ->
            Expect.equal (Revision.value conflict.ActualRevision) 2L "Current case revision"

            match conflict.Changes with
            | Some(ConflictChanges.Page page) ->
                Expect.equal page.Entries.Length 1 "The intervening case part is explained"

                Expect.equal
                    page.Entries.Head.Task
                    (TaskId.ofLiteral "case.close")
                    "The accepted task owns that part"
            | other -> failtestf "Expected an authorized conflict explanation: %A" other
        | other -> failtestf "Expected a revision conflict: %A" other

        do! verified database
    }

let private widePrincipal (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! services = caseWorker fixture
        let prefix = "https://issuer.example/"

        let material =
            [ 1..64 ]
            |> List.map (fun index ->
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("principal-fixture-" + string index)
                )
                |> Convert.ToBase64String
                |> fun value -> value.Replace('+', '-').Replace('/', '_').TrimEnd('='))
            |> String.concat ""

        let issuer = prefix + material.Substring(0, 2048 - prefix.Length)

        let subjects =
            [
                String(Array.init 512 (fun index -> char (0x4E00 + index)))
                String.replicate 256 "😀"
            ]

        use! inspector = database.OwnerSource.OpenConnectionAsync()

        let! _ =
            DatabaseSession.execute
                inspector
                "CREATE TEMP TABLE principal_tuple_probe(principal_kind text,issuer text,subject text)"
                [||]
                token

        let! _ =
            DatabaseSession.execute
                inspector
                "INSERT INTO principal_tuple_probe VALUES ('HUMAN',$1,$2)"
                [| box issuer; box subjects.Head |]
                token

        let mutable oldIndexFailure = None

        try
            let! _ =
                DatabaseSession.execute
                    inspector
                    "CREATE UNIQUE INDEX principal_tuple_probe_key ON principal_tuple_probe(principal_kind,issuer,subject)"
                    [||]
                    token

            ()
        with :? Npgsql.PostgresException as error ->
            oldIndexFailure <- Some(error.SqlState, error.MessageText)

        match oldIndexFailure with
        | Some(state, message) ->
            Expect.equal state "54000" "The legacy tuple exceeds the pinned PostgreSQL index limit"

            Expect.stringContains
                message
                "index row size"
                "The intended oversized-index defect was reached"
        | None -> failtest "The control fixture did not demonstrate the former index failure"

        for offset, subject in subjects |> List.indexed do
            Expect.equal subject.Length 512 "The existing subject limit counts UTF-16 units"
            let principal = PrincipalKey.human issuer subject |> required
            let operation = id fixture (100 + offset)

            let registration =
                Submissions.create
                    "people.register"
                    operation
                    (Binding.PeopleNew Revision.zero)
                    (Submissions.principal principal
                     |> Map.fold
                         (fun values key value -> Map.add key value values)
                         (Submissions.profile
                             {
                                 DisplayName = "A wide identity"
                                 DigestEmail = None
                                 Contact = None
                             }))

            let! reply = Service.executeSubmission services registration token
            acceptedPeople reply
            let actor = operation |> OperationId.create |> Option.get |> ActorId.ofOperationId

            let! page =
                Service.grants
                    services
                    {
                        ActorId = ActorId.value actor
                        AfterCursor = None
                        Limit = 50
                    }
                    token

            let page = read page

            Expect.equal
                page.Actor.Principal
                principal
                "The hash index retains exact maximum-width identity"

            Expect.equal page.Revision Revision.first "First person part"

        do! verified database
    }

let private replayPermission (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! first = caseWorker fixture

        let! audit =
            grant
                first
                (id fixture 901001)
                fixture.Owner
                {
                    Role = Role.Auditor
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople audit
        let other = PrincipalKey.human "https://issuer.example/" "other-manager" |> required
        let otherId = id fixture 901002

        let input =
            Submissions.create
                "people.admit"
                otherId
                (Binding.PeopleNew Revision.zero)
                (Submissions.principal other
                 |> Map.fold
                     (fun values key value -> Map.add key value values)
                     (Submissions.profile
                         {
                             DisplayName = "Other manager"
                             DigestEmail = None
                             Contact = None
                         })
                 |> Map.add
                     (Submissions.input InputTarget.Grants)
                     (SubmissionValue.Rows
                         [
                             Submissions.grant
                                 {
                                     Role = Role.Owner
                                     Scope = GrantTarget.Installation
                                 }
                         ]))

        let! admitted = Service.executeSubmission first input token
        acceptedPeople admitted
        let second = core fixture other

        let subject =
            PrincipalKey.human "https://issuer.example/" "replay-target" |> required

        let registration =
            Submissions.create
                "people.register"
                (id fixture 901003)
                (Binding.PeopleNew Revision.zero)
                (Submissions.principal subject
                 |> Map.fold
                     (fun values key value -> Map.add key value values)
                     (Submissions.profile
                         {
                             DisplayName = "Replay target"
                             DigestEmail = None
                             Contact = None
                         }))

        let! created = Service.executeSubmission first registration token
        acceptedPeople created

        let! revoked =
            grant
                second
                (id fixture 901004)
                fixture.Owner
                {
                    Role = Role.Owner
                    Scope = GrantTarget.Installation
                }
                false

        acceptedPeople revoked
        let! replay = Service.executeSubmission first registration token

        Expect.equal
            replay.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Current read access does not grant management replay"

        let! restored =
            grant
                second
                (id fixture 901005)
                fixture.Owner
                {
                    Role = Role.Owner
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople restored
        let! replay = Service.executeSubmission first registration token
        replayReceipt replay |> ignore

        let! bootstrap =
            Service.provisionOwner first (id fixture 1) DomainInputs.ownerProfile [] token

        match bootstrap with
        | Ok(Verdict.Replay _) -> ()
        | other -> failtestf "Expected explicit bootstrap identity replay: %A" other

        do! verified database
    }

[<Tests>]
let tests =
    testList
        "submission service with PostgreSQL"
        [
            testCaseAsync
                "people replay requires current management and bootstrap retains explicit identity"
                (run replayPermission)
            testCaseAsync
                "preview, commit, intent replay, family concurrency and conflict explanations share the engine"
                (run lifecycle)
            testCaseAsync
                "maximum Unicode principal identity stores, reads and verifies through the hash index"
                (run widePrincipal)
        ]
