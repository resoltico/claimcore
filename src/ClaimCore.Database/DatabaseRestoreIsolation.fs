namespace ClaimCore.Database

open System
open Npgsql
open ClaimCore.Postgres

/// Synthetic producer mode cannot be pointed at an adopted or nonloopback installation.
module internal DatabaseRestoreIsolation =
    let requireIsolated (owner: string) (witnessConnection: string) =
        let primary = OwnerConnection.builder owner
        let witness = NpgsqlConnectionStringBuilder(witnessConnection)
        let primaryHost = primary.Host |> Option.ofObj |> Option.defaultValue ""
        let witnessHost = witness.Host |> Option.ofObj |> Option.defaultValue ""
        let primaryDatabase = primary.Database |> Option.ofObj |> Option.defaultValue ""
        let witnessDatabase = witness.Database |> Option.ofObj |> Option.defaultValue ""

        let loopback (host: string) =
            host = "127.0.0.1" || host = "localhost" || host = "::1"

        if
            not (loopback primaryHost && loopback witnessHost)
            || primary.Port = witness.Port
            || not (primaryDatabase.EndsWith("_test", StringComparison.Ordinal))
            || not (witnessDatabase.EndsWith("_test", StringComparison.Ordinal))
        then
            invalidOp "Synthetic restore producer accepts only two isolated loopback test clusters."
