namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

/// Every publication remains an unresolved external-copy liability until an exact ADOPT
/// receipt consumes it. Scan independently of adoption requests so orphan rows fail audit.
module internal DataAuditExternalPublications =
    let hasUnadoptedForCase connection transaction caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.managed_copy_external_publications p "
                    + "WHERE p.case_id=@case AND NOT EXISTS "
                    + "(SELECT 1 FROM claimcore.managed_copies c "
                    + "WHERE c.copy_id=p.copy_id AND c.producer_kind='ADOPTED_EXTERNAL'))",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            let! result = command.ExecuteScalarAsync()
            return unbox<bool> result
        }

    let private page connection transaction after (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT publication_id FROM claimcore.managed_copy_external_publications "
                    + "WHERE publication_id>@after ORDER BY publication_id LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let ids = ResizeArray<Guid>()

            while reader.Read() do
                ids.Add(reader.GetGuid(0))

            return ids |> Seq.toList
        }

    let private requireAdoptedOrigin connection transaction =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.managed_copies c "
                    + "LEFT JOIN claimcore.managed_copy_external_publications p "
                    + "ON p.copy_id=c.copy_id WHERE c.producer_kind='ADOPTED_EXTERNAL' "
                    + "AND (p.publication_id IS NULL OR p.case_id IS DISTINCT FROM c.source_case_id "
                    + "OR p.ciphertext_sha256 IS DISTINCT FROM c.ciphertext_sha256 "
                    + "OR p.ciphertext_bytes IS DISTINCT FROM c.ciphertext_bytes "
                    + "OR p.encryption_key_id IS DISTINCT FROM c.encryption_key_id "
                    + "OR p.location_commitment IS DISTINCT FROM c.location_commitment "
                    + "OR p.custodian_commitment IS DISTINCT FROM c.custodian_commitment))",
                    connection,
                    transaction
                )

            let! result = command.ExecuteScalarAsync()

            if unbox<bool> result then
                corrupt ()
        }

    let private requireUnadoptedPending connection transaction =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.managed_copy_external_publications p "
                    + "JOIN claimcore.case_erasure_tombstones t ON t.case_id=p.case_id "
                    + "WHERE t.phase IN ('PAYLOAD_ERASED_SUPPRESSION_RETAINED','ERASURE_FINAL') "
                    + "AND NOT EXISTS (SELECT 1 FROM claimcore.managed_copies c "
                    + "WHERE c.copy_id=p.copy_id AND c.producer_kind='ADOPTED_EXTERNAL'))",
                    connection,
                    transaction
                )

            let! result = command.ExecuteScalarAsync()

            if unbox<bool> result then
                corrupt ()
        }

    let verifyAll connection transaction (witness: WitnessProtocol) cutoff (ct: CancellationToken) =
        task {
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! ids = page connection transaction after ct

                for publicationId in ids do
                    let! found =
                        ManagedCopyExternalPublicationEvidence.verifyPublication
                            connection
                            transaction
                            witness
                            cutoff
                            publicationId
                            ct

                    if found.IsNone then
                        corrupt ()

                    count <- count + 1L

                match List.tryLast ids with
                | None -> more <- false
                | Some id -> after <- id

            do! requireAdoptedOrigin connection transaction
            do! requireUnadoptedPending connection transaction
            return count
        }
