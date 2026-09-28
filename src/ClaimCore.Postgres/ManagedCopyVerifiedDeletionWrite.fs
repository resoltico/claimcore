namespace ClaimCore.Postgres

open System
open Npgsql
open NpgsqlTypes

/// One owner transaction updates the projection, appends the event, and consumes exact approval.
module internal ManagedCopyVerifiedDeletionWrite =
    let private consumeApproval connection transaction (transition: ManagedCopyTransition) =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copy_deletion_approval_uses "
                    + "(approval_id,deletion_event_id) VALUES (@approval,@event)",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" (transition.DeletionApprovalId |> Option.get)
            Sql.uuid command "event" transition.Copy.EventId
            let! used = command.ExecuteNonQueryAsync()

            if used <> 1 then
                invalidOp "Copy deletion approval was not consumed."
        }

    let apply
        (connection: NpgsqlConnection)
        transaction
        (transition: ManagedCopyTransition)
        canonical
        signature
        (absence: VerifiedManagedCopyDeletionAbsence)
        (intent: WitnessIntent)
        =
        task {
            let eventHash =
                ManagedCopyEventHash.compute transition.PreviousEventHash canonical (Some signature)

            use projection =
                new NpgsqlCommand(
                    "UPDATE claimcore.managed_copies SET state='VERIFIED_DELETED',"
                    + "revision=@revision,event_hash=@hash,deletion_proof_sha256=@proof,"
                    + "last_verified_at=@observed WHERE copy_id=@copy "
                    + "AND revision=@previous AND state='DELETE_PENDING'",
                    connection,
                    transaction
                )

            Sql.integer projection "revision" transition.Revision
            Sql.add projection "hash" NpgsqlDbType.Bytea (box eventHash)
            Sql.add projection "proof" NpgsqlDbType.Bytea (box absence.InspectionReportSha256)
            Sql.add projection "observed" NpgsqlDbType.TimestampTz (box absence.ObservedAt)
            Sql.uuid projection "copy" transition.Copy.CopyId
            Sql.integer projection "previous" (transition.Revision - 1L)
            let! updated = projection.ExecuteNonQueryAsync()

            if updated <> 1 then
                invalidOp "Copy deletion projection was not co-committed."

            do!
                ManagedCopyTransitionAdministration.insertEvent
                    connection
                    transaction
                    transition
                    canonical
                    signature
                    eventHash
                    intent

            do! consumeApproval connection transaction transition
        }
