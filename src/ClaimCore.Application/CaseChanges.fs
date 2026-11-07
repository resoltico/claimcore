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
