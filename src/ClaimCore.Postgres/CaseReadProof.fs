namespace ClaimCore.Postgres

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

/// Acquisition is bounded separately from verification. Only already-authorized subjects enter.
module internal CaseReadProof =
    let private acceptedColumns =
        "case_reference,revision,CASE WHEN octet_length(canonical_request) BETWEEN 1 AND 65536 THEN canonical_request END,"
        + "CASE WHEN octet_length(snapshot) BETWEEN 1 AND 65536 THEN snapshot END,"
        + "effective_business_date - DATE '2000-01-01',observed_utc_instant,rule_revision,witness_sequence,witness_epoch,"
        + "CASE WHEN octet_length(witness_entry_hash)=32 THEN witness_entry_hash END,case_id,"
        + "preparer_actor_id,importer_actor_id,submitter_actor_id,resolver_actor_id,accepted_actor_id,grant_revision,operation_id"

    let private loadAccepted
        (connection: NpgsqlConnection)
        transaction
        selector
        parameter
        values
        (ct: CancellationToken)
        =
        task {
            if Array.length values > 8 then
                invalidArg (nameof values) "Primary proof chunk exceeds eight units."

            use command =
                new NpgsqlCommand(
                    "SELECT "
                    + selector
                    + acceptedColumns
                    + " FROM claimcore.case_changes WHERE "
                    + parameter
                    + " ORDER BY case_reference,revision DESC",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("subjects", values) |> ignore
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = Dictionary<Guid, WitnessPrimaryMatch.Accepted>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)

                if not found then
                    reading <- false
                else
                    if reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(9) then
                        raise (InvalidDataException("Primary proof exceeds its encoded bounds."))

                    let operation = reader.GetGuid(17)
                    let row = WitnessPrimaryMatch.readAccepted reader

                    if rows.Count >= values.Length || not (rows.TryAdd(operation, row)) then
                        raise WitnessPending

            return rows
        }

    let loadOperations connection transaction (operations: Guid array) ct =
        loadAccepted connection transaction "" "operation_id=ANY(@subjects)" operations ct

    let loadCurrent connection transaction (references: string array) ct =
        loadAccepted
            connection
            transaction
            "DISTINCT ON (case_reference) "
            "case_reference=ANY(@subjects)"
            references
            ct

    let pairs (witness: WitnessProtocol) subjects ct =
        witness.EvidenceStore.ReadEvidenceBatch(subjects, ct)

    let pair (rows: Dictionary<Guid * Phase, Evidence>) operation settlement =
        match rows.TryGetValue((operation, Intent)), rows.TryGetValue((operation, settlement)) with
        | (true, intent), (true, outcome) -> intent, outcome
        | _ -> raise WitnessPending

    let verifyAccepted
        (witness: WitnessProtocol)
        operation
        (row: WitnessPrimaryMatch.Accepted)
        (evidence: Evidence * Evidence)
        =
        let intent, _ = evidence

        let plain =
            witness.KeyCustody.Decrypt(
                intent.Ticket.KeyId,
                witness.AssociatedData(operation, "INTENT"),
                intent.EncryptedPayload
            )

        try
            use document = JsonDocument.Parse(plain)

            if
                not (WitnessPrimaryMatch.accepted operation intent.Ticket document.RootElement row)
            then
                raise WitnessPending

            let tickets =
                WitnessProof.verifyLoaded
                    witness.KeyCustody
                    witness.Identity
                    operation
                    SettledAccepted
                    row.Sequence
                    row.Epoch
                    row.Hash
                    (SHA256.HashData plain)
                    evidence

            WitnessProof.requireScope (Some row.CaseId) tickets
        finally
            CryptographicOperations.ZeroMemory plain

    let verifyReceipts
        witness
        connection
        transaction
        operations
        (expectedSubject: Guid option)
        (ct: CancellationToken)
        =
        task {
            for chunk in operations |> List.chunkBySize 8 do
                ct.ThrowIfCancellationRequested()

                let! rows = loadOperations connection transaction (List.toArray chunk) ct

                let! evidence =
                    pairs
                        witness
                        (chunk |> List.map (fun operation -> operation, SettledAccepted))
                        ct

                for operation in chunk do
                    ct.ThrowIfCancellationRequested()

                    match rows.TryGetValue operation with
                    | true, row ->
                        if expectedSubject.IsSome && expectedSubject <> Some row.CaseId then
                            raise WitnessPending

                        verifyAccepted
                            witness
                            operation
                            row
                            (pair evidence operation SettledAccepted)
                    | _ -> raise WitnessPending

            ct.ThrowIfCancellationRequested()
        }
