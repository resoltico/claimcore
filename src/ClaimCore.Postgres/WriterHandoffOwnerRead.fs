namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal PrimaryWriterPreparation =
    {
        Value: WriterHandoffPreparation
        Canonical: byte array
        Signature: byte array
        Intent: Ticket
    }

/// Owner readback of the exact primary PREPARE target; no repair or adoption occurs here.
module internal WriterHandoffOwnerRead =
    let completed (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) handoffId =
        use command =
            new NpgsqlCommand(
                "SELECT settlement_canonical,settlement_signature,"
                + "settlement_sequence,settlement_hash "
                + "FROM claimcore.writer_handoffs WHERE handoff_id=@handoff",
                connection,
                transaction
            )

        Sql.uuid command "handoff" handoffId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            None
        else
            let result =
                reader.GetFieldValue<byte array>(0),
                reader.GetFieldValue<byte array>(1),
                reader.GetInt64(2),
                reader.GetFieldValue<byte array>(3)

            if reader.Read() then
                invalidOp "Primary writer handoff is duplicated."

            Some result

    let preparation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        handoffId
        =
        use command =
            new NpgsqlCommand(
                "SELECT canonical_action,ed25519_signature,candidate_sha256,"
                + "witness_sequence,witness_epoch,witness_entry_hash "
                + "FROM claimcore.writer_handoff_preparations WHERE handoff_id=@handoff",
                connection,
                transaction
            )

        Sql.uuid command "handoff" handoffId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            None
        else
            let canonical = reader.GetFieldValue<byte array>(0)
            let signature = reader.GetFieldValue<byte array>(1)
            let candidate = reader.GetFieldValue<byte array>(2)
            let sequence = reader.GetInt64(3)
            let epoch = reader.GetInt64(4)
            let hash = reader.GetFieldValue<byte array>(5)
            let value = WriterHandoffPreparation.parse canonical

            if reader.Read() then
                invalidOp "Primary writer preparation is duplicated."

            match value with
            | Some proposal when
                proposal.HandoffId = handoffId
                && epoch = witness.Identity.Epoch
                && candidate = Security.Cryptography.SHA256.HashData(canonical)
                ->
                let observed =
                    witness.EvidenceStore.TryReadEvidence(handoffId, Intent)
                    |> Option.defaultWith (fun () -> invalidOp "Witness writer intent is absent.")

                if observed.Ticket.Sequence <> sequence || observed.Ticket.EntryHash <> hash then
                    invalidOp "Primary writer preparation ticket differs."

                Some
                    {
                        Value = proposal
                        Canonical = canonical
                        Signature = signature
                        Intent = observed.Ticket
                    }
            | _ -> invalidOp "Primary writer preparation is invalid."
