namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Renderer-neutral differences are computed once from the disclosed core facts.
module internal CaseChanges =
    let private values =
        function
        | None -> Map.empty
        | Some fields -> CaseFields.values fields |> Map.ofList

    let fields before after =
        let previous, next = values before, values after

        CaseFields.declarations
        |> List.choose (fun field ->
            let before = Map.tryFind field.Field previous |> Option.flatten
            let after = Map.tryFind field.Field next |> Option.flatten

            if before = after then
                None
            else
                Some
                    {
                        FieldName = field.Field
                        Before = before
                        After = after
                    })

    let review act (before: CaseSnapshot option) (after: CaseSnapshot) =
        match act with
        | CaseAct.Void _
        | CaseAct.Reinstate _
        | CaseAct.Bare BareAct.Erase -> []
        | _ ->
            fields
                (before |> Option.bind (fun snapshot -> CaseState.fields snapshot.State))
                (CaseState.fields after.State)

    let private live =
        function
        | LiveCase.Active fields -> Lifecycle.Active, fields
        | LiveCase.Voided fields -> Lifecycle.Voided, fields

    let private compared before after =
        let afterLifecycle, afterFields = live after
        let beforeLifecycle = before |> Option.map (live >> fst)

        let lifecycle =
            if beforeLifecycle = Some afterLifecycle then
                None
            else
                Some
                    {
                        LifecycleChange.Before = beforeLifecycle
                        After = afterLifecycle
                    }

        {
            Lifecycle = lifecycle
            Fields = fields (before |> Option.map (live >> snd)) (Some afterFields)
        }

    let history previous (receipt: OperationReceipt) =
        match receipt.Subject with
        | ReceiptSubject.Authority _ -> Error CoreFault.StoreIntegrityError
        | ReceiptSubject.Case current ->
            let before =
                match previous, Revision.value current.Revision with
                | None, 1L -> Ok None
                | Some {
                           Subject = ReceiptSubject.Case previous
                       },
                  revision when
                    previous.CaseId = current.CaseId
                    && previous.Reference = current.Reference
                    && Revision.value previous.Revision + 1L = revision
                    ->
                    Ok(Some previous.Snapshot)
                | _ -> Error CoreFault.StoreIntegrityError

            before
            |> Result.map (fun before ->
                match current.Snapshot, before with
                | SnapshotDisclosure.Disclosed after, None -> Some(compared None after)
                | SnapshotDisclosure.Disclosed after, Some(SnapshotDisclosure.Disclosed before) ->
                    Some(compared (Some before) after)
                | _ -> None)
