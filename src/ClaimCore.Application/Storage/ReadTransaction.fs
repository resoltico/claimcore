namespace ClaimCore.Application.Storage

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

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
    member this.ForExport =
        let export =
            ReadTx(connection, installation, cipher, observedAt, token, exportSnapshot = true)

        export.Credential <- this.Credential
        export

    member val Credential: (PrincipalKey * VerifiedCredential) option = None with get, set

    member _.ExportSnapshot = defaultArg exportSnapshot false
    member _.Connection = connection
    member _.InstallationId = installation.InstallationId
    member _.Cipher = cipher
    member _.CancellationToken = token
    member val ObservedAt = observedAt with get, set
