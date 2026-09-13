module ClaimCore.Tests.CoreQueryTests

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

let private values =
    [
        "incidentDate", registration.IncidentDate
        "incidentNotificationDate", registration.IncidentNotificationDate
        "incidentCountry", registration.IncidentCountry
        "claimantName", registration.ClaimantName
        "insurerName", registration.InsurerName
        "claimedAmount", registration.ClaimedAmount
        "claimedCurrency", registration.ClaimedCurrency
    ]

let private createWithClock businessClock =
    CoreApi.create
        (new CoreStore.Store() :> IClaimStore)
        (new CoreRecoveryStore.Store() :> IRecoveryStore)
        businessClock

let private create () = createWithClock clock

let private expectReferenceRejection outcome =
    match outcome with
    | QueryOutcome.Rejected rejection ->
        Expect.equal rejection.Code RejectionCode.InvalidInput "Invalid reference"
        Expect.equal rejection.Field (Some "caseReference") "Reference field"
    | _ -> failtest "Expected a reference rejection."

let private invalidReferenceQueries (core: IClaimsCore) =
    core.List({ AfterReference = Some " "; Limit = 1 }, CancellationToken.None)
    |> waitFor
    |> expectReferenceRejection

    core.History(
        {
            CaseReference = " "
            AfterCursor = None
            Limit = 1
            Detail = HistoryDetail.Summary
        },
        CancellationToken.None
    )
    |> waitFor
    |> expectReferenceRejection

let private openCase (core: IClaimsCore) operationId reference =
    let draft =
        {
            OperationId = operationId
            CaseReference = reference
            ExpectedVersion = 0L
            Kind = CommandKind.Open
            Values = values
        }

    core.Execute(draft, CancellationToken.None) |> waitFor |> ignore

let private lookupTests =
    testList
        "typed lookup outcomes"
        [
            testCase "get distinguishes a missing reference from a rejected request" (fun () ->
                let core = create ()

                match core.Get("MISSING-001", CancellationToken.None) |> waitFor with
                | QueryOutcome.Succeeded(Lookup.NotFound reference) ->
                    Expect.equal reference "MISSING-001" "Requested reference"
                | _ -> failtest "Expected typed absence."

                match core.Get(" ", CancellationToken.None) |> waitFor with
                | QueryOutcome.Rejected rejection ->
                    Expect.equal rejection.Code RejectionCode.InvalidInput "Invalid reference"

                    Expect.equal
                        rejection.Action
                        RecommendedAction.CorrectInput
                        "Correction guidance"
                | _ -> failtest "Expected typed input rejection."

                invalidReferenceQueries core)
            testCase
                "operation observation distinguishes a missing operation from a store fault"
                (fun () ->
                    let core = create ()
                    let missing = Guid.Parse("20000000-0000-4000-8000-000000000201")

                    match core.ObserveOperation(missing, CancellationToken.None) |> waitFor with
                    | QueryOutcome.Succeeded(Lookup.NotFound operationId) ->
                        Expect.equal operationId missing "Not-observed operation"
                    | _ -> failtest "Expected explicit operation absence."

                    match core.ObserveOperation(Guid.Empty, CancellationToken.None) |> waitFor with
                    | QueryOutcome.Rejected rejection ->
                        Expect.equal rejection.Field (Some "operationId") "Empty UUID field"
                    | _ -> failtest "Expected empty operation-ID rejection.")
            testCase "empty history is explicit only after the case exists" (fun () ->
                let core = create ()
                openCase core (Guid.Parse("20000000-0000-4000-8000-000000000202")) "HISTORY-001"

                let request =
                    {
                        CaseReference = "HISTORY-001"
                        AfterCursor = Some(HistoryCursor.encode 1L)
                        Limit = 50
                        Detail = HistoryDetail.Summary
                    }

                match core.History(request, CancellationToken.None) |> waitFor with
                | QueryOutcome.Succeeded(Lookup.Found page) ->
                    Expect.isEmpty page.Entries "Existing case can have no later entries"
                | _ -> failtest "Expected found-empty typed history.")
        ]

let private validationTests =
    testList
        "query validation and cancellation"
        [
            testCase "history rejects malformed opaque cursors" (fun () ->
                let request =
                    {
                        CaseReference = "HISTORY-002"
                        AfterCursor = Some "not-a-cursor"
                        Limit = 50
                        Detail = HistoryDetail.Full
                    }

                match (create ()).History(request, CancellationToken.None) |> waitFor with
                | QueryOutcome.Rejected rejection ->
                    Expect.equal rejection.Code RejectionCode.InvalidInput "Cursor is input"
                    Expect.equal rejection.Field (Some "cursor") "Named malformed cursor"
                | _ -> failtest "Expected cursor rejection.")
            testCase "query cancellation returns no synthetic success or failure" (fun () ->
                use cancellation = new CancellationTokenSource()
                cancellation.Cancel()

                match
                    (create ()).List({ AfterReference = None; Limit = 1 }, cancellation.Token)
                    |> waitFor
                with
                | QueryOutcome.Cancelled -> ()
                | _ -> failtest "Expected typed cancellation."

                let clockMustNotRun =
                    { new IBusinessDate with
                        member _.Today() =
                            failwith "Queries must not read the business clock."
                    }

                match
                    (createWithClock clockMustNotRun).Get("MISSING-CLOCK", CancellationToken.None)
                    |> waitFor
                with
                | QueryOutcome.Succeeded(Lookup.NotFound _) -> ()
                | _ -> failtest "Expected a clock-independent missing lookup.")
        ]

let tests = testList "typed core queries" [ lookupTests; validationTests ]
