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

    let private commandFor connection transaction (subjects: (CaseView * int64 * Guid) list) =
        let command =
            new NpgsqlCommand(
                "SELECT c.case_id,c.lifecycle_sequence,l.event_id,CASE WHEN octet_length(c.lifecycle_event_hash)=32 THEN c.lifecycle_event_hash END,"
                + "l.business_revision,CASE WHEN octet_length(l.canonical_action) BETWEEN 1 AND 16384 THEN l.canonical_action END,"
                + "CASE WHEN octet_length(l.candidate_sha256)=32 THEN l.candidate_sha256 END,l.witness_sequence,l.witness_epoch,CASE WHEN octet_length(l.witness_entry_hash)=32 THEN l.witness_entry_hash END,c.disposition,c.privacy_phase,c.case_reference "
                + "FROM claimcore.cases c LEFT JOIN claimcore.case_lifecycle_events l "
                + "ON l.case_id=c.case_id AND l.lifecycle_sequence=c.lifecycle_sequence "
                + "WHERE c.case_reference=ANY(@subjects) ORDER BY c.case_reference",
                connection,
                transaction
            )

        command.Parameters.AddWithValue(
            "subjects",
            subjects
            |> List.map (fun (view, _, _) -> view.Fields.CaseReference)
            |> List.toArray
        )
        |> ignore

        command

    let private load
        connection
        transaction
        (subjects: (CaseView * int64 * Guid) list)
        (ct: CancellationToken)
        =
        task {
            if subjects.IsEmpty || subjects.Length > 8 then
                invalidArg (nameof subjects) "Lifecycle proof chunk is outside its bounds."

            let revisions =
                subjects
                |> List.map (fun (view, accepted, caseId) ->
                    view.Fields.CaseReference, (view.Version, accepted, caseId))
                |> Map.ofList

            use command = commandFor connection transaction subjects
            use! reader = command.ExecuteReaderAsync(ct)
            let proofs = ResizeArray<_>()
            let mutable seen = Set.empty
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)

                if not found then
                    reading <- false
                else
                    let reference = reader.GetString(12)
                    require (not (seen.Contains reference))
                    seen <- seen.Add reference
                    let current, accepted, caseId = revisions[reference]
                    require (reader.GetGuid(0) = caseId)

                    if reader.GetInt64(1) <> 0L then
                        require (not (reader.IsDBNull(5)))

                    validate reader accepted current |> Option.iter proofs.Add

            require (seen.Count = subjects.Length)
            reader.Close()

            return proofs |> Seq.toList
        }

    let page (witness: WitnessProtocol) connection transaction subjects ct =
        task {
            let! proofs = load connection transaction subjects ct
            ct.ThrowIfCancellationRequested()

            try
                for chunk in proofs |> List.chunkBySize 8 do
                    let! evidence =
                        CaseReadProof.pairs
                            witness
                            (chunk
                             |> List.map (fun (eventId, _, _, _, _, _) ->
                                 eventId, ClaimCore.Witness.SettledAuthority))
                            ct

                    for eventId, caseId, sequence, epoch, hash, digest in chunk do
                        ct.ThrowIfCancellationRequested()

                        let pair =
                            CaseReadProof.pair evidence eventId ClaimCore.Witness.SettledAuthority

                        let tickets =
                            WitnessProof.verifyLoaded
                                witness.KeyCustody
                                witness.Identity
                                eventId
                                ClaimCore.Witness.SettledAuthority
                                sequence
                                epoch
                                hash
                                digest
                                pair

                        WitnessProof.requireScope (Some caseId) tickets

                ct.ThrowIfCancellationRequested()
                return Ok()
            with
            | :? OperationCanceledException as error when ct.IsCancellationRequested ->
                return raise error
            | _ -> return Error CoreFailure.StoreUnavailable
        }
