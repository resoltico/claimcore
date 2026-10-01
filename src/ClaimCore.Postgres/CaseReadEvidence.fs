namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open WitnessProtocolReconciliation

/// Claimant-bearing reads prove one recorded projection without appending under a witness read fence.
module internal CaseReadEvidence =
    let receipt (witness: WitnessProtocol) connection transaction operationId =
        try
            witness.VerifyAccepted(connection, transaction, operationId)
            Ok()
        with _ ->
            Error(CoreFailure.CommitOutcomeUnknown operationId)

    let current witness connection transaction claim =
        let view = Claim.view claim

        use command =
            new NpgsqlCommand(
                "SELECT operation_id,snapshot FROM claimcore.case_changes "
                + "WHERE case_reference=@reference ORDER BY revision DESC LIMIT 1",
                connection,
                transaction
            )

        Sql.text command "reference" view.Fields.CaseReference
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            raise (InvalidDataException("Current projection lacks accepted evidence."))

        let operationId = reader.GetGuid(0)
        let expected = reader.GetFieldValue<byte array>(1)

        let prior =
            match CaseRecord.decodeSnapshot expected with
            | Ok snapshot when CaseRecord.encodeSnapshot snapshot = expected -> snapshot
            | _ -> raise (InvalidDataException("Accepted projection encoding is invalid."))

        if prior.Fields <> view.Fields || prior.Version > view.Version || reader.Read() then
            raise (InvalidDataException("Current projection differs from accepted evidence."))

        reader.Close()

        match receipt witness connection transaction operationId with
        | Error failure -> Error failure
        | Ok() -> CaseReadLifecycleEvidence.verify witness connection transaction view prior.Version

    let page witness connection transaction claims =
        claims
        |> List.tryPick (fun claim ->
            match current witness connection transaction claim with
            | Ok() -> None
            | Error failure -> Some failure)
