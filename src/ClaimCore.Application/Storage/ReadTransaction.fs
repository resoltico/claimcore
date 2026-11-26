namespace ClaimCore.Application.Storage

open System
open System.Threading
open Npgsql
open ClaimCore.Application

/// A provider snapshot and its supplied observation instant, scoped to one store callback.
type internal ReadTx
    (
        connection: NpgsqlConnection,
        installation: Installation,
        cipher: InstallationCipher,
        observedAt: DateTimeOffset,
        token: CancellationToken,
        ?exportSnapshot: bool
    ) =
    member _.ForExport =
        ReadTx(connection, installation, cipher, observedAt, token, exportSnapshot = true)

    member _.ExportSnapshot = defaultArg exportSnapshot false
    member _.Connection = connection
    member _.InstallationId = installation.InstallationId
    member _.CaseReferencePolicy = installation.CaseReferencePolicy
    member _.Cipher = cipher
    member _.CancellationToken = token
    member val ObservedAt = observedAt with get, set
