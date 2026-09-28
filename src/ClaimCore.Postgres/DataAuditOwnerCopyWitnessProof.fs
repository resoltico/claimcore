namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Witness
open DataAuditCommon
open WitnessProtocolReconciliation

[<NoEquality; NoComparison>]
type internal OwnerCopyTransitionProof =
    {
        EventId: Guid
        SourceCaseId: Guid option
        Sequence: int64
        Epoch: int64
        EntryHash: byte array
        CandidateDigest: byte array
        ActionCutoffSequence: int64
        ActionCutoffHash: byte array
    }

/// Binds each owner copy transition to the independent witness at the audit cutoff.
module internal DataAuditOwnerCopyWitnessProof =
    let private bytes (reader: NpgsqlDataReader) index = reader.GetFieldValue<byte array>(index)

    let fromRow (reader: NpgsqlDataReader) (next: ManagedCopyTransition) =
        {
            EventId = next.Copy.EventId
            SourceCaseId = next.Copy.SourceCaseId
            Sequence = reader.GetInt64(9)
            Epoch = reader.GetInt64(10)
            EntryHash = bytes reader 11
            CandidateDigest = bytes reader 6
            ActionCutoffSequence = next.ActionWitnessCutoffSequence
            ActionCutoffHash = next.ActionWitnessCutoffHash
        }

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (proof: OwnerCopyTransitionProof)
        =
        task {
            witnessProof (fun () ->
                witness.VerifyHistoricalTip(proof.ActionCutoffSequence, proof.ActionCutoffHash))

            match proof.SourceCaseId with
            | Some caseId ->
                do!
                    CaseWitnessAuditEvidence.verify
                        connection
                        transaction
                        witness
                        cutoff
                        caseId
                        proof.EventId
                        proof.Sequence
                        proof.Epoch
                        proof.EntryHash
                        proof.CandidateDigest
                        SettledAuthority
            | None ->
                witnessProof (fun () ->
                    witness.VerifyAuthorityEvidenceForInstallation(
                        proof.EventId,
                        proof.Sequence,
                        proof.Epoch,
                        proof.EntryHash,
                        proof.CandidateDigest
                    ))
        }
