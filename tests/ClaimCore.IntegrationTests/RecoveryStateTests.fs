module ClaimCore.IntegrationTests.RecoveryStateTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures

let private openRuntime () =
    Runtime.OpenPostgres(appConnection (), CancellationToken.None)
    |> await
    |> Result.defaultWith (fun _ -> failtest "Synthetic recovery runtime must open.")

let private prepare (core: IClaimsCore) operationId =
    let command = openRequest operationId ("STATE-" + operationId.ToString("N"))

    match core.Prepare(command, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        details.Summary.RequestSha256
        |> Option.defaultWith (fun () -> failtest "Retained digest is required.")
    | _ -> failtest "Synthetic preparation must be retained."

let private missingState =
    testCase "[CC-REC-001] missing preparation has explicit read and action outcomes" (fun () ->
        use runtime = openRuntime ()
        let operationId = Guid.NewGuid()
        let digest = String.replicate 64 "a"

        match
            runtime.Core.Recovery.Inspect(
                operationId,
                None,
                recoveryPageLimit,
                CancellationToken.None
            )
            |> await
        with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound actual) ->
            Expect.equal actual operationId "Missing inspect identity"
        | _ -> failtest "Missing inspect must be explicit."

        match
            runtime.Core.Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
            |> await
        with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound actual) ->
            Expect.equal actual operationId "Missing export identity"
        | _ -> failtest "Missing export must be explicit."

        match
            runtime.Core.Recovery.Resolve(operationId, digest, CancellationToken.None)
            |> await
        with
        | ResolveOutcome.RefusedBeforeAttempt(None, refusal) ->
            Expect.equal refusal.Code RecoveryRejectionCode.PreparationNotFound "No attempt"
        | _ -> failtest "Missing resolve must refuse before attempt."

        match
            runtime.Core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
            |> await
        with
        | RecoveryDismissOutcome.DismissNotFound actual ->
            Expect.equal actual operationId "Missing dismiss identity"
        | _ -> failtest "Missing dismissal must be explicit.")

let private acceptedState =
    testCase
        "[CC-REC-001] accepted receipt permits replay and export but never dismissal"
        (fun () ->
            use runtime = openRuntime ()
            let operationId = Guid.NewGuid()
            let digest = prepare runtime.Core operationId

            match
                runtime.Core.Recovery.Resolve(operationId, digest, CancellationToken.None)
                |> await
            with
            | ResolveOutcome.ResolveCompleted(_, _, DefiniteExecution.Accepted _, _) -> ()
            | _ -> failtest "First exact resolution must accept."

            match
                runtime.Core.Recovery.Resolve(operationId, digest, CancellationToken.None)
                |> await
            with
            | ResolveOutcome.ResolveObservedAccepted receipt ->
                Expect.equal receipt.OperationId operationId "Original receipt replayed"
            | _ -> failtest "Accepted recovery resolution must not append another attempt."

            match
                runtime.Core.Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
                |> await
            with
            | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact) ->
                Expect.equal artifact.RequestSha256 digest "Exact accepted export"
            | _ -> failtest "Accepted material remains exportable."

            match
                runtime.Core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
                |> await
            with
            | RecoveryDismissOutcome.DismissRefused(Some _, refusal) ->
                Expect.equal
                    refusal.Code
                    RecoveryRejectionCode.RecoveryActionUnavailable
                    "No dismissal"
            | _ -> failtest "Accepted operation cannot be dismissed.")

let private dismissedState =
    testCase "[CC-REC-001] dismissed preparation is idempotent and export-only" (fun () ->
        use runtime = openRuntime ()
        let operationId = Guid.NewGuid()
        let digest = prepare runtime.Core operationId

        match
            runtime.Core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
            |> await
        with
        | RecoveryDismissOutcome.DismissedPreparation _ -> ()
        | _ -> failtest "Unsubmitted preparation must dismiss."

        match
            runtime.Core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
            |> await
        with
        | RecoveryDismissOutcome.AlreadyDismissedPreparation _ -> ()
        | _ -> failtest "Repeated dismissal must be idempotent."

        match
            runtime.Core.Recovery.Resolve(operationId, digest, CancellationToken.None)
            |> await
        with
        | ResolveOutcome.RefusedBeforeAttempt(_, refusal) ->
            Expect.equal refusal.Code RecoveryRejectionCode.OperationRevoked "No attempt"
        | _ -> failtest "Dismissed preparation cannot resolve."

        match
            runtime.Core.Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
            |> await
        with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found _) -> ()
        | _ -> failtest "Dismissed material remains exportable.")

let tests =
    testList "PostgreSQL recovery state table" [ missingState; acceptedState; dismissedState ]
