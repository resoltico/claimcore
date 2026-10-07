module ClaimCore.IntegrationTests.RecoveryStateTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures

let private openRuntime () =
    witnessedOpen (appConnection ()) CancellationToken.None
    |> await
    |> Result.defaultWith runtimeOpeningFailure

let private prepare (core: IActorClaimsCore) operationId =
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
            (actorCore runtime)
                .Recovery.Inspect(operationId, None, recoveryPageLimit, CancellationToken.None)
            |> await
        with
        | RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable -> ()
        | _ -> failtest "Missing inspect must use the non-disclosing refusal."

        match
            (actorCore runtime)
                .Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
            |> await
        with
        | RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable -> ()
        | _ -> failtest "Missing export must use the non-disclosing refusal."

        match
            (actorCore runtime).Recovery.Resolve(operationId, digest, CancellationToken.None)
            |> await
        with
        | ResolveOutcome.RefusedBeforeAttempt(None, RecoveryRejection.ResourceUnavailable) -> ()
        | _ -> failtest "Missing resolve must refuse before attempt."

        match
            (actorCore runtime).Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
            |> await
        with
        | RecoveryDismissOutcome.DismissRefused(None, RecoveryRejection.ResourceUnavailable) -> ()
        | _ -> failtest "Missing dismissal must be explicit.")

let private acceptedState =
    testCase
        "[CC-REC-001] accepted receipt permits replay and export but never dismissal"
        (fun () ->
            use runtime = openRuntime ()
            let operationId = Guid.NewGuid()
            let digest = prepare (actorCore runtime) operationId

            match
                (actorCore runtime).Recovery.Resolve(operationId, digest, CancellationToken.None)
                |> await
            with
            | ResolveOutcome.ResolveCompleted(_, _, DefiniteExecution.Accepted _, _) -> ()
            | _ -> failtest "First exact resolution must accept."

            match
                (actorCore runtime).Recovery.Resolve(operationId, digest, CancellationToken.None)
                |> await
            with
            | ResolveOutcome.ResolveObservedAccepted receipt ->
                Expect.equal receipt.OperationId operationId "Original receipt replayed"
            | _ -> failtest "Accepted recovery resolution must not append another attempt."

            match
                (actorCore runtime)
                    .Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
                |> await
            with
            | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact) ->
                Expect.equal artifact.RequestSha256 digest "Exact accepted export"
            | _ -> failtest "Accepted material remains exportable."

            match
                (actorCore runtime)
                    .Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
                |> await
            with
            | RecoveryDismissOutcome.DismissRefused(Some _, refusal) ->
                Expect.equal
                    refusal.Code
                    RecoveryRejectionCode.RecoveryActionUnavailable
                    "No dismissal"
            | _ -> failtest "Accepted operation cannot be dismissed.")

let private dismissedState =
    testCase "[CC-REC-001] dismissed preparation is idempotent and cannot be exported" (fun () ->
        use runtime = openRuntime ()
        let operationId = Guid.NewGuid()
        let digest = prepare (actorCore runtime) operationId

        match
            (actorCore runtime).Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
            |> await
        with
        | RecoveryDismissOutcome.DismissedPreparation _ -> ()
        | _ -> failtest "Unsubmitted preparation must dismiss."

        match
            (actorCore runtime).Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
            |> await
        with
        | RecoveryDismissOutcome.AlreadyDismissedPreparation _ -> ()
        | _ -> failtest "Repeated dismissal must be idempotent."

        match
            (actorCore runtime).Recovery.Resolve(operationId, digest, CancellationToken.None)
            |> await
        with
        | ResolveOutcome.RefusedBeforeAttempt(_, refusal) ->
            Expect.equal refusal.Code RecoveryRejectionCode.OperationRevoked "No attempt"
        | _ -> failtest "Dismissed preparation cannot resolve."

        match
            (actorCore runtime)
                .Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
            |> await
        with
        | RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.OperationRevoked -> ()
        | _ -> failtest "Dismissed material cannot create an export.")

let tests =
    testList "PostgreSQL recovery state table" [ missingState; acceptedState; dismissedState ]
