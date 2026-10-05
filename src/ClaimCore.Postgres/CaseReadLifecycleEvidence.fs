namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading
open System.Security.Cryptography
open System.Text.Json
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

/// Lifecycle events can advance the business revision without changing its thirteen fields.
module internal CaseReadLifecycleEvidence =
    let private require condition =
        if not condition then
            raise (InvalidDataException("Current lifecycle projection differs from its evidence."))

    let private validate
        (reader: System.Data.Common.DbDataReader)
        acceptedRevision
        currentRevision
        =
        let sequence = reader.GetInt64(1)

        if sequence = 0L then
            require (currentRevision = acceptedRevision)
            None
        else
            require (not (reader.IsDBNull(2)))
            let canonical = reader.GetFieldValue<byte array>(5)
            let digest = SHA256.HashData(canonical)
            use document = JsonDocument.Parse(canonical)
            let root = document.RootElement
            let revision = reader.GetInt64(4)

            require (
                root.GetProperty("version").GetInt32() = 1
                && root.GetProperty("kind").GetString() = "CASE_LIFECYCLE_EVENT"
            )

            require (
                root.GetProperty("businessRevision").GetInt64() = revision
                && max acceptedRevision revision = currentRevision
                && root.GetProperty("lifecycleSequence").GetInt64() = sequence
            )

            require (
                digest = reader.GetFieldValue<byte array>(6)
                && reader.GetFieldValue<byte array>(3) =
                    CaseLifecycleCandidate.eventHash
                        (root.GetProperty("previousHash").GetBytesFromBase64())
                        digest
                && root.GetProperty("disposition").GetString() = reader.GetString(10)
                && root.GetProperty("privacyPhase").GetString() = reader.GetString(11)
            )

            Some(
                reader.GetGuid(2),
                reader.GetGuid(0),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetFieldValue<byte array>(9),
                digest
            )

    let verify
        (witness: WitnessProtocol)
        connection
        transaction
        (view: CaseView)
        acceptedRevision
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT c.case_id,c.lifecycle_sequence,l.event_id,c.lifecycle_event_hash,"
                    + "l.business_revision,l.canonical_action,l.candidate_sha256,l.witness_sequence,"
                    + "l.witness_epoch,l.witness_entry_hash,c.disposition,c.privacy_phase "
                    + "FROM claimcore.cases c LEFT JOIN claimcore.case_lifecycle_events l "
                    + "ON l.case_id=c.case_id AND l.lifecycle_sequence=c.lifecycle_sequence "
                    + "WHERE c.case_reference=@reference",
                    connection,
                    transaction
                )

            Sql.text command "reference" view.Fields.CaseReference
            use! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)
            require found
            let evidence = validate reader acceptedRevision view.Version
            let! duplicated = reader.ReadAsync(ct)
            require (not duplicated)
            reader.Close()

            match evidence with
            | None -> return Ok()
            | Some(eventId, caseId, sequence, epoch, hash, digest) ->
                try
                    do!
                        witness.VerifyAuthorityEvidenceForCase(
                            eventId,
                            sequence,
                            epoch,
                            hash,
                            digest,
                            caseId,
                            ct
                        )

                    return Ok()
                with
                | :? OperationCanceledException as error when ct.IsCancellationRequested ->
                    return raise error
                | _ -> return Error CoreFailure.StoreUnavailable
        }
