module ClaimCore.IntegrationTests.WorklistAuthorizationTests

open System
open System.Threading
open System.Globalization
open ClaimCore.Postgres
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCase "installation read retains per-case service corruption exclusion" (fun () ->
        let adminPrincipal =
            PrincipalKey.human "https://scope.example/" "admin"
            |> Result.defaultWith (string >> failtest)

        let database =
            PostgresFixture.createFor adminPrincipal
            |> fun pending -> pending.GetAwaiter().GetResult()

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

        let dueDate = today.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

        for index, reference in List.indexed [ "VISIBLE-1"; "VISIBLE-2" ] do
            let opened =
                Service.execute
                    (core adminPrincipal)
                    (ClaimCore.TestSupport.Inputs.caseOperation
                        {
                            OperationId =
                                Guid.Parse(sprintf "b0000000-0000-4000-8000-%012d" (3 + index))
                            CaseReference = reference
                            ExpectedRevision = 0L
                            Act = AuthoredCaseAct.Open Inputs.registration
                        })
                    CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()

            match opened.Outcome with
            | ExecuteResult.Accepted _ -> ()
            | _ -> failtestf "Fixture opening failed: %A" opened

        let servicePrincipal =
            PrincipalKey.service "https://scope.example/" "service"
            |> Result.defaultWith (string >> failtest)

        let serviceId = Guid.Parse "b0000000-0000-4000-8000-000000000010"

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
                (Guid.Parse "b0000000-0000-4000-8000-000000000011")
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
                        [|
                            box serviceId
                            box (Guid.Parse "b0000000-0000-4000-8000-000000000003")
                        |]
                        CancellationToken.None
            }
            |> fun pending -> pending.GetAwaiter().GetResult()

        Expect.equal
            changed
            1
            "The negative control inserts exactly one forbidden scoped projection role"

        let corruptedCase =
            CaseFixtures.caseId (Guid.Parse "b0000000-0000-4000-8000-000000000003")

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


        let service = core servicePrincipal

        let get =
            Service.get
                service
                {
                    Target = CaseSelector.Reference("VISIBLE-1")
                }
                CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()

        let listed =
            Service.list
                service
                (Inputs.caseListPage { AfterCursor = None; Limit = 50 })
                CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()

        Expect.equal
            get
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "The loaded scope corruption refuses case.get"

        match listed with
        | Outcome.Succeeded page ->
            Expect.equal
                (page.Items |> List.map (fun row -> CaseReference.format row.Reference))
                [ "VISIBLE-2" ]
                "An installation read must exclude the refused scope and retain unrelated cases"

            let row = List.exactlyOne page.Items
            Expect.isNone row.NextAction "A case without reminders has no next-action identity"
            Expect.isNone row.Responsible "An unassigned case has no responsible actor reference"
            Expect.equal row.PendingReminders 0L "The list exposes the core pending count"

            Expect.equal
                row.Headline
                CaseHeadline.Undecided
                "The core headline reflects case progress"

            Expect.equal
                row.Attention
                [ CaseAttention.NoNextAction; CaseAttention.Unassigned ]
                "A readable open case reports independent attention reasons"
        | _ -> failtestf "Expected an authorized unrelated-case page, got %A" listed

        for query, expected in
            [ "visible-1", None; "visible-2", Some "VISIBLE-2"; "VISIBLE", None ] do
            let request = Inputs.caseListPage { AfterCursor = None; Limit = 1 }

            let request =
                { request with
                    Filter =
                        { request.Filter with
                            Text = Some query
                        }
                }

            match
                Service.list service request CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()
            with
            | Outcome.Succeeded page ->
                Expect.equal
                    (page.ExactReference
                     |> Option.map (fun exact -> CaseReference.format exact.CaseReference))
                    expected
                    "Exact matches use case-folding and the same read authorization, including corruption exclusion"
            | result -> failtestf "Expected authorized search page, got %A" result

        let target =
            {
                Target = CaseSelector.Reference "VISIBLE-2"
            }

        for principal, expected in
            [ adminPrincipal, Some CaseActKind.Decide; servicePrincipal, None ] do
            match
                Service.get (core principal) target CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()
            with
            | Outcome.Succeeded model ->
                Expect.equal
                    model.Advice.Recommended
                    expected
                    "A recommendation requires an offered act"

                Expect.equal
                    model.Headline
                    CaseHeadline.Undecided
                    "Detail and listing use core headline semantics"

                Expect.equal
                    model.Attention
                    [ CaseAttention.NoNextAction; CaseAttention.Unassigned ]
                    "Detail and listing agree"
            | result -> failtestf "Expected readable detail, got %A" result

        let reminderId = Guid.Parse "b0000000-0000-4000-8000-000000000020"

        let created =
            Service.execute
                (core adminPrincipal)
                (OperationInput.Reminder
                    {
                        OperationId = reminderId
                        Target = CaseSelector.Reference "VISIBLE-2"
                        Act =
                            AuthoredReminderAct.Create
                                {
                                    Action = "Review evidence"
                                    Due = dueDate
                                }
                    })
                CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()

        match created.Outcome with
        | ExecuteResult.Accepted _ -> ()
        | result -> failtestf "Expected accepted reminder, got %A" result

        match
            Service.list
                service
                (Inputs.caseListPage { AfterCursor = None; Limit = 50 })
                CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()
        with
        | Outcome.Succeeded page ->
            let row = List.exactlyOne page.Items

            Expect.equal
                row.PendingReminders
                1L
                "The current snapshot includes the created reminder"

            match row.NextAction with
            | Some next ->
                Expect.equal
                    (ReminderId.value next.ReminderId)
                    reminderId
                    "The renderer can open the exact next reminder"

                Expect.equal
                    (ReminderAction.format next.Action)
                    "Review evidence"
                    "The action is core supplied"

                Expect.equal
                    (CalendarDate.format next.Due)
                    dueDate
                    "The due date belongs to that reminder"

                Expect.equal
                    next.DueState
                    CaseDue.Upcoming
                    "Due state uses the snapshot business date"
            | None -> failtest "A pending reminder must have a next action"
        | result -> failtestf "Expected readable scheduled case, got %A" result)
