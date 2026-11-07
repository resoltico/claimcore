module ClaimCore.IntegrationTests.ViewCountTests

open System
open System.Threading
open System.Globalization
open ClaimCore.Postgres
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport

let private get (task: System.Threading.Tasks.Task<'value>) = task.GetAwaiter().GetResult()

let private principal subject create =
    create "https://counts.example/" subject
    |> Result.defaultWith (fun _ -> failtest "Valid principal")

[<Tests>]
let tests =
    testCase "view counts preserve scope corruption exclusions and non-disclosure" (fun () ->
        let adminPrincipal =
            PrincipalKey.human "https://scope.example/" "admin"
            |> Result.defaultWith (string >> failtest)

        let database =
            PostgresFixture.createFor adminPrincipal
            |> fun pending -> pending.GetAwaiter().GetResult()

        let id = StoreFixtures.id database.Fixture

        use _owned =
            { new IDisposable with
                member _.Dispose() = database.Fixture.Dispose()
            }

        let admin =
            StoreFixtures.caseWorker database.Fixture
            |> fun pending -> pending.GetAwaiter().GetResult()

        let core principal =
            StoreFixtures.core database.Fixture principal

        let context =
            Service.context admin () CancellationToken.None
            |> fun pending -> pending.GetAwaiter().GetResult()

        let today =
            match context with
            | Outcome.Succeeded {
                                    Requester = RequesterState.Active current
                                } -> current.Runtime.Basis.BusinessDate
            | other -> failtestf "Expected current fixture date: %A" other

        let dueDate = today.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

        for number in [ 3; 4 ] do
            let result =
                Service.execute
                    (core adminPrincipal)
                    (Inputs.caseOperation
                        {
                            OperationId = id number
                            CaseReference = sprintf "COUNT-%d" number
                            ExpectedRevision = 0L
                            Act = AuthoredCaseAct.Open Inputs.registration
                        })
                    CancellationToken.None
                |> get

            match result.Outcome with
            | ExecuteResult.Accepted _ -> ()
            | other -> failtestf "Open fixture: %A" other

        for number in [ 3; 4 ] do
            let result =
                Service.execute
                    (core adminPrincipal)
                    (OperationInput.Reminder
                        {
                            OperationId = id (100 + number)
                            Target = CaseSelector.Reference(sprintf "COUNT-%d" number)
                            Act =
                                AuthoredReminderAct.Create
                                    {
                                        Action = "Inspect evidence"
                                        Due = dueDate
                                    }
                        })
                    CancellationToken.None
                |> get

            match result.Outcome with
            | ExecuteResult.Accepted _ -> ()
            | other -> failtestf "Reminder fixture: %A" other

        let servicePrincipal = principal "service" PrincipalKey.service
        let serviceId = id 5

        let registered =
            Service.execute
                admin
                (OperationInput.Authority(
                    StoreFixtures.authorityRequest
                        serviceId
                        servicePrincipal
                        (AuthorityAct.Register Inputs.actorProfile)
                ))
                CancellationToken.None
            |> fun pending -> pending.GetAwaiter().GetResult()

        StoreFixtures.acceptedAuthority registered

        let granted =
            StoreFixtures.grant
                admin
                (id 7)
                servicePrincipal
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true
            |> fun pending -> pending.GetAwaiter().GetResult()

        StoreFixtures.acceptedAuthority granted

        let changed =
            task {
                use! connection = database.OwnerSource.OpenConnectionAsync()

                return!
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.grants(actor_id,role,case_id) VALUES ($1,'DATA_STEWARD',$2)"
                        [| box serviceId; box (id 3) |]
                        CancellationToken.None
            }
            |> fun pending -> pending.GetAwaiter().GetResult()

        Expect.equal
            changed
            1
            "The negative control inserts exactly one forbidden scoped projection role"

        let corruptedCase = CaseFixtures.caseId (id 3)

        let persisted =
            database.Fixture.Store.Read
                {
                    Work =
                        (fun tx ->
                            tx.Authority
                                {
                                    Principal = servicePrincipal
                                    CaseId = Some corruptedCase
                                })
                    CancellationToken = CancellationToken.None
                }
            |> fun pending -> pending.GetAwaiter().GetResult()

        let persisted = persisted |> Result.defaultWith (fun error -> failtestf "%A" error)

        Expect.contains
            (Map.find corruptedCase persisted.Cases)
            Role.DataSteward
            "The forbidden scope is committed and visible to the actual authority loader"


        let combined =
            Service.workViews (core servicePrincipal) () CancellationToken.None |> get

        match combined with
        | Outcome.Succeeded result ->
            Expect.equal
                (result.CaseViews |> List.map (fun item -> item.View, item.Count))
                [
                    CaseView.NeedsAttention, 1L
                    CaseView.Mine, 0L
                    CaseView.Overdue, 1L
                    CaseView.DueToday, 0L
                    CaseView.NoNextAction, 0L
                    CaseView.Unassigned, 1L
                    CaseView.Undecided, 1L
                    CaseView.DecidedUnpaid, 0L
                    CaseView.Closed, 0L
                    CaseView.Voided, 0L
                    CaseView.All, 1L
                ]
                "An invalid per-case service grant excludes only its scope from every count"
        | other -> failtestf "Expected scoped counts: %A" other

        match combined with
        | Outcome.Succeeded result ->
            Expect.equal
                (result.ReminderViews |> List.map _.Count)
                [ 1L; 0L; 1L; 0L; 0L; 0L; 0L ]
                "Counts exclude reminders on the unavailable scope and retain zero rows"
        | other -> failtestf "Expected empty authorized reminder counts: %A" other

        let denied = principal "no-read" PrincipalKey.human

        let registered =
            Service.execute
                admin
                (OperationInput.Authority(
                    StoreFixtures.authorityRequest
                        (id 6)
                        denied
                        (AuthorityAct.Register Inputs.actorProfile)
                ))
                CancellationToken.None
            |> get

        StoreFixtures.acceptedAuthority registered

        for deniedPrincipal in [ denied; principal "unknown" PrincipalKey.human ] do
            Expect.equal
                (Service.workViews (core deniedPrincipal) () CancellationToken.None |> get)
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "No count leaks to a caller without current read authority")
