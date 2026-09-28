namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

[<NoEquality; NoComparison>]
type internal ExportProof =
    {
        ExportId: Guid
        CaseId: Guid
        Sequence: int64
        Epoch: int64
        EntryHash: byte array
        CandidateDigest: byte array
        Advanced: bool
    }

module internal DataAuditExportWitness =
    let exact cutoff (reader: NpgsqlDataReader) candidateDigest =
        let ordinal name = reader.GetOrdinal(name)
        let sequence = reader.GetInt64(ordinal "witness_sequence")
        let epoch = reader.GetInt64(ordinal "witness_epoch")
        let hash = reader.GetFieldValue<byte array>(ordinal "witness_entry_hash")

        if
            sequence > cutoff
            || sequence <> reader.GetInt64(ordinal "event_sequence")
            || epoch <> reader.GetInt64(ordinal "event_epoch")
            || hash <> reader.GetFieldValue<byte array>(ordinal "event_entry_hash")
        then
            corrupt ()

        {
            ExportId = reader.GetGuid(ordinal "export_id")
            CaseId = reader.GetGuid(ordinal "case_id")
            Sequence = sequence
            Epoch = epoch
            EntryHash = hash
            CandidateDigest = candidateDigest
            Advanced = reader.GetInt64(ordinal "copy_revision") > 1L
        }

    let verify connection transaction witness cutoff (proof: ExportProof) =
        CaseWitnessAuditEvidence.verify
            connection
            transaction
            witness
            cutoff
            proof.CaseId
            proof.ExportId
            proof.Sequence
            proof.Epoch
            proof.EntryHash
            proof.CandidateDigest
            SettledAuthority
