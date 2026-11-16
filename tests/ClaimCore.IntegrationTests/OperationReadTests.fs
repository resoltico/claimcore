module ClaimCore.IntegrationTests.OperationReadTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Application
open ClaimCore.Application.Administration
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None
let private sync (pending: System.Threading.Tasks.Task<'a>) = pending.GetAwaiter().GetResult()

let private runtime () =
    let database = PostgresFixture.create () |> sync

    try
        let fixture = database.Fixture
        let service = core fixture fixture.Owner

        let bootstrap =
            Service.provisionOwner
                service
                (id fixture 1)
                Inputs.ownerProfile
                ClaimCore.Domain.Tasks.Registry.initialOwnerRoles
                token
            |> sync

        match bootstrap with
        | Ok(Verdict.Proceed _) -> ()
        | other -> failtestf "Bootstrap required: %A" other

        let opened =
            Service.executeSubmission
                service
                (((request
                    (fixture)
                    (2)
                    ("ROOT-READ")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                token
            |> sync
            |> accepted

        Expect.equal
            opened.Position.Sequence
            (Sequence.create 2L |> Option.get)
            "Bootstrap person part consumes one operation position"

        let caseId = (caseReceipt opened).CaseId

        let read work =
            fixture.Store.Read
                {
                    Work = work
                    CancellationToken = token
                }
            |> sync
            |> Result.defaultWith (fun error -> failtestf "Read failed: %A" error)

        let first =
            read (fun tx ->
                PeopleReads.peopleHistoryPage
                    tx
                    {
                        TargetActorId = None
                        BeforePosition = None
                        Limit = 2
                    })

        Expect.equal
            (first.Items |> List.map _.Index)
            [ 0 ]
            "Bootstrap has one person part containing every initial role"

        Expect.isNone first.Next "The complete bootstrap fits in one part"

        match first.Items.Head.Change.Value with
        | PartValue.Person person ->
            Expect.equal
                person.Grants.Length
                5
                "All explicit initial assignments are retained in that part"
        | _ -> failtest "Bootstrap person part required"

        let timeline =
            read (fun tx ->
                LedgerReads.timelinePage
                    tx
                    {
                        CaseId = caseId
                        Filter = None
                        Range = None
                        BeforePosition = None
                        Limit = 2
                    })

        Expect.equal timeline.Items.Length 1 "One opening effect"

        Expect.equal
            timeline.Items.Head.OperationId
            opened.OperationId
            "Timeline binds actual retained root"

        let retained =
            read (fun tx -> LedgerReads.findOperation tx opened.OperationId) |> Option.get

        Expect.equal
            retained.Root.Hash
            opened.Position.EntryHash
            "Receipt and stored root share proof"

        let target = CaseSelector.Id caseId

        let execute input =
            Service.executeSubmission service input token |> sync |> accepted

        execute (
            (ClaimCore.TestSupport.Submissions.createRaw)
                "work.update"
                (id fixture 3)
                (ClaimCore.Application.Contracts.OperationBinding.Work((target), (0L)))
                (ClaimCore.TestSupport.Submissions.work (
                    {
                        Responsible = WorkValue.Keep
                        Summary = WorkValue.Set "Root summary"
                    }
                ))
        )
        |> ignore

        execute (
            (ClaimCore.TestSupport.Submissions.createRaw)
                "note.add"
                (id fixture 4)
                (ClaimCore.Application.Contracts.OperationBinding.Note((target), None))
                (Map.ofList
                    [
                        TaskInputId.Target InputTarget.NoteText,
                        ClaimCore.Application.Contracts.SubmissionValue.Text("Root note")
                    ])
        )
        |> ignore

        let notes =
            read (fun tx ->
                NoteReads.notePage
                    tx
                    {
                        CaseId = caseId
                        Before = None
                        Limit = 2
                    })

        Expect.equal notes.Items.Length 1 "Note projection joins exact source effect"

        Expect.equal
            notes.Items.Head.CreatedPosition
            (EffectPosition.create (Sequence.create 4L |> Option.get) 0 |> Option.get)
            "Note provenance includes effect index"

        let due =
            opened.Basis.BusinessDate
                .AddDays(1)
                .ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

        execute (
            (ClaimCore.TestSupport.Submissions.createRaw)
                "reminder.create"
                (id fixture 5)
                (ClaimCore.Application.Contracts.OperationBinding.Reminder((target), None))
                (ClaimCore.TestSupport.Submissions.reminderPlan ("First reminder") (due))
        )
        |> ignore

        let compound =
            execute (
                (ClaimCore.TestSupport.Submissions.createRaw)
                    "reminder.completeAndPlanNext"
                    (id fixture 6)
                    (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                        (target),
                        Some((id fixture 5), (1L))
                    ))
                    (ClaimCore.TestSupport.Submissions.reminderPlan ("Next reminder") (due))
            )

        let compoundPage =
            read (fun tx ->
                LedgerReads.timelinePage
                    tx
                    {
                        CaseId = caseId
                        Filter = None
                        Range = None
                        BeforePosition = None
                        Limit = 1
                    })

        Expect.equal compoundPage.Items.Head.Index 1 "Compound secondary effect orders last"

        let primaryPage =
            read (fun tx ->
                LedgerReads.timelinePage
                    tx
                    {
                        CaseId = caseId
                        Filter = None
                        Range = None
                        BeforePosition = compoundPage.Next
                        Limit = 1
                    })

        Expect.equal primaryPage.Items.Head.Index 0 "Timeline cursor preserves compound primary"

        Expect.equal
            primaryPage.Items.Head.OperationId
            compound.OperationId
            "Both effects bind one root"

        let predecessors = read (fun tx -> LedgerReads.previousEntries tx primaryPage.Items)

        Expect.equal
            predecessors.Count
            1
            "Primary reminder predecessor is found through root effects"

        let finalReceipt =
            execute (
                (ClaimCore.TestSupport.Submissions.createRaw)
                    "note.remove"
                    (id fixture 7)
                    (ClaimCore.Application.Contracts.OperationBinding.Note(
                        (target),
                        Some((id fixture 4))
                    ))
                    (Map.empty)
            )

        use administration =
            new Administration(
                database.Container.GetConnectionString(),
                BuildIdentity.current.Version
            )

        let status = administration.Status token |> sync
        Expect.isSome status.InitialOwner "Status reads owner from root effects"
        Expect.equal status.Position finalReceipt.Position "Status reports operation head"

        match administration.Verify(None, token) |> sync with
        | VerificationResult.Verified _ -> ()
        | other -> failtestf "Actual root replay verification: %A" other

        // A valid source FK is insufficient: the exact granting effect must agree with replay.
        use ownerConnection = database.OwnerSource.OpenConnection()

        DatabaseSession.execute
            ownerConnection
            "UPDATE claimcore.grants SET granted_sequence=2,granted_effect_index=0 WHERE role='CASE_HANDLER'"
            [||]
            token
        |> sync
        |> ignore

        match administration.Verify(None, token) |> sync with
        | VerificationResult.Diverged finding ->
            Expect.equal
                finding.Finding
                VerificationFinding.PeopleProjection
                "Wrong accepting part is detected independently of valid source FK"
        | other -> failtestf "Poisoned grant provenance must diverge: %A" other

        administration.Maintenance(MaintenanceChange.On, token) |> sync |> ignore

        let checkpoint =
            InstallationCheckpoint.create fixture.InstallationId finalReceipt.Position None
            |> Option.get

        let rebuilt = administration.Rebuild(checkpoint, token) |> sync

        let rebuilt =
            rebuilt
            |> Result.defaultWith (fun error -> failtestf "Rebuild failed: %A" error)

        Expect.equal
            (rebuilt.PreviousDivergence |> Option.map _.Finding)
            (Some VerificationFinding.PeopleProjection)
            "Rebuild reports and repairs exact effect provenance"

        match administration.Verify(Some checkpoint, token) |> sync with
        | VerificationResult.Verified _ -> ()
        | other -> failtestf "Rebuilt actual root verification: %A" other
    finally
        database.Fixture.Dispose()

let private profileReplayBeforeAdmission () =
    let database = PostgresFixture.create () |> sync

    try
        let fixture = database.Fixture
        let service = bootstrap fixture |> sync
        let raw = Guid.CreateVersion7()
        let identity = OperationId.create raw |> Option.get

        let input: ActorProfileInput =
            {
                DisplayName = " retained name "
                Contact = Some " retained contact "
            }

        let profile =
            ActorProfile.restore input
            |> Result.defaultWith (fun error -> failtestf "%A" error)

        Expect.isError
            (ActorProfile.admit profile)
            "Current policy refuses the historical authored shape"

        let target = ActorId.ofOperationId (CaseFixtures.operationId (id fixture 1))

        let acceptedRoot =
            fixture.Store.Write
                {
                    Work =
                        fun tx ->
                            task {
                                let! before = AggregateStorage.principal tx fixture.Owner
                                let person = before |> Option.get

                                let header =
                                    {
                                        OperationHeader.InstallationId = fixture.InstallationId
                                        Sequence = Sequence.successor tx.Head.Sequence
                                        PreviousHash = tx.Head.EntryHash
                                        OperationId = identity
                                        ActorId = target
                                        RecordedAt = tx.ObservedAt
                                        BusinessDate = None
                                    }

                                let change =
                                    {
                                        PartChange.Aggregate = AggregateReference.Person target
                                        PartId = ActorId.value target
                                        Revision = Revision.successor person.Revision
                                        NaturalKey = None
                                        Value =
                                            PartValue.Person
                                                {
                                                    Principal = None
                                                    Enabled = person.Actor.Enabled
                                                    Grants = []
                                                    Profile = ProfileValue.Present(Some profile)
                                                }
                                        Erases = None
                                        Stamp =
                                            {
                                                Position =
                                                    EffectPosition.create header.Sequence 0
                                                    |> Option.get
                                                OperationId = identity
                                                RecordedAt = tx.ObservedAt
                                                ActorId = target
                                            }
                                    }

                                let inputs =
                                    InputMap.create
                                        (Map.ofList
                                            [
                                                Submissions.input InputTarget.ProfileDisplayName,
                                                InputValue.Text input.DisplayName
                                                Submissions.input InputTarget.ProfileContact,
                                                InputValue.Text input.Contact.Value
                                            ])
                                        Map.empty
                                    |> Option.get

                                let retained =
                                    RetainedOperation.create
                                        header
                                        (TaskId.ofLiteral "people.setProfile")
                                        (Binding.People(target, Revision.first))
                                        inputs
                                        [ change ]
                                        tx.NewPayloadNonce
                                    |> Result.defaultWith (fun error -> failtestf "%A" error)

                                do! tx.Append retained

                                let after =
                                    Person.apply (Some person) change
                                    |> Result.defaultWith (fun error -> failtestf "%A" error)

                                let rows =
                                    ProjectionRows.person after
                                    |> Result.defaultWith (fun error -> failtestf "%A" error)

                                do! tx.ProjectPerson rows
                                return Verdict.Proceed retained
                            }
                    CancellationToken = token
                }
            |> sync
            |> Result.defaultWith (fun error -> failtestf "Historical profile setup: %A" error)
            |> function
                | Verdict.Proceed retained -> retained
                | other -> failtestf "Historical profile setup verdict: %A" other

        let execute operation expected =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (service).Store
                        (operation)
                        (fixture.Owner)
                        (Some expected)
                        "people.setProfile"
                        (ClaimCore.TestSupport.Submissions.profile (input))

                return! Service.executeSubmission (service) submission (token)
            })
            |> sync

        let replayed = execute raw 1L |> replayReceipt

        Expect.equal
            replayed.OperationId
            identity
            "Exact accepted retry precedes current profile admission"

        Expect.equal replayed.Position acceptedRoot.Root.Position "Replay retains original proof"

        match (execute (Guid.CreateVersion7()) 2L).Outcome with
        | ExecuteResult.Rejected(Refusal.InvalidInput issues) ->
            Expect.equal
                (InputIssues.toList issues |> List.map _.Target)
                [ InputTarget.ProfileDisplayName; InputTarget.ProfileContact ]
                "Independent current profile issues accumulate once"
        | other -> failtestf "Fresh operation must receive current profile policy: %A" other

        use administration =
            new Administration(
                database.Container.GetConnectionString(),
                BuildIdentity.current.Version
            )

        Expect.equal
            (administration.Head token |> sync |> _.Position)
            acceptedRoot.Root.Position
            "Refusal does not append"

        match administration.Verify(None, token) |> sync with
        | VerificationResult.Verified _ -> ()
        | other -> failtestf "Replay checks permanent typed profile semantics: %A" other
    finally
        database.Fixture.Dispose()

[<Tests>]
let tests =
    testList
        "operation root runtime"
        [
            testCase
                "PostgreSQL operation readers, same-root continuation and projection rebuild"
                runtime
            testCase
                "accepted people replay precedes current profile admission"
                profileReplayBeforeAdmission
        ]
