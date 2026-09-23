namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open PreparationData

/// Coordinates the append-only lifecycle marker for technical request recovery.
module internal PreparationLifecycleStore =
    [<RequireQualifiedAccess; NoEquality; NoComparison>]
    type SubmissionLifecycle =
        | Started of RetainedPreparation
        | AlreadyStarted of RetainedPreparation
        | Dismissed of RetainedPreparation

    let private observeMarker
        connection
        transaction
        operationId
        state
        missingMessage
        started
        observed
        =
        task {
            let! recordedAt = insertMarker connection transaction operationId state

            match recordedAt with
            | Some value -> return started value
            | None ->
                let! preparation = readHeader connection (Some transaction) operationId

                match preparation with
                | Some value -> return observed value
                | None -> return raise (InvalidDataException(missingMessage))
        }

    let admitSubmission
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operationId
        (preparation: RetainedPreparation)
        : Task<SubmissionLifecycle> =
        match preparation.Lifecycle with
        | PreparationLifecycle.Unsubmitted ->
            observeMarker
                connection
                transaction
                operationId
                "SUBMISSION_STARTED"
                "Preparation disappeared during lifecycle admission."
                (fun recordedAt ->
                    SubmissionLifecycle.Started
                        { preparation with
                            Lifecycle = PreparationLifecycle.SubmissionStarted recordedAt
                        })
                (fun value ->
                    match value.Lifecycle with
                    | PreparationLifecycle.SubmissionStarted _ ->
                        SubmissionLifecycle.AlreadyStarted value
                    | PreparationLifecycle.Dismissed _ -> SubmissionLifecycle.Dismissed value
                    | PreparationLifecycle.Unsubmitted ->
                        invalidOp "An existing lifecycle marker cannot be unsubmitted.")
        | PreparationLifecycle.SubmissionStarted _ ->
            Task.FromResult(SubmissionLifecycle.AlreadyStarted preparation)
        | PreparationLifecycle.Dismissed _ ->
            Task.FromResult(SubmissionLifecycle.Dismissed preparation)
