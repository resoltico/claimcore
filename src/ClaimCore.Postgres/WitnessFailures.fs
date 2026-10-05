namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open Npgsql

[<RequireQualifiedAccess>]
type internal WitnessFailureCause =
    | PendingEvidence
    | Transport
    | Schema
    | Authority
    | Integrity
    | Unexpected

[<RequireQualifiedAccess>]
type internal WitnessFailureStage =
    | Read
    | Append
    | Settlement

/// Cause is operator evidence, never a determination that a dispatched append did not commit.
module internal WitnessFailures =
    let classify (error: exn) =
        match error with
        | WitnessPending -> WitnessFailureCause.PendingEvidence
        | :? PostgresException as value when value.SqlState = "42501" ->
            WitnessFailureCause.Authority
        | :? PostgresException as value when
            value.SqlState = "42P01" || value.SqlState = "3F000" || value.SqlState = "42703"
            ->
            WitnessFailureCause.Schema
        | :? CryptographicException
        | :? InvalidDataException
        | :? FormatException -> WitnessFailureCause.Integrity
        | :? NpgsqlException
        | :? IOException
        | :? TimeoutException -> WitnessFailureCause.Transport
        | _ -> WitnessFailureCause.Unexpected

    let report observer stage error =
        try
            observer stage (classify error)
        with _ ->
            ()
