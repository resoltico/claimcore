namespace ClaimCore.Postgres

open System
open Npgsql

/// One bounded journal scan owns all of its prepared primary lookup commands.
[<NoEquality; NoComparison>]
type internal DataAuditJournalCommands =
    {
        Accepted: NpgsqlCommand
        Revoked: NpgsqlCommand
        Authority: NpgsqlCommand
        ExternalPublication: NpgsqlCommand
        ExternalPublicationMarker: NpgsqlCommand
        Technical: NpgsqlCommand
        Terminal: NpgsqlCommand
        SealedCase: NpgsqlCommand
        Pruned: NpgsqlCommand
        Abort: NpgsqlCommand
    }

    interface IDisposable with
        member this.Dispose() =
            for command in
                [
                    this.Accepted
                    this.Revoked
                    this.Authority
                    this.ExternalPublication
                    this.ExternalPublicationMarker
                    this.Technical
                    this.Terminal
                    this.SealedCase
                    this.Pruned
                    this.Abort
                ] do
                command.Dispose()

module internal DataAuditJournalCommands =
    let private sealedCase connection transaction =
        let command =
            new NpgsqlCommand(DataAuditJournalQueries.sealedCase, connection, transaction)

        try
            Sql.uuid command "case" Guid.Empty
            Sql.integer command "sequence" 0L
            command
        with _ ->
            command.Dispose()
            reraise ()

    let create connection transaction =
        let tracked = ResizeArray<NpgsqlCommand>()

        let keep (command: NpgsqlCommand) =
            tracked.Add command
            command

        let keyed sql =
            DataAuditJournalQueries.keyed connection transaction sql |> keep

        try
            {
                Accepted =
                    keyed
                        "SELECT EXISTS (SELECT 1 FROM claimcore.case_changes WHERE operation_id=@operation)"
                Revoked =
                    keyed
                        "SELECT EXISTS (SELECT 1 FROM claimcore.operation_revocations WHERE operation_id=@operation)"
                Authority = keyed DataAuditJournalQueries.authority
                ExternalPublication =
                    keyed (
                        "SELECT EXISTS (SELECT 1 FROM claimcore.managed_copy_external_publications "
                        + "WHERE publication_id=@operation)"
                    )
                ExternalPublicationMarker =
                    DataAuditJournalExternalPublication.command connection transaction |> keep
                Technical = keyed DataAuditJournalQueries.technical
                Terminal = keyed DataAuditJournalQueries.terminal
                SealedCase = sealedCase connection transaction |> keep
                Pruned = DataAuditJournalPruned.command connection transaction |> keep
                Abort = DataAuditJournalAbort.command connection transaction |> keep
            }
        with _ ->
            for command in tracked do
                command.Dispose()

            reraise ()
