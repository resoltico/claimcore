namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness
open ClaimCore.Application
open DataAuditCommon

module internal DataAuditWitness =
    let private verifyRevocationRow (witness: WitnessProtocol) cutoff (reader: NpgsqlDataReader) =
        let operationId = reader.GetGuid(0)
        let sequence = reader.GetInt64(5)

        if sequence > cutoff then
            corrupt ()

        let evidence: RevocationActorEvidence =
            {
                CaseId = reader.GetGuid(2)
                RevokingActorId = reader.GetGuid(3)
                GrantRevision = reader.GetInt64(4)
            }

        let digest =
            witnessProof (fun () ->
                WitnessCandidate.revokedDigestFromEvidence
                    operationId
                    (reader.GetString(1))
                    evidence)

        witnessProof (fun () ->
            witness.VerifyRevokedEvidenceForCase(
                operationId,
                sequence,
                reader.GetInt64(6),
                reader.GetFieldValue<byte array>(7),
                digest,
                evidence.CaseId
            ))

    let verifyRevocations
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        witness
        cutoff
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT operation_id,request_sha256,case_id,revoking_actor_id,grant_revision,witness_sequence,witness_epoch,witness_entry_hash FROM claimcore.operation_revocations ORDER BY operation_id",
                    connection,
                    transaction
                )

            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let mutable count = 0L
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync(cancellationToken)
                reading <- hasRow

                if hasRow then
                    verifyRevocationRow witness cutoff reader
                    count <- count + 1L

            return count
        }

    let private authorityAction (row: ActorAuthorityAuditRow) =
        ActorGrantCandidate.decodeStored
            row.Canonical
            row.Revision
            row.EventId
            row.ActionName
            row.TargetActorId
            row.ApproverActorId
        |> Option.defaultWith corrupt

    let private verifyAuthorityRow
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        expected
        (row: ActorAuthorityAuditRow)
        =
        task {
            let digest = SHA256.HashData(row.Canonical)

            let action = authorityAction row

            if
                row.Revision <> expected
                || row.WitnessSequence > cutoff
                || digest <> row.CandidateHash
            then
                corrupt ()

            match action.Grant with
            | Some { Scope = GrantScope.Case caseId } ->
                do!
                    CaseWitnessAuditEvidence.verify
                        connection
                        transaction
                        witness
                        cutoff
                        caseId
                        row.EventId
                        row.WitnessSequence
                        row.WitnessEpoch
                        row.WitnessHash
                        digest
                        SettledAuthority
            | _ ->
                witnessProof (fun () ->
                    witness.VerifyAuthorityEvidenceForInstallation(
                        row.EventId,
                        row.WitnessSequence,
                        row.WitnessEpoch,
                        row.WitnessHash,
                        digest
                    ))

            return action
        }

    let verifyAuthorityEvents
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        witness
        cutoff
        (cancellationToken: CancellationToken)
        =
        task {
            use tip =
                new NpgsqlCommand(
                    "SELECT revision FROM claimcore.authority_tip WHERE singleton",
                    connection,
                    transaction
                )

            let! expectedTip = tip.ExecuteScalarAsync(cancellationToken)

            let mutable projection = AuthorityHistory.empty
            let mutable more = true

            while more do
                let! rows =
                    DataAuditAuthorityRows.page
                        connection
                        transaction
                        projection.Revision
                        cancellationToken

                for row in rows do
                    let! action =
                        verifyAuthorityRow
                            connection
                            transaction
                            witness
                            cutoff
                            (projection.Revision + 1L)
                            row

                    projection <-
                        AuthorityHistory.apply projection action
                        |> Result.defaultWith (fun _ -> corrupt ())

                more <- rows.Length = 50

            match expectedTip with
            | :? int64 as value when value = projection.Revision -> ()
            | _ -> corrupt ()

            return projection
        }
