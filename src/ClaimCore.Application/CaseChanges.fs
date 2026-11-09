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

    let payments (before: CaseFields option) (after: CaseFields option) =
        let entries (fields: CaseFields option) =
            fields |> Option.map _.Payments |> Option.defaultValue []

        let previous = entries before
        let next = entries after
        let previousIds = previous |> List.map PaymentEntry.id |> Set.ofList
        let nextIds = next |> List.map PaymentEntry.id |> Set.ofList

        let changes =
            [
                for entry in previous do
                    if not (Set.contains (PaymentEntry.id entry) nextIds) then
                        yield PaymentDifference.Removed entry
                for entry in next do
                    if not (Set.contains (PaymentEntry.id entry) previousIds) then
                        yield PaymentDifference.Recorded entry
            ]

        if changes.IsEmpty then
            None
        else
            PaymentCollectionDifference.create
                changes
                (before |> Option.bind (fun fields -> PaymentProgress.totals fields.Progress))
                (after |> Option.bind (fun fields -> PaymentProgress.totals fields.Progress))
            |> Result.defaultWith invalidOp
            |> Some

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
            Payments =
                payments
                    (before |> Option.bind (fun snapshot -> CaseState.fields snapshot.State))
                    (CaseState.fields after.State)
            Fields =
                differences
                    (before |> Option.map snapshotValues |> Option.defaultValue Map.empty)
                    (snapshotValues after)
        }

    let private live =
        function
        | LiveCase.Active fields -> Lifecycle.Active, fields
        | LiveCase.Voided fields -> Lifecycle.Voided, fields

    let internal lifecycle before after =
        let afterLifecycle, _ = live after
        let beforeLifecycle = before |> Option.map (live >> fst)

        if beforeLifecycle = Some afterLifecycle then
            None
        else
            Some
                {
                    LifecycleChange.Before = beforeLifecycle
                    After = afterLifecycle
                }
