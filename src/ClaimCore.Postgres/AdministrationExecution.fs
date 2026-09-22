namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql

/// Per-operation knowledge, not ambient state. Commit confirmation is captured before disposal.
type internal AdministrationProgress<'value>() =
    let mutable started = false
    let mutable commitAttempted = false
    let mutable confirmed: 'value option = None

    member _.BeginWork() =
        if started || commitAttempted then
            invalidOp "Administrative work was already entered."

        started <- true

    member _.Started = started
    member _.CommitAttempted = commitAttempted
    member _.Confirmed = confirmed

    member _.Commit(commit: unit -> unit, value: 'value) =
        if not started || commitAttempted then
            invalidOp "Exactly one commit belongs to an administrative operation."

        commitAttempted <- true
        commit ()
        confirmed <- Some value
        value

module internal AdministrationExecution =
    let private classify =
        function
        | AdministrationException reason -> reason
        | UnsupportedPostgresVersion -> AdministrationFailure.PostgresVersionUnsupported
        | RuntimeDatabaseMismatch -> AdministrationFailure.DatabaseConfigurationInvalid
        | :? ArgumentException -> AdministrationFailure.OwnerConnectionInvalid
        | :? InvalidDataException -> AdministrationFailure.SchemaDefinitionInvalid
        | :? NpgsqlException
        | :? IOException
        | :? TimeoutException -> AdministrationFailure.DatabaseUnavailable
        | _ -> AdministrationFailure.OperationFailed

    let run (action: AdministrationProgress<'value> -> 'value) =
        let progress = AdministrationProgress<'value>()

        try
            action progress |> ignore

            match progress.Confirmed with
            | Some value -> AdministrationOutcome.Completed value
            | None -> AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        with error ->
            match progress.Confirmed with
            | Some result -> AdministrationOutcome.CompletedCleanupFailed result
            | None when progress.CommitAttempted ->
                AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
            | None when progress.Started -> AdministrationOutcome.NotCommitted(classify error)
            | None -> AdministrationOutcome.NotStarted(classify error)
