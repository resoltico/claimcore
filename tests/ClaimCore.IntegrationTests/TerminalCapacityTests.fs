module ClaimCore.IntegrationTests.TerminalCapacityTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures

let private openRuntime () =
    Runtime.OpenPostgres(appConnection (), CancellationToken.None)
    |> await
    |> Result.defaultWith (fun _ -> failtest "Synthetic capacity runtime must open.")

let private acceptOne (core: IClaimsCore) sequence =
    let request =
        openRequest (Guid.NewGuid()) ($"TERMINAL-CAPACITY-{sequence:D4}-{Guid.NewGuid():N}")

    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted receipt,
                                  SettlementConfirmation.Confirmed) ->
        Expect.equal
            receipt.OperationId
            request.OperationId
            "Each synthetic terminal operation is exact"
    | _ -> failtest "Every synthetic terminal operation must accept with a co-committed settlement."

let private acceptTerminalHistory (core: IClaimsCore) =
    for sequence in 1..1024 do
        acceptOne core sequence

let private assertFreshPreparation (core: IClaimsCore) =
    let request =
        openRequest (Guid.NewGuid()) ("POST-TERMINAL-CAPACITY-" + Guid.NewGuid().ToString("N"))

    match core.Prepare(request, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        Expect.equal
            details.Summary.OperationId
            request.OperationId
            "A distinct pending operation still prepares"

        Expect.equal
            details.Summary.Authority
            RecoveryAuthority.PendingAuthority
            "Only pending work consumes capacity"
    | _ -> failtest "1,024 recent accepted operations must not block a fresh preparation."

let private terminalHistoryDoesNotConsumePendingCapacity =
    testCase
        "[CC-REC-001] 1,024 recent accepted operations leave capacity for a distinct fresh preparation"
        (fun () ->
            use runtime = openRuntime ()
            acceptTerminalHistory runtime.Core
            assertFreshPreparation runtime.Core)

let tests =
    testList "PostgreSQL terminal capacity" [ terminalHistoryDoesNotConsumePendingCapacity ]
