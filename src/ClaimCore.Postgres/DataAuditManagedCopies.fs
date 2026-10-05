namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open Npgsql
open ClaimCore.Witness
open ClaimCore.RecordFormat
open DataAuditCommon

/// A product-generated export is one witnessed immutable artifact and one co-committed managed
/// copy registration. Owner-attested copies remain inadmissible until their signed transition
/// chain and custody evidence are independently verified.
module internal DataAuditManagedCopies =
    let private matchesIdentity (witness: WitnessProtocol) (reader: NpgsqlDataReader) =
        let uuid name = reader.GetGuid(reader.GetOrdinal(name))
        let exportId = uuid "export_id"

        [
            uuid "copy_id" = exportId
            uuid "product_export_id" = exportId
            uuid "event_id" = exportId
            uuid "event_copy_id" = exportId
            uuid "source_case_id" = uuid "case_id"
            uuid "installation_id" = witness.Identity.InstallationId
            uuid "lineage_id" = witness.Identity.LineageId
            reader.GetInt64(reader.GetOrdinal("copy_epoch")) = witness.Identity.Epoch
        ]
        |> List.forall id

    let private matchesClassification (reader: NpgsqlDataReader) =
        let text name =
            reader.GetString(reader.GetOrdinal(name))

        let number name =
            reader.GetInt64(reader.GetOrdinal(name))

        let absent name =
            reader.IsDBNull(reader.GetOrdinal(name))

        [
            text "producer_kind" = "PRODUCT_EXPORT"
            text "event_producer" = "PRODUCT_EXPORT"
            text "copy_kind" = "EXPORT"
            text "cluster_name" = "NONE"
            text "event_kind" = "REGISTER"
            number "event_revision" = 1L
            absent "signing_key_id"
            absent "ed25519_signature"
        ]
        |> List.forall id

    let private initialProjection (reader: NpgsqlDataReader) =
        reader.GetInt64(reader.GetOrdinal("copy_revision")) = 1L
        && reader.GetString(reader.GetOrdinal("state")) = "UNKNOWN"
        && reader.IsDBNull(reader.GetOrdinal("last_verified_at"))
        && reader.IsDBNull(reader.GetOrdinal("verification_proof_sha256"))
        && reader.IsDBNull(reader.GetOrdinal("deletion_proof_sha256"))

    let private matchesEvidence
        (reader: NpgsqlDataReader)
        canonical
        artifactDigest
        candidateDigest
        =
        let bytes name =
            reader.GetFieldValue<byte array>(reader.GetOrdinal(name))

        let uuid name = reader.GetGuid(reader.GetOrdinal(name))

        let moment name =
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(name))

        let number name =
            reader.GetInt64(reader.GetOrdinal(name))

        [
            bytes "ciphertext_sha256" = artifactDigest
            reader.GetInt64(reader.GetOrdinal("ciphertext_bytes")) =
                int64 (bytes "artifact_bytes").Length
            uuid "encryption_key_id" = uuid "key_id"
            moment "captured_at" = moment "issued_at"
            (number "copy_revision" > 1L || moment "retain_until" = moment "expires_at")
            bytes "canonical_attestation" = canonical
            bytes "event_candidate" = candidateDigest
            bytes "previous_hash" = Array.zeroCreate<byte> 32
        ]
        |> List.forall id

    let private exactProjection
        (witness: WitnessProtocol)
        (reader: NpgsqlDataReader)
        canonical
        artifactDigest
        candidateDigest
        =
        if
            not (matchesIdentity witness reader)
            || not (matchesClassification reader)
            || not (
                reader.GetInt64(reader.GetOrdinal("copy_revision")) > 1L
                || initialProjection reader
            )
            || not (matchesEvidence reader canonical artifactDigest candidateDigest)
        then
            corrupt ()

        let hash = ManagedCopyEventHash.compute (Array.zeroCreate<byte> 32) canonical None

        let bytes name =
            reader.GetFieldValue<byte array>(reader.GetOrdinal(name))

        if
            bytes "event_hash" <> hash
            || (initialProjection reader && bytes "copy_hash" <> hash)
        then
            corrupt ()

    let private purgedTiming (reader: NpgsqlDataReader) =
        let ordinal name = reader.GetOrdinal(name)
        let number name = reader.GetInt64(ordinal name)

        not (reader.IsDBNull(ordinal "purge_event_id"))
        && reader.GetString(ordinal "erasure_phase") = "ERASURE_PENDING"
        && number "purge_witness_cutoff_sequence" >= number "witness_sequence"
        && (number "copy_revision" > 1L
            || reader.GetFieldValue<DateTimeOffset>(ordinal "retain_until") =
                reader.GetFieldValue<DateTimeOffset>(ordinal "expires_at"))

    let private purgedProjection
        (witness: WitnessProtocol)
        (reader: NpgsqlDataReader)
        canonical
        artifactDigest
        candidateDigest
        =
        let ordinal name = reader.GetOrdinal(name)

        let bytes name =
            reader.GetFieldValue<byte array>(ordinal name)

        let number name = reader.GetInt64(ordinal name)

        if
            not (matchesIdentity witness reader)
            || not (matchesClassification reader)
            || not (number "copy_revision" > 1L || initialProjection reader)
            || not (purgedTiming reader)
            || bytes "ciphertext_sha256" <> artifactDigest
            || reader.GetGuid(ordinal "encryption_key_id") <> reader.GetGuid(ordinal "key_id")
            || reader.GetFieldValue<DateTimeOffset>(ordinal "captured_at")
               <> reader.GetFieldValue<DateTimeOffset>(ordinal "issued_at")
            || bytes "event_candidate" <> candidateDigest
            || bytes "previous_hash" <> Array.zeroCreate<byte> 32
            || bytes "canonical_attestation" <> canonical
        then
            corrupt ()

        let expectedHash =
            ManagedCopyEventHash.compute (Array.zeroCreate<byte> 32) canonical None

        if
            bytes "event_hash" <> expectedHash
            || (initialProjection reader && bytes "copy_hash" <> expectedHash)
        then
            corrupt ()

    let rec private page
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        after
        (cancellationToken: CancellationToken)
        =
        task {
            use command = new NpgsqlCommand(DataAuditExportSql.query, connection, transaction)
            Sql.uuid command "after" after
            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let proofs = ResizeArray<ExportProof>()
            let mutable previous = after
            let mutable reading = true
            let mutable more = false

            while reading do
                let! found = reader.ReadAsync(cancellationToken)
                reading <- found

                if found then
                    let exportId = reader.GetGuid(reader.GetOrdinal("export_id"))

                    if exportId = previous then
                        corrupt ()

                    if proofs.Count = 50 then
                        more <- true
                        reading <- false
                    else
                        let proof = validateRow witness cutoff reader
                        proofs.Add proof
                        previous <- exportId

            return proofs |> Seq.toList, previous, more
        }

    and private validateRow (witness: WitnessProtocol) cutoff (reader: NpgsqlDataReader) =
        let ordinal name = reader.GetOrdinal(name)

        if reader.IsDBNull(ordinal "copy_id") || reader.IsDBNull(ordinal "event_id") then
            corrupt ()

        let payloadAbsent = reader.IsDBNull(ordinal "artifact_bytes")
        let actionAbsent = reader.IsDBNull(ordinal "canonical_action")

        if payloadAbsent <> actionAbsent then
            corrupt ()

        let canonical, artifactDigest, candidateDigest =
            if payloadAbsent then
                DataAuditExportCandidates.purgedCandidate reader
            else
                DataAuditExportCandidates.exactCandidate reader

        if payloadAbsent then
            purgedProjection witness reader canonical artifactDigest candidateDigest
        else
            exactProjection witness reader canonical artifactDigest candidateDigest

        DataAuditExportWitness.exact cutoff reader candidateDigest

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (cancellationToken: CancellationToken)
        =
        task {
            do!
                DataAuditExportIntegrity.rejectOrphanProductEvents
                    connection
                    transaction
                    cancellationToken

            do! DataAuditExportBudgets.verify connection transaction cancellationToken
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! proofs, last, next =
                    page connection transaction witness cutoff after cancellationToken

                for proof in proofs do
                    do!
                        DataAuditExportWitness.verify
                            connection
                            transaction
                            witness
                            cutoff
                            proof
                            cancellationToken

                    do!
                        DataAuditExportWitness.verifyAdoption
                            connection
                            transaction
                            witness
                            cutoff
                            proof
                            cancellationToken

                count <- count + int64 proofs.Length
                after <- last
                more <- next

            return count
        }
