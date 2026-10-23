module ClaimCore.Tests.ProtocolMappingTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Web.Composition
open ClaimCore.Contract
open ClaimCore.Contract.Protocol
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.Tests.ApplicationFixtures

let private subject =
    AuthenticatedSubjects.human "https://issuer.example/" "owner"
    |> Result.defaultWith failtest

let private admitted result =
    result
    |> Result.defaultWith (fun _ -> failtest "Expected admitted synthetic request")

let private roundTrip value =
    Expect.equal
        (Replies.decode (WireReply.endpoint value) (Replies.encode value))
        (Ok value)
        "Hosting returns a reply accepted by the independent Wire boundary"

let private admittedIdentity =
    function
    | WireRequest.AuthorityExecute request ->
        // The frozen shape corpus intentionally uses "x" as its issuer; exercise Application with an admitted principal.
        WireRequest.AuthorityExecute
            { request with
                Target =
                    {
                        Kind = "HUMAN"
                        Issuer = "https://issuer.example/"
                        Subject = "synthetic-target"
                    }
            }
    | request -> request

let private applicationExamples () =
    use document =
        JsonDocument.Parse(
            File
                .ReadAllBytes(
                    Path.Combine(
                        RepositoryRoot.find (),
                        "tests/fixtures/wire-request-examples.json"
                    )
                )
                .AsMemory()
        )

    document.RootElement.GetProperty("cases").EnumerateArray()
    |> Seq.map (fun example ->
        let endpoint =
            example.GetProperty("endpoint").GetString()
            |> nonNull
            |> Endpoint.tryParse
            |> Option.get

        Admission.admit
            endpoint
            (Encoding.UTF8.GetBytes(example.GetProperty("value").GetRawText()))
        |> admitted)
    |> Seq.filter (fun value ->
        let endpoint = WireRequest.endpoint value in
        endpoint <> Endpoint.Session && endpoint <> Endpoint.SessionLogout)
    |> Seq.map admittedIdentity
    |> Seq.toList

let private mappedExamples () =
    async {
        let store, fixture = fixture ()
        use _owned = store
        let! _ = caseWorker fixture |> Async.AwaitTask
        let service = ServiceGateway.bind (core fixture)

        for request in applicationExamples () do
            let request = ServiceGateway.admitTechnical request |> admitted

            let! reply =
                service.Invoke(subject, request, CancellationToken.None) |> Async.AwaitTask

            roundTrip reply

            Expect.isTrue
                (Correlation.matches request reply)
                "Actual Application replies preserve request correlation"
    }

let private cancellationPolicy () =
    async {
        let store, fixture = fixture ()
        use _owned = store
        let! _ = caseWorker fixture |> Async.AwaitTask
        let service = ServiceGateway.bind (core fixture)

        let request =
            applicationExamples ()
            |> List.pick (function
                | WireRequest.CaseExecute value ->
                    match value.Act with
                    | CaseAct.Open _ -> Some value
                    | _ -> None
                | _ -> None)

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        let! preview =
            service.Invoke(subject, WireRequest.CasePreview request, cancellation.Token)
            |> Async.AwaitTask

        match preview with
        | WireReply.CasePreview {
                                    Outcome = CasePreviewOutcome.Failed CoreFault.StoreUnavailable
                                } -> ()
        | _ -> failtest "Preview observes caller cancellation."

        let! execute =
            service.Invoke(subject, WireRequest.CaseExecute request, cancellation.Token)
            |> Async.AwaitTask

        match execute with
        | WireReply.CaseExecute {
                                    Outcome = CaseExecuteOutcome.Accepted _
                                } -> ()
        | _ -> failtest "A dispatched mutation cannot be cancelled by its caller."

        roundTrip execute
    }

[<Tests>]
let tests =
    testList
        "typed protocol mapping"
        [
            testCaseAsync
                "all published request examples cross Application and return correlated typed replies"
                (mappedExamples ())
            testCaseAsync
                "reads cancel while dispatched mutations retain their completion path"
                (cancellationPolicy ())
        ]
