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
        | _ -> failtestf "Expected an authorized unrelated-case page, got %A" listed)
