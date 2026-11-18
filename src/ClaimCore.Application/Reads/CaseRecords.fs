namespace ClaimCore.Application.Reads

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage

module internal CaseRecords =
    let validateSelector =
        function
        | CaseSelector.Id _ -> Ok()
        | CaseSelector.Reference raw ->
            CaseReference.parse raw
            |> Result.map ignore
            |> Result.mapError (fun violation ->
                Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation))

    let authorize principal access capability (record: StoredCase option) =
        record
        |> Option.exists (fun record ->
            Access.can principal access capability (Scope.Case record.CaseId))

    let current _installation parties (record: StoredCase) =
        CaseFamily.validateSnapshot record.Snapshot
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun () ->
            let index = CaseIndex.create record.Index.ChangedAt record.Snapshot None [] parties

            let expected =
                { index with
                    Responsible = record.Index.Responsible
                    FollowUp = record.Index.FollowUp
                    WorkSearchText = record.Index.WorkSearchText
                }

            if record.Snapshot.CaseId = record.CaseId && expected = record.Index then
                Ok record.Snapshot
            else
                Error CoreFault.StoreIntegrityError)
