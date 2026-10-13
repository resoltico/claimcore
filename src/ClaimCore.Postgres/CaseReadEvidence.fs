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
    let history
        witness
        connection
        transaction
        caseId
        (receipts: Receipt list)
        (ct: CancellationToken)
        =
        task {
            try
                do!
                    CaseReadProof.verifyReceipts
                        witness
                        connection
                        transaction
                        (receipts |> List.map _.OperationId)
                        (Some caseId)
                        ct

                return None
            with
            | :? OperationCanceledException as error when ct.IsCancellationRequested ->
                return raise error
            | _ ->
                return
                    receipts
                    |> List.tryHead
                    |> Option.map (fun receipt ->
                        CoreFailure.CommitOutcomeUnknown receipt.OperationId)
        }

    let private priorFor
        (rows: System.Collections.Generic.Dictionary<Guid, WitnessPrimaryMatch.Accepted>)
        (view: CaseView)
        =
        let operation, row =
            rows
            |> Seq.filter (fun row -> row.Value.Reference = view.Fields.CaseReference)
            |> Seq.exactlyOne
            |> fun row -> row.Key, row.Value

        let prior =
            match CaseRecord.decodeSnapshot row.Snapshot with
            | Ok value when CaseRecord.encodeSnapshot value = row.Snapshot -> value
            | _ -> raise (InvalidDataException("Accepted projection encoding is invalid."))

        if
            prior.Fields <> view.Fields
            || prior.Version <> row.Revision
            || prior.Version > view.Version
        then
            raise (InvalidDataException("Current projection differs from accepted evidence."))

        view, prior.Version, operation, row

    let private acceptedPage witness priors (ct: CancellationToken) =
        task {
            let mutable operation = priors |> List.head |> fun (_, _, id, _) -> id

            try
                let! evidence =
                    CaseReadProof.pairs
                        witness
                        (priors
                         |> List.map (fun (_, _, id, _) -> id, ClaimCore.Witness.SettledAccepted))
                        ct

                for _, _, id, row in priors do
                    operation <- id
                    ct.ThrowIfCancellationRequested()

                    CaseReadProof.verifyAccepted
                        witness
                        id
                        row
                        (CaseReadProof.pair evidence id ClaimCore.Witness.SettledAccepted)

                return None
            with
            | :? OperationCanceledException as error when ct.IsCancellationRequested ->
                return raise error
            | _ -> return Some(CoreFailure.CommitOutcomeUnknown operation)
        }

    let private chunk witness connection transaction claims ct =
        task {
            let views = claims |> List.map Claim.view

            let references =
                views |> List.map (fun view -> view.Fields.CaseReference) |> List.toArray

            let! rows = CaseReadProof.loadCurrent connection transaction references ct

            if rows.Count <> claims.Length then
                raise (InvalidDataException("Current projection lacks accepted evidence."))

            let priors = views |> List.map (priorFor rows)

            match! acceptedPage witness priors ct with
            | Some failure -> return Some failure
            | None ->
                let! proof =
                    CaseReadLifecycleEvidence.page
                        witness
                        connection
                        transaction
                        (priors
                         |> List.map (fun (view, revision, _, row) -> view, revision, row.CaseId))
                        ct

                return
                    match proof with
                    | Ok() -> None
                    | Error cause -> Some cause
        }

    /// A current case can require two proof units: acceptance and lifecycle.
    let page witness connection transaction claims (ct: CancellationToken) =
        task {
            let mutable failure = None

            for claims in claims |> List.chunkBySize 4 do
                if failure.IsNone then
                    ct.ThrowIfCancellationRequested()
                    let! result = chunk witness connection transaction claims ct
                    failure <- result

            ct.ThrowIfCancellationRequested()
            return failure
        }

    let current witness connection transaction claim ct =
        task {
            let! failure = page witness connection transaction [ claim ] ct

            return
                match failure with
                | None -> Ok()
                | Some cause -> Error cause
        }
