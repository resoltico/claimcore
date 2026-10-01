namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

/// Recheck claimant-bearing results under the runtime's final primary/witness disclosure fence.
/// Lost disclosure authority cannot change the already established commit or attempt evidence.
module internal ActorMutationDisclosure =
    let commandAction preparing (request: CommandRequest) =
        match request.Command, preparing with
        | Command.Open _, true -> EndpointAction.PrepareNewCase
        | Command.Open _, false -> EndpointAction.ExecuteNewCase
        | _, true -> EndpointAction.PrepareCommand
        | _ -> EndpointAction.ExecuteCommand

    let private require (admission: Task<ActorCallContext option>) : Task =
        task {
            let! allowed =
                task {
                    try
                        let! context = admission
                        return context.IsSome
                    with _ ->
                        return false
                }

            if not allowed then
                invalidOp "Claimant result disclosure is unavailable."
        }

    let private command (gate: IActorGate) principal preparing request =
        gate.Command(principal, commandAction preparing request, request, CancellationToken.None)
        |> require

    let private operation (gate: IActorGate) principal action operationId =
        gate.Operation(principal, action, operationId, CancellationToken.None)
        |> require

    let prepare gate principal request =
        function
        | PrepareOutcome.Prepared _
        | PrepareOutcome.ObservedAccepted _
        | PrepareOutcome.RetainedForRecovery _ -> command gate principal true request
        | PrepareOutcome.PrepareRejected _
        | PrepareOutcome.PrepareFailed _
        | PrepareOutcome.CancelledBeforeAdmission _
        | PrepareOutcome.PreparationStateUnknown _ -> Task.CompletedTask

    let submit gate principal request =
        function
        | SubmissionOutcome.ObservedAccepted _
        | SubmissionOutcome.Completed _
        | SubmissionOutcome.RejectedBeforeAttempt(Some _, _)
        | SubmissionOutcome.FailedBeforeAttempt(Some _, _)
        | SubmissionOutcome.CancelledBeforeAttempt _
        | SubmissionOutcome.AttemptAdmissionUnknown _
        | SubmissionOutcome.AttemptUnresolved _ -> command gate principal false request
        | SubmissionOutcome.RejectedBeforeAttempt(None, _)
        | SubmissionOutcome.FailedBeforeAttempt(None, _)
        | SubmissionOutcome.PreparationStateUnknown _
        | SubmissionOutcome.CancelledBeforeAdmission _ -> Task.CompletedTask

    let resolve gate principal operationId =
        function
        | ResolveOutcome.ResolveObservedAccepted _
        | ResolveOutcome.ResolveCompleted _
        | ResolveOutcome.RefusedBeforeAttempt(Some _, _)
        | ResolveOutcome.ResolveFailedBeforeAttempt(Some _, _)
        | ResolveOutcome.ResolveCancelledBeforeAttempt _
        | ResolveOutcome.ResolveAttemptAdmissionUnknown _
        | ResolveOutcome.ResolveAttemptUnresolved _ ->
            operation gate principal EndpointAction.RecoveryResolve operationId
        | ResolveOutcome.RefusedBeforeAttempt(None, _)
        | ResolveOutcome.ResolveFailedBeforeAttempt(None, _)
        | ResolveOutcome.ResolveCancelledBeforeAdmission _ -> Task.CompletedTask

    let dismiss gate principal operationId =
        function
        | RecoveryDismissOutcome.DismissedPreparation _
        | RecoveryDismissOutcome.AlreadyDismissedPreparation _
        | RecoveryDismissOutcome.DismissRefused(Some _, _) ->
            operation gate principal EndpointAction.RecoveryDismiss operationId
        | RecoveryDismissOutcome.AlreadyRevoked _
        | RecoveryDismissOutcome.DismissNotFound _
        | RecoveryDismissOutcome.DismissRefused(None, _)
        | RecoveryDismissOutcome.DismissFailed _
        | RecoveryDismissOutcome.DismissCancelledBeforeAdmission _
        | RecoveryDismissOutcome.DismissStateUnknown _ -> Task.CompletedTask

    let export gate principal operationId =
        function
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact) ->
            task {
                try
                    do! operation gate principal EndpointAction.RecoveryExport operationId
                with error ->
                    CryptographicOperations.ZeroMemory artifact.Bytes
                    return raise error
            }
            :> Task
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound _)
        | RecoveryQueryOutcome.RecoveryRejected _
        | RecoveryQueryOutcome.RecoveryFailed _
        | RecoveryQueryOutcome.RecoveryCancelled -> Task.CompletedTask

    let retain gate principal =
        function
        | RecoveryImportRetainOutcome.RetainedPreparation details
        | RecoveryImportRetainOutcome.ExistingPreparation details ->
            operation gate principal EndpointAction.RecoveryImportRetain details.Summary.OperationId
        | RecoveryImportRetainOutcome.ObservedAcceptedImport receipt ->
            operation gate principal EndpointAction.RecoveryImportRetain receipt.OperationId
        | RecoveryImportRetainOutcome.ImportRejected _
        | RecoveryImportRetainOutcome.ImportFailed _
        | RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
        | RecoveryImportRetainOutcome.RetainStateUnknown _ -> Task.CompletedTask
