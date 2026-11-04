module ClaimCore.Tests.WorklistAuthorizationTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCase "installation read retains per-case service corruption exclusion" (fun () ->
        let installation = Guid.Parse "b0000000-0000-4000-8000-000000000001"

        use memory =
            new MemoryStore.Store(installation, DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero))

        let calendar =
            { new IBusinessCalendar with
                member _.DateAt _ = DateOnly(2026, 9, 7)
                member _.TimeZoneId = "Etc/UTC"
            }

        let principal subject create =
            create "https://issuer.example/" subject
            |> Result.defaultWith (fun _ -> failtest "Fixture principal")

        let adminPrincipal = principal "admin" PrincipalKey.human

        let admin =
            {
                ActorId = CaseFixtures.actorId (Guid.Parse "b0000000-0000-4000-8000-000000000002")
                Principal = adminPrincipal
                Enabled = true
            }

        memory.SeedActor admin [ Role.CaseHandler, None ]

        let core principal =
            Service.create "synthetic" installation (memory :> IStore) calendar principal

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

        let servicePrincipal = principal "service" PrincipalKey.service

        let actor =
            {
                ActorId = CaseFixtures.actorId (Guid.Parse "b0000000-0000-4000-8000-000000000010")
                Principal = servicePrincipal
                Enabled = true
            }

        let caseId = CaseFixtures.caseId (Guid.Parse "b0000000-0000-4000-8000-000000000003")
        memory.SeedActor actor [ Role.CaseReader, None; Role.DataSteward, Some caseId ]
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
            | result -> failtestf "Expected readable detail, got %A" result)
