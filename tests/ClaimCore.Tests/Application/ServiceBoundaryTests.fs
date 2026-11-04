module ClaimCore.Tests.ServiceBoundaryTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures

let private unavailable () =
    let store = new ClaimCore.TestSupport.MemoryStore.Store(installation, instant)
    store.ReadFailure <- Some StoreFailure.Unavailable
    store.WriteFailure <- Some StoreFailure.Unavailable
    store

let private inputs () =
    [
        ClaimCore.TestSupport.Inputs.caseOperation (opening 470 "BOUNDARY")
        OperationInput.Authority(
            authorityRequest
                (id 471)
                other
                (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
        )
    ]

let private malformed () =
    async {
        use probe = unavailable ()

        let core =
            Service.create
                BuildIdentity.current.Version
                installation
                (probe :> IStore)
                calendar
                owner

        let invalid =
            [
                ClaimCore.TestSupport.Inputs.caseOperation
                    { opening 470 "BOUNDARY" with
                        OperationId = Guid.Empty
                    }
                ClaimCore.TestSupport.Inputs.caseOperation (
                    actRequest 471 "BOUNDARY" 1L (AuthoredCaseAct.Void Unchecked.defaultof<string>)
                )
                OperationInput.Authority(
                    authorityRequest
                        Guid.Empty
                        other
                        (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                )
            ]

        for input in invalid do
            let! execute =
                (Service.execute core (input) (CancellationToken.None)) |> Async.AwaitTask

            let! preview =
                (Service.preview core (input) (CancellationToken.None)) |> Async.AwaitTask

            match input with
            | OperationInput.Case _ ->
                Expect.equal
                    execute.Outcome
                    (ExecuteResult.Failed CoreFault.StoreUnavailable)
                    "Case admission needs the snapshot's business date"

                Expect.equal
                    preview.Outcome
                    (Outcome.Failed CoreFault.StoreUnavailable)
                    "Preview uses the same admission snapshot"
            | _ ->
                Expect.isTrue
                    (match execute.Outcome with
                     | ExecuteResult.Rejected _ -> true
                     | _ -> false)
                    "Authority syntax is rejected without a business-date read"

                Expect.isTrue
                    (match preview.Outcome with
                     | Outcome.Rejected _ -> true
                     | _ -> false)
                    "Authority preview syntax is rejected without a business-date read"

        Expect.equal probe.Calls 4 "Each case execution and preview needs one snapshot"

    }

let private failures () =
    async {
        for failure, expected in
            [
                StoreFailure.Unavailable, ExecuteResult.Failed CoreFault.StoreUnavailable
                StoreFailure.Cancelled, ExecuteResult.Failed CoreFault.StoreUnavailable
                StoreFailure.Internal, ExecuteResult.Failed CoreFault.InternalError
                StoreFailure.CommitOutcomeUnknown, ExecuteResult.OutcomeUnknown
            ] do
            use probe = unavailable ()
            probe.WriteFailure <- Some failure

            let core =
                Service.create
                    BuildIdentity.current.Version
                    installation
                    (probe :> IStore)
                    calendar
                    owner

            for input in inputs () do
                let! reply =
                    (Service.execute core (input) (CancellationToken.None)) |> Async.AwaitTask

                Expect.equal
                    reply.OperationId
                    (OperationInput.operationId input)
                    "Failure retains identity"

                Expect.equal reply.Outcome expected "Only dispatched commit uncertainty is unknown"

            Expect.equal probe.Calls 2 "Each write dispatched once"
    }

let private cancelled () =
    async {
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        use probe = unavailable ()

        let core =
            Service.create
                BuildIdentity.current.Version
                installation
                (probe :> IStore)
                calendar
                owner

        for input in inputs () do
            let! execute = (Service.execute core (input) (cancellation.Token)) |> Async.AwaitTask

            let! preview = (Service.preview core (input) (cancellation.Token)) |> Async.AwaitTask

            Expect.equal
                execute.OperationId
                (OperationInput.operationId input)
                "Cancelled write identity"

            Expect.equal
                execute.Outcome
                (ExecuteResult.Failed CoreFault.StoreUnavailable)
                "Definite predispatch failure"

            Expect.equal
                preview.Outcome
                (Outcome.Failed CoreFault.StoreUnavailable)
                "Cancelled preview"

        Expect.equal probe.Calls 0 "No writes or previews dispatched"

        let! context = (Service.context core () cancellation.Token) |> Async.AwaitTask

        let! current =
            (Service.get
                core
                ({
                    Target = CaseSelector.Reference("BOUNDARY")
                })
                (cancellation.Token))
            |> Async.AwaitTask

        let! observed =
            (ClaimCore.TestSupport.StoreFixtures.observe
                core
                ({ OperationId = id 470 })
                (cancellation.Token))
            |> Async.AwaitTask

        Expect.equal context (Outcome.Failed CoreFault.StoreUnavailable) "Cancelled context"
        Expect.equal current (Outcome.Failed CoreFault.StoreUnavailable) "Cancelled case"
        Expect.equal observed (Outcome.Failed CoreFault.StoreUnavailable) "Cancelled observation"
    }

let private committed () =
    async {
        for input in inputs () do
            use memory = new ClaimCore.TestSupport.MemoryStore.Store(installation, instant)

            memory.SeedActor
                worker.Actor.Value
                (worker.Installation |> List.map (fun role -> role, None))

            use cancellation = new CancellationTokenSource()
            let inner = memory :> IStore

            let probe =
                { new IStore with
                    member _.Read
                        ({
                             ReadOperation.Work = work
                             CancellationToken = ct
                         })
                        =
                        inner.Read(
                            {
                                ReadOperation.Work = work
                                CancellationToken = ct
                            }
                        )

                    member _.Write
                        ({
                             WriteOperation.Work = work
                             CancellationToken = ct
                         })
                        =
                        task {
                            let! result =
                                inner.Write(
                                    {
                                        WriteOperation.Work = work
                                        CancellationToken = ct
                                    }
                                )

                            cancellation.Cancel()
                            return result
                        }
                }

            let core =
                Service.create BuildIdentity.current.Version installation probe calendar owner

            let! reply = (Service.execute core (input) (cancellation.Token)) |> Async.AwaitTask

            let! observed =
                ClaimCore.TestSupport.StoreFixtures.observe
                    core
                    {
                        OperationId = OperationInput.operationId input
                    }
                    CancellationToken.None
                |> Async.AwaitTask

            match reply.Outcome, observed with
            | ExecuteResult.Accepted receipt, Outcome.Succeeded stored ->
                Expect.equal receipt stored "Completed acceptance is forwarded despite cancellation"
            | _ -> failtest "Committed acceptance and stored receipt must agree"

            Expect.isTrue
                cancellation.IsCancellationRequested
                "Cancellation occurred at port completion"
    }

[<Tests>]
let tests =
    testList
        "Core port boundaries"
        [
            testCaseAsync
                "case admission needs a snapshot; authority syntax needs no port work"
                (malformed ())
            testCaseAsync "write failures preserve identity and dispatch uncertainty" (failures ())
            testCaseAsync
                "predispatch cancellation and cancelled reads remain definite"
                (cancelled ())
            testCaseAsync
                "completed acceptance survives cancellation before delivery"
                (committed ())
        ]

[<Tests>]
let oneRound =
    testCase "case snapshot admission enriches syntax failures in the application path" (fun () ->
        let request: CaseOperationRequest =
            {
                OperationId = id 479
                Target = CaseSelector.Reference "BAD REF"
                ExpectedRevision = 0L
                Act =
                    CaseAct.Open(
                        ClaimCore.TestSupport.Inputs.registrationFrom
                            [
                                BusinessField.IncidentDate, "2026-09-08"
                                BusinessField.IncidentNotificationDate, "2026-09-06"
                                BusinessField.IncidentCountry, "latvia"
                                BusinessField.ClaimantName, " Padded "
                                BusinessField.ClaimedCurrency, "eur"
                            ]
                    )
            }

        let prepared =
            CaseExecution.prepare request |> Result.defaultWith (string >> failtest)

        let snapshot = (locked worker None None).Read

        let actual =
            CaseExecution.complete (services installation calendar owner) prepared snapshot

        match actual with
        | Error(Refusal.InvalidInput issues) ->
            Expect.equal
                (InputIssues.toList issues |> List.map _.Violation)
                [
                    Violation.ReferenceFormat
                    Violation.FutureDate
                    Violation.ChronologicalOrder
                    Violation.CountryFormat
                    Violation.SurroundingWhitespace
                    Violation.CurrencyFormat
                ]
                "The service clock and partially parsed values participate in the first refusal"
        | result -> failtestf "Expected one refusal with every issue, got %A" result)
