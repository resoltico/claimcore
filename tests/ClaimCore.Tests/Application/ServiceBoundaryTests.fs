module ClaimCore.Tests.ServiceBoundaryTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures

/// A port failure has no transaction state and never invokes application callbacks.
type private FailingStore() =
    let mutable calls = 0
    member _.Calls = calls
    member val WriteFailure = StoreFailure.Unavailable with get, set

    interface IStore with
        member _.Context _ =
            calls <- calls + 1
            Task.FromResult(Error StoreFailure.Unavailable)

        member _.Read _ =
            calls <- calls + 1
            Task.FromResult(Error StoreFailure.Unavailable)

        member this.Write _ =
            calls <- calls + 1
            Task.FromResult(Error this.WriteFailure)

let private unavailable () = FailingStore()

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
        let probe = unavailable ()

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

            Expect.equal
                execute.Outcome
                (ExecuteResult.Failed CoreFault.StoreUnavailable)
                "D2 requires current caller state before any Application business refusal"

            Expect.equal
                preview.Outcome
                (Outcome.Failed CoreFault.StoreUnavailable)
                "Preview cannot reveal business validation without its requester snapshot"

        Expect.equal
            probe.Calls
            6
            "Every malformed execute and preview consults exactly one snapshot"

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
            let probe = unavailable ()
            probe.WriteFailure <- failure

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
        let probe = unavailable ()

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

[<Tests>]
let tests =
    testList
        "Core port boundaries"
        [
            testCaseAsync
                "business input refusals require current requester snapshots"
                (malformed ())
            testCaseAsync "write failures preserve identity and dispatch uncertainty" (failures ())
            testCaseAsync
                "predispatch cancellation and cancelled reads remain definite"
                (cancelled ())
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
