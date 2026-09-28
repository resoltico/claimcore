namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application

type private RequestFenceRow =
    {
        KeyId: Guid
        ReferenceDigest: byte array
        EventId: Guid
        CandidateDigest: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        LifecycleSequence: int64
        LifecycleHash: byte array
    }

/// Resolve the exact previously settled erasure request while the caller holds authority
/// and case locks. A marker alone cannot mint the opaque Domain fence proof.
module internal CaseErasureRequestProof =
    let private corrupt () : 'a =
        raise (InvalidDataException("Erasure request fence differs."))

    let private row (reader: Npgsql.NpgsqlDataReader) =
        {
            KeyId = reader.GetGuid(0)
            ReferenceDigest = reader.GetFieldValue<byte array>(1)
            EventId = reader.GetGuid(2)
            CandidateDigest = reader.GetFieldValue<byte array>(3)
            WitnessSequence = reader.GetInt64(4)
            WitnessEpoch = reader.GetInt64(5)
            WitnessHash = reader.GetFieldValue<byte array>(6)
            LifecycleSequence = reader.GetInt64(7)
            LifecycleHash = reader.GetFieldValue<byte array>(8)
        }

    let private find connection transaction caseId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT suppression_key_id,reference_commitment,request_event_id,"
                    + "request_candidate_sha256,request_witness_sequence,request_witness_epoch,"
                    + "request_witness_entry_hash,lifecycle_sequence,lifecycle_hash "
                    + "FROM claimcore.case_erasure_tombstones "
                    + "WHERE case_id=@case AND phase='ERASURE_REQUESTED'",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)

            if not found then
                return None
            else
                let value = row reader
                let! extra = reader.ReadAsync(ct)

                if extra then
                    corrupt ()

                return Some value
        }

    let private verify
        connection
        transaction
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        caseId
        reference
        (value: RequestFenceRow)
        (ct: CancellationToken)
        =
        task {
            commitments.Admit()
            let expected = commitments.Reference reference

            if
                value.KeyId <> commitments.KeyId
                || expected.Length <> 32
                || not (CryptographicOperations.FixedTimeEquals(expected, value.ReferenceDigest))
            then
                corrupt ()

            let! stored =
                CaseLifecycleAuditRows.eventById connection transaction value.EventId ct

            let event = stored |> Option.defaultWith corrupt

            if
                event.CaseId <> caseId
                || event.Reference <> reference
                || event.ActionName <> "REQUEST_ERASURE"
                || event.Sequence <> value.LifecycleSequence
                || event.EventHash <> value.LifecycleHash
                || event.CandidateHash <> value.CandidateDigest
                || event.WitnessSequence <> value.WitnessSequence
                || event.WitnessEpoch <> value.WitnessEpoch
                || event.WitnessHash <> value.WitnessHash
            then
                corrupt ()

            CaseLifecycleAuditEvidence.event
                witness
                Int64.MaxValue
                caseId
                event.PreviousHash
                event.Sequence
                event
            |> ignore

            return value.EventId, Convert.ToHexStringLower value.CandidateDigest
        }

    let read
        connection
        transaction
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        caseId
        reference
        (ct: CancellationToken)
        =
        task {
            let! found = find connection transaction caseId ct

            match found with
            | None -> return None
            | Some value ->
                let! proof =
                    verify connection transaction witness commitments caseId reference value ct

                return Some proof
        }
