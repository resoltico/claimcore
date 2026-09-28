namespace ClaimCore.Postgres

open System.Threading
open Npgsql
open ClaimCore.Application
open DataAuditCommon

/// Resolves an approval target from an owner registration or verified adoption custody.
module internal ManagedCopyDeletionApprovalTarget =
    let private projection
        connection
        transaction
        (request: CopyDeletionApprovalRequest)
        (origin: VerifiedCopyAdoptionOrigin)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT c.revision,c.state,c.retain_until,v.holder_actor_id,"
                    + "v.active,v.signer_purpose FROM claimcore.managed_copies c "
                    + "JOIN claimcore.managed_copy_signers v ON v.signing_key_id=@verifier "
                    + "WHERE c.copy_id=@copy AND c.source_case_id=@case "
                    + "AND c.producer_kind=@producer",
                    connection,
                    transaction
                )

            Sql.uuid command "verifier" request.VerifierSigningKeyId
            Sql.uuid command "copy" origin.CopyId
            Sql.uuid command "case" origin.CaseId
            Sql.text command "producer" origin.ProducerKind
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let result: CopyDeletionTarget =
                    {
                        SourceCaseId = Some origin.CaseId
                        Revision = reader.GetInt64(0)
                        State = reader.GetString(1)
                        LocationCommitment = origin.LocationCommitment
                        RetainUntil = reader.GetFieldValue<System.DateTimeOffset>(2)
                        CopyHolderActorId = origin.CustodianHolderActorId
                        VerifierHolderActorId = reader.GetGuid(3)
                        VerifierActive = reader.GetBoolean(4)
                        VerifierPurpose =
                            ManagedCopySignerCandidate.purposeOfName (reader.GetString(5))
                    }

                return if reader.Read() then None else Some result
        }

    let private adopted
        connection
        transaction
        (witness: WitnessProtocol)
        (request: CopyDeletionApprovalRequest)
        =
        task {
            try
                let! found =
                    ManagedCopyAdoptionEvidence.verifyOrigin
                        connection
                        transaction
                        witness
                        request.WitnessCutoffSequence
                        request.CopyId
                        CancellationToken.None

                match found with
                | None -> return None
                | Some origin ->
                    do!
                        DataAuditAdoptedCopyTransitions.verify
                            connection
                            transaction
                            witness
                            request.WitnessCutoffSequence
                            origin
                            CancellationToken.None

                    return! projection connection transaction request origin
            with _ ->
                return None
        }

    let read connection transaction witness request =
        task {
            let! owner = ManagedCopyDeletionApprovalPolicy.target connection transaction request

            match owner with
            | Some copy -> return Some copy
            | None -> return! adopted connection transaction witness request
        }
