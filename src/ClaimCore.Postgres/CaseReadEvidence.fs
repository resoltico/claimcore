namespace ClaimCore.Postgres

open System
open System.Threading
open System.IO
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open WitnessProtocolReconciliation

/// Claimant-bearing reads prove one recorded projection without appending under a witness read fence.
module internal CaseReadEvidence =
    let receipt (witness: WitnessProtocol) connection transaction operationId ct =
        task {
            try
                do! witness.VerifyAccepted(connection, transaction, operationId, ct)
                return Ok()
            with
            | :? OperationCanceledException as error when ct.IsCancellationRequested ->
                return raise error
            | _ -> return Error(CoreFailure.CommitOutcomeUnknown operationId)
        }

    let current witness connection transaction claim (ct: CancellationToken) =
        task {
            let view = Claim.view claim

            use command =
                new NpgsqlCommand(
                    "SELECT operation_id,snapshot FROM claimcore.case_changes "
                    + "WHERE case_reference=@reference ORDER BY revision DESC LIMIT 1",
                    connection,
                    transaction
                )

            Sql.text command "reference" view.Fields.CaseReference
            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                raise (InvalidDataException("Current projection lacks accepted evidence."))

            let operationId = reader.GetGuid(0)
            let expected = reader.GetFieldValue<byte array>(1)

            let prior =
                match CaseRecord.decodeSnapshot expected with
                | Ok snapshot when CaseRecord.encodeSnapshot snapshot = expected -> snapshot
                | _ -> raise (InvalidDataException("Accepted projection encoding is invalid."))

            let! duplicated = reader.ReadAsync(ct)

            if prior.Fields <> view.Fields || prior.Version > view.Version || duplicated then
                raise (InvalidDataException("Current projection differs from accepted evidence."))

            reader.Close()

            let! proof = receipt witness connection transaction operationId ct

            match proof with
            | Error failure -> return Error failure
            | Ok() ->
                return!
                    CaseReadLifecycleEvidence.verify
                        witness
                        connection
                        transaction
                        view
                        prior.Version
                        ct
        }

    let page witness connection transaction claims ct =
        task {
            let mutable failure = None

            for claim in claims do
                if failure.IsNone then
                    let! result = current witness connection transaction claim ct

                    match result with
                    | Ok() -> ()
                    | Error cause -> failure <- Some cause

            return failure
        }
