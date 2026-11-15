namespace ClaimCore.Domain.Tasks

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes

module internal CaseRules =
    let contentAvailable (state: CaseFamily option) (_: Binding) =
        state |> Option.forall (fun state -> state.Case.Value.Content.IsSome)

    let content (state: CaseFamily) =
        match state.Case.Value.Content with
        | Some content -> Ok content
        | None -> Error(Rejection.InvalidState RecordViolation.InvalidFinancialPosition)

    let expected =
        function
        | Binding.Case(_, revision)
        | Binding.CasePayment(_, revision, _)
        | Binding.CaseErase(_, revision, _) -> Some revision
        | _ -> None

    let revision binding (state: CaseFamily) =
        match expected binding with
        | Some expected -> FamilyRules.revision expected state.Case.Revision
        | None -> Error FamilyRules.unavailable

    let gate requireOpen predicate state =
        FamilyRules.active requireOpen state
        |> Option.orElseWith (fun () ->
            if predicate state then
                None
            else
                Some(FamilyRules.notOffered state))

    let reviewed binding (state: CaseFamily) gate =
        if state.Case.Value.Lifecycle = Lifecycle.Erased then
            Some(FamilyRules.notOffered state)
        else
            match revision binding state with
            | Error refusal -> Some refusal
            | Ok() -> gate state

    let availability declaration environment state binding capability gate =
        FamilyRules.availability declaration environment state binding capability (fun state ->
            reviewed binding state gate)

    let admitInputsWithPermission syntax context environment state binding permitted =
        FamilyRules.admitPermission context environment state binding permitted (fun state ->
            if Result.isError syntax || state.Case.Value.Lifecycle = Lifecycle.Erased then
                None
            else
                revision binding state
                |> function
                    | Ok() -> None
                    | Error refusal -> Some refusal)

    let admitInputs syntax context environment state binding capability =
        admitInputsWithPermission
            syntax
            context
            environment
            state
            binding
            (FamilyRules.permission environment (Scope.Case state.Id) capability)

    let admit context environment state binding capability gate =
        FamilyRules.admit context environment state binding capability (fun state ->
            reviewed binding state gate)

    let checkState gate state =
        gate state
        |> Option.map (Rejection.Refused >> Error)
        |> Option.defaultValue (Ok())

    let acceptedContent gate state =
        checkState gate state |> Result.bind (fun () -> content state)

    let changeWithDisposition context (state: CaseFamily) (value: CasePartState) disposition =
        let accepted =
            {
                CaseValue.Reference = None
                Lifecycle = value.Lifecycle
                Status = value.Status
                Content = value.Content
                Disposition = disposition
            }

        FamilyRules.change
            context
            state.Id
            (CaseId.value state.Id)
            (Revision.successor state.Case.Revision)
            (PartValue.Case accepted)

    let change context state value =
        changeWithDisposition context state value None

    let seeds fields =
        fields
        |> List.map (fun (field, value) ->
            TaskInputId.Target(InputTarget.Field field), InputValue.Text(FieldValue.format value))
        |> Map.ofList
