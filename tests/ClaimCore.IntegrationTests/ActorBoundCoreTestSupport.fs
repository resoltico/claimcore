module internal ClaimCore.IntegrationTests.ActorBoundCoreTestSupport

open System
open System.Diagnostics
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.IntegrationTests.Fixtures

let seedLateCase (core: IActorClaimsCore) =
    let mutable finalReference = ""

    for index in 0..51 do
        let reference = sprintf "PAGE-%03d" index
        let input = openRequest (Guid.NewGuid()) reference

        match core.Execute(input, CancellationToken.None) |> await with
        | SubmissionOutcome.Completed(_,
                                      _,
                                      DefiniteExecution.Accepted _,
                                      SettlementConfirmation.Confirmed) -> ()
        | _ -> failtest "Synthetic list seed must be accepted."

        finalReference <- reference

    finalReference

let requireComparableDenialTiming (core: IActorClaimsCore) reference operationId =
    let requireUnavailable =
        function
        | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
        | _ -> failtest "A timing sample must keep the same public refusal."

    let sample action =
        let start = Stopwatch.GetTimestamp()
        action () |> requireUnavailable
        Stopwatch.GetElapsedTime(start).TotalMilliseconds

    let caseKnown () =
        core.Get(reference, CancellationToken.None) |> await

    let caseMissing () =
        core.Get("NO-SUCH-TIMING-CASE", CancellationToken.None) |> await

    let operationKnown () =
        core.ObserveOperation(operationId, CancellationToken.None) |> await

    let missingOperation = Guid.NewGuid()

    let operationMissing () =
        core.ObserveOperation(missingOperation, CancellationToken.None) |> await

    for _ in 1..5 do
        sample caseKnown |> ignore
        sample caseMissing |> ignore
        sample operationKnown |> ignore
        sample operationMissing |> ignore

    let median values =
        let ordered = values |> List.sort
        ordered[ordered.Length / 2]

    let comparable known missing =
        let pairs = [ for _ in 1..32 -> sample known, sample missing ]
        let first, second = pairs |> List.map fst, pairs |> List.map snd

        let lower = max 0.1 (min (median first) (median second))
        let ratio = max (median first) (median second) / lower
        Expect.isLessThan ratio 4.0 "Denied identities stay in one broad timing class"

    comparable caseKnown caseMissing
    comparable operationKnown operationMissing
