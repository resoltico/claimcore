module ClaimCore.IntegrationTests.OperationReadTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Postgres
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
            PeopleExecution.provisionStore
                service
                (id fixture 1)
                Inputs.ownerProfile
                People.initialOwnerRoles
                token
            |> sync

        match bootstrap with
        | Ok(Verdict.Proceed _) -> ()
        | other -> failtestf "Bootstrap required: %A" other

        let opened =
            Service.execute
                service
                (Inputs.caseOperation (
                    actRequest fixture 2 "ROOT-READ" 0L (AuthoredCaseAct.Open registration)
                ))
                token
            |> sync
            |> accepted

        Expect.equal
            opened.Position.Sequence
            (Sequence.create 2L |> Option.get)
            "Bootstrap six effects consume one operation position"

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
                tx.PeopleHistoryPage
                    {
                        TargetActorId = None
                        BeforePosition = None
                        Limit = 2
                    })

        Expect.equal
            (first.Items |> List.map _.Index)
            [ 5; 4 ]
            "Descending effect ordinals within bootstrap"

        let second =
            read (fun tx ->
                tx.PeopleHistoryPage
                    {
                        TargetActorId = None
                        BeforePosition = first.Next
                        Limit = 2
                    })

        Expect.equal
            (second.Items |> List.map _.Index)
            [ 3; 2 ]
            "Continuation includes remaining effects of same root"

        Expect.equal
            first.Items.Head.OperationId
            second.Items.Head.OperationId
            "One owning operation"

        let timeline =
            read (fun tx ->
                tx.TimelinePage
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

        let retained = read (fun tx -> tx.FindOperation opened.OperationId) |> Option.get

        Expect.equal
            retained.Root.Hash
            opened.Position.EntryHash
            "Receipt and stored root share proof"

        let target = CaseSelector.Id caseId

        let execute input =
            Service.execute service input token |> sync |> accepted

        execute (
            OperationInput.Work
                {
                    OperationId = id fixture 3
                    Target = target
                    ExpectedRevision = 0L
                    Act =
                        WorkAct.UpdateWork
                            {
                                Responsible = WorkValue.Keep
                                Summary = WorkValue.Set "Root summary"
                            }
                }
        )
        |> ignore

        execute (
            OperationInput.Note
                {
                    OperationId = id fixture 4
                    Target = target
                    Act = AuthoredNoteAct.AddNote "Root note"
                }
        )
        |> ignore

        let notes =
            read (fun tx ->
                tx.NotePage
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
            OperationInput.Reminder
                {
                    OperationId = id fixture 5
                    Target = target
                    Act = AuthoredReminderAct.Create { Action = "First reminder"; Due = due }
                }
        )
        |> ignore

        let compound =
            execute (
                OperationInput.Reminder
                    {
                        OperationId = id fixture 6
                        Target = target
                        Act =
                            AuthoredReminderAct.CompleteAndPlanNext(
                                id fixture 5,
                                1L,
                                { Action = "Next reminder"; Due = due }
                            )
                    }
            )

        let compoundPage =
            read (fun tx ->
                tx.TimelinePage
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
                tx.TimelinePage
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

        let predecessors = read (fun tx -> tx.PreviousEntries primaryPage.Items)

        Expect.equal
            predecessors.Count
            1
            "Primary reminder predecessor is found through root effects"

        let finalReceipt =
            execute (
                OperationInput.Note
                    {
                        OperationId = id fixture 7
                        Target = target
                        Act = AuthoredNoteAct.RemoveNote(id fixture 4)
                    }
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
            "UPDATE claimcore.grants SET granted_effect_index=0 WHERE role='CASE_HANDLER'"
            [||]
            token
        |> sync
        |> ignore

        match administration.Verify(None, token) |> sync with
        | VerificationResult.Diverged finding ->
            Expect.equal
                finding.Finding
                VerificationFinding.PeopleProjection
                "Wrong effect ordinal is detected independently of valid source FK"
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
            DatabaseSession.transaction
                database.RuntimeSource
                System.Data.IsolationLevel.ReadCommitted
                token
                (fun connection transaction ->
                    task {
                        let! head, observed = Transactions.head connection token

                        let header =
                            {
                                OperationHeader.InstallationId = fixture.InstallationId
                                Sequence = Sequence.successor head.Sequence
                                PreviousHash = head.EntryHash
                                OperationId = identity
                                ActorId = target
                                RecordedAt = observed
                                BusinessDate = None
                            }
                        // Accepted historical input deliberately differs from current authoring policy.
                        let retained =
                            RetainedOperation.create
                                header
                                (CommandSkeleton.People
                                    {
                                        TargetActorId = target
                                        Principal = fixture.Owner
                                        Act = PeopleAct.SetProfile 0
                                    })
                                (OperationValues.People { Profile = Some profile })
                                (fun () -> Security.Cryptography.RandomNumberGenerator.GetBytes 32)
                            |> Result.defaultWith (fun error -> failtestf "%A" error)

                        let! _ = OperationStorage.append connection retained.Root token

                        do!
                            Persistence.people
                                connection
                                (PeopleProjection.SetProfile(target, profile))
                                token

                        do! DatabaseSession.commit transaction token
                        return retained
                    })
            |> sync

        let execute operation =
            Service.execute
                service
                (OperationInput.People(
                    Inputs.peopleRequest operation fixture.Owner (PeopleAct.SetProfile input)
                ))
                token
            |> sync

        let replayed = execute raw |> replayReceipt

        Expect.equal
            replayed.OperationId
            identity
            "Exact accepted retry precedes current profile admission"

        Expect.equal replayed.Position acceptedRoot.Position "Replay retains original proof"

        match (execute (Guid.CreateVersion7())).Outcome with
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
            acceptedRoot.Position
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
