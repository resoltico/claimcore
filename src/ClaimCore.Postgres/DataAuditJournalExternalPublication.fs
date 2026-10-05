namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

/// The prune target's publication marker is bound into the signed v2 target-set digest.
/// It survives removal of encrypted CASE payload and still requires the immutable receipt.
module internal DataAuditJournalExternalPublication =
    let private classifyLive (witness: WitnessProtocol) (ticket: Ticket) ct =
        task {
            let! retained = witness.EvidenceStore.TryReadEvidence(ticket.OperationId, Intent, ct)
            let intent = retained |> Option.defaultWith corrupt

            if
                intent.Ticket.ScopeKind <> Case
                || intent.Ticket.SubjectCaseId <> ticket.SubjectCaseId
                || intent.Ticket.Sequence >= ticket.Sequence
            then
                corrupt ()

            let plain =
                witness.KeyCustody.Decrypt(
                    intent.Ticket.KeyId,
                    witness.AssociatedData(ticket.OperationId, "INTENT"),
                    intent.EncryptedPayload
                )

            try
                try
                    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(plain))
                    let mutable action = Unchecked.defaultof<JsonElement>

                    return
                        document.RootElement.ValueKind = JsonValueKind.Object
                        && document.RootElement.TryGetProperty("action", &action)
                        && action.ValueKind = JsonValueKind.String
                        && action.GetString() = "PUBLISH_EXTERNAL_COPY"
                with :? JsonException ->
                    return false
            finally
                CryptographicOperations.ZeroMemory(plain)
        }

    let command connection transaction =
        let value =
            new NpgsqlCommand(
                "SELECT is_external_publication FROM claimcore.case_erasure_prune_targets "
                + "WHERE case_id=@case AND sequence=@sequence AND operation_id=@operation",
                connection,
                transaction
            )

        Sql.uuid value "case" Guid.Empty
        Sql.integer value "sequence" 0L
        Sql.uuid value "operation" Guid.Empty
        value

    let requiresReceipt
        (command: NpgsqlCommand)
        (witness: WitnessProtocol)
        (ticket: Ticket)
        (ct: CancellationToken)
        =
        task {
            command.Parameters["case"].Value <- ticket.SubjectCaseId.Value
            command.Parameters["sequence"].Value <- ticket.Sequence
            command.Parameters["operation"].Value <- ticket.OperationId
            let! result = command.ExecuteScalarAsync(ct)

            match result with
            | :? bool as marked -> return marked
            | null -> return! classifyLive witness ticket ct
            | _ -> return corrupt ()
        }
