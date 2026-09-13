module ClaimCore.Tests.PaginationTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private clock =
    { new IBusinessDate with
        member _.Today() = today
    }

let private waitFor (task: Task<'value>) = task.GetAwaiter().GetResult()

let private registrationValues =
    [
        "incidentDate", registration.IncidentDate
        "incidentNotificationDate", registration.IncidentNotificationDate
        "incidentCountry", registration.IncidentCountry
        "claimantName", registration.ClaimantName
        "insurerName", registration.InsurerName
        "claimedAmount", registration.ClaimedAmount
        "claimedCurrency", registration.ClaimedCurrency
    ]

let private create () =
    CoreApi.create
        (new CoreStore.Store() :> IClaimStore)
        (new CoreRecoveryStore.Store() :> IRecoveryStore)
        clock

let private openCase (core: IClaimsCore) operationId reference =
    let command =
        {
            OperationId = operationId
            CaseReference = reference
            ExpectedVersion = 0L
            Kind = CommandKind.Open
            Values = registrationValues
        }

    core.Execute(command, CancellationToken.None) |> waitFor |> ignore

let private closeCase (core: IClaimsCore) operationId reference revision =
    let command =
        {
            OperationId = operationId
            CaseReference = reference
            ExpectedVersion = revision
            Kind = CommandKind.Close
            Values = []
        }

    core.Execute(command, CancellationToken.None) |> waitFor |> ignore

let private casePages =
    testCase "case listing returns request-bounded stable pages without loss or overlap" (fun () ->
        let core = create ()

        for index in 0..2 do
            openCase
                core
                (Guid.Parse($"20000000-0000-4000-8000-{index + 300:D12}"))
                $"PAGE-{index:D3}"

        let first =
            core.List({ AfterReference = None; Limit = 2 }, CancellationToken.None)
            |> waitFor

        match first with
        | QueryOutcome.Succeeded page ->
            Expect.equal
                (page.Items |> List.map _.CaseReference)
                [ "PAGE-000"; "PAGE-001" ]
                "First page"

            Expect.equal page.NextAfterReference (Some "PAGE-001") "Opaque continuation source"

            match
                core.List(
                    {
                        AfterReference = page.NextAfterReference
                        Limit = 2
                    },
                    CancellationToken.None
                )
                |> waitFor
            with
            | QueryOutcome.Succeeded next ->
                Expect.equal (next.Items |> List.map _.CaseReference) [ "PAGE-002" ] "No overlap"
                Expect.isNone next.NextAfterReference "Completed page"
            | _ -> failtest "Expected second case page."
        | _ -> failtest "Expected first case page.")

let private historyPages =
    testCase
        "history cursors preserve detail mode and continue from the last returned revision"
        (fun () ->
            let core = create ()
            let reference = "HISTORY-PAGE-001"
            openCase core (Guid.Parse("20000000-0000-4000-8000-000000000310")) reference
            closeCase core (Guid.Parse("20000000-0000-4000-8000-000000000311")) reference 1L

            let request =
                {
                    CaseReference = reference
                    AfterCursor = None
                    Limit = 1
                    Detail = HistoryDetail.Summary
                }

            match core.History(request, CancellationToken.None) |> waitFor with
            | QueryOutcome.Succeeded(Lookup.Found page) ->
                Expect.equal page.Entries.Length 1 "First history entry"
                Expect.isSome page.NextCursor "Opaque history cursor"

                let next =
                    { request with
                        AfterCursor = page.NextCursor
                    }

                match core.History(next, CancellationToken.None) |> waitFor with
                | QueryOutcome.Succeeded(Lookup.Found second) ->
                    Expect.equal second.Entries.Length 1 "Second history entry"
                    Expect.isNone second.NextCursor "No duplicate continuation"
                | _ -> failtest "Expected second history page."
            | _ -> failtest "Expected first history page.")

let tests = testList "typed pagination boundaries" [ casePages; historyPages ]
