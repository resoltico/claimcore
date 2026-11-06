namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Renderer-neutral differences are computed once from the disclosed core facts.
module internal CaseChanges =
    let private values =
        function
        | None -> Map.empty
        | Some fields ->
            CaseFields.declarations
            |> List.map (fun field -> field.Field, CaseFields.typedValue field.Field fields)
            |> Map.ofList

    let private differences previous next : CaseFieldDifference list =
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

    let fields before after : FieldDiff list =
        differences (values before) (values after)
        |> List.map (fun change ->
            {
                FieldDiff.FieldName = change.FieldName
                Before = Option.map CaseFieldValue.format change.Before
                After = Option.map CaseFieldValue.format change.After
            })

    let review (_act: ValidCaseAct) (before: CaseSnapshot option) (after: CaseSnapshot) =
        let snapshotValues (snapshot: CaseSnapshot) =
            values (CaseState.fields snapshot.State)
            |> Map.add
                BusinessField.CaseReference
                (Some(CaseFieldValue.Reference snapshot.Reference))

        let priorLifecycle =
            before |> Option.map (fun snapshot -> CaseState.lifecycle snapshot.State)

        let nextLifecycle = CaseState.lifecycle after.State

        {
            CaseReviewChanges.Lifecycle =
                if priorLifecycle = Some nextLifecycle then
                    None
                else
                    Some
                        {
                            Before = priorLifecycle
                            After = nextLifecycle
                        }
            Fields =
                differences
                    (before |> Option.map snapshotValues |> Option.defaultValue Map.empty)
                    (snapshotValues after)
        }

    let private live =
        function
        | LiveCase.Active fields -> Lifecycle.Active, fields
        | LiveCase.Voided fields -> Lifecycle.Voided, fields

    let internal compared before after =
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

    let history (previous: OperationReceipt option) (receipt: OperationReceipt) =
        match receipt.Subject with
        | ReceiptSubject.Work _
        | ReceiptSubject.Reminder _
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
