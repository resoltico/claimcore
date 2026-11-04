module ClaimCore.Tests.ViewCountTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport

let private get (task: System.Threading.Tasks.Task<'value>) = task.GetAwaiter().GetResult()

let private principal subject create =
    create "https://counts.example/" subject
    |> Result.defaultWith (fun _ -> failtest "Valid principal")

let private id number =
    Guid.Parse(sprintf "e91ca25a-4e72-4f36-b28b-%012d" number)

[<Tests>]
let tests =
    testCase "view counts preserve scope corruption exclusions and non-disclosure" (fun () ->
        let installation = id 1

        use store =
            new MemoryStore.Store(
                installation,
                DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)
            )

        let calendar =
            { new IBusinessCalendar with
                member _.DateAt _ = DateOnly(2026, 11, 4)
                member _.TimeZoneId = "Etc/UTC"
            }

        let adminPrincipal = principal "admin" PrincipalKey.human

        let admin =
            {
                ActorId = ActorId.create (id 2) |> Option.get
                Principal = adminPrincipal
                Enabled = true
            }

        store.SeedActor admin [ Role.CaseHandler, None ]

        let core principal =
            Service.create "counts" installation (store :> IStore) calendar principal

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
                                        Due = "2026-11-03"
                                    }
                        })
                    CancellationToken.None
                |> get

            match result.Outcome with
            | ExecuteResult.Accepted _ -> ()
            | other -> failtestf "Reminder fixture: %A" other

        let servicePrincipal = principal "service" PrincipalKey.service

        let serviceActor =
            {
                ActorId = ActorId.create (id 5) |> Option.get
                Principal = servicePrincipal
                Enabled = true
            }

        store.SeedActor
            serviceActor
            [
                Role.CaseReader, None
                Role.DataSteward, Some(CaseId.create (id 3) |> Option.get)
            ]

        match Service.caseViews (core servicePrincipal) () CancellationToken.None |> get with
        | Outcome.Succeeded result ->
            Expect.equal
                (result.Items |> List.map (fun item -> item.View, item.Count))
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

        match Service.reminderViews (core servicePrincipal) () CancellationToken.None |> get with
        | Outcome.Succeeded result ->
            Expect.equal
                (result.Items |> List.map _.Count)
                [ 1L; 0L; 1L; 0L; 0L; 0L; 0L ]
                "Counts exclude reminders on the unavailable scope and retain zero rows"
        | other -> failtestf "Expected empty authorized reminder counts: %A" other

        let denied = principal "no-read" PrincipalKey.human

        store.SeedActor
            {
                ActorId = ActorId.create (id 6) |> Option.get
                Principal = denied
                Enabled = true
            }
            []

        for deniedPrincipal in [ denied; principal "unknown" PrincipalKey.human ] do
            Expect.equal
                (Service.caseViews (core deniedPrincipal) () CancellationToken.None |> get)
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "No count leaks to a caller without current read authority"

            Expect.equal
                (Service.reminderViews (core deniedPrincipal) () CancellationToken.None |> get)
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Reminder counts keep the same non-disclosure")
