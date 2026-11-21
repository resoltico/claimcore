namespace ClaimCore.Application.Storage

open System
open System.Threading
open Npgsql

/// A provider snapshot and its supplied observation instant, scoped to one store callback.
type internal ReadTx
    (
        connection: NpgsqlConnection,
        installation: Guid,
        cipher: InstallationCipher,
        observedAt: DateTimeOffset,
        token: CancellationToken
    ) =
    member _.Connection = connection
    member _.InstallationId = installation
    member _.Cipher = cipher
    member _.CancellationToken = token
    member val ObservedAt = observedAt with get, set
