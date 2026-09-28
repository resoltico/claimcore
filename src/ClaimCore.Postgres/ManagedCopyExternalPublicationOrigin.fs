namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

/// A historical tip is not an external-copy origin by itself. Resolve the exact signed,
/// CASE-witnessed publication and the later erasure fence for every consumer.
module internal ManagedCopyExternalPublicationOrigin =
    let private fence connection transaction caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT request_witness_sequence,created_at "
                    + "FROM claimcore.case_erasure_tombstones WHERE case_id=@case",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let result = reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1)
                return if reader.Read() then None else Some result
        }

    let verify
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (request: CopyAdoptionApprovalRequest)
        (ct: CancellationToken)
        =
        task {
            match request.Origin with
            | CopyAdoptionOrigin.ProductExport _ -> return false
            | CopyAdoptionOrigin.AdoptedExternal(sequence, hash) ->
                let! found =
                    ManagedCopyExternalPublicationEvidence.verifyCopy
                        connection
                        transaction
                        witness
                        cutoff
                        request.CopyId
                        ct

                let! erasure = fence connection transaction request.CaseId

                match found, erasure with
                | Some published, Some(fenceSequence, requestedAt) ->
                    return
                        published.CaseId = request.CaseId
                        && published.WitnessSequence = sequence
                        && published.WitnessEntryHash = hash
                        && published.WitnessSequence < fenceSequence
                        && published.CapturedAt < requestedAt
                        && published.CiphertextSha256 = request.CiphertextSha256
                        && published.CiphertextBytes = request.CiphertextBytes
                        && published.CapturedAt = request.CapturedAt
                        && published.RetainUntil <= request.RetainUntil
                        && published.LocationCommitment = request.LocationCommitment
                        && published.CustodianCommitment = request.CustodianCommitment
                | _ -> return false
        }
