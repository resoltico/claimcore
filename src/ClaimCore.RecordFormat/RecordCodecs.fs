namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain
open ClaimCore.Json


module internal RecordCodecs =
    open Codec

    let private tagged name variants = Codec.taggedOn "kind" name variants

    let private members group =
        CaseFields.ofGroup group
        |> List.map (fun field -> field, (BusinessField.describe field).Kind)

    let role = enumeration (Vocabulary.entries Role.vocabulary)

    let private scalar parse format =
        text
        |> interpret
            (fun path raw ->
                parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid accepted scalar format."
                    }))
            format

    let private acceptedReference = scalar CaseReference.parse CaseReference.format

    let private acceptedGroup group create values =
        members group
        |> List.map (fun (field, kind) ->
            let declaration = BusinessField.describe field
            let value = scalar (FieldValue.parse kind) FieldValue.format

            let value =
                if declaration.AllowsAbsence then
                    nullable value
                else
                    value |> map Some Option.get

            field, declaration.Key, declaration.AllowsAbsence, value)
        |> Codec.fieldMapWithPresence
        |> interpret
            (fun path fields ->
                fields
                |> Map.toList
                |> List.choose (fun (field, value) ->
                    value |> Option.map (fun value -> field, value))
                |> create
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> List.map (fun (field, value) -> field, Some value) >> Map.ofList)

    let private acceptedRegistration =
        acceptedGroup RecordGroup.Registration Registration.create Registration.values

    let private acceptedDecision =
        acceptedGroup RecordGroup.Decision Decision.create Decision.values
        |> named "Decision"

    let private businessFields
        (fields: Fields<CaseFields, CaseStatus -> Registration -> FinancialPosition -> 'Result>)
        =
        fields
        |> field "status" _.Status (enumeration (Vocabulary.entries CaseStatus.vocabulary))
        |> field "registration" _.Registration acceptedRegistration
        |> field "finance" _.Finance (FinancialCodecs.position acceptedDecision)

    let caseFields =
        (record (fun reference status registration finance ->
            {
                CaseFields.CaseReference = reference
                Status = status
                Registration = registration
                Finance = finance
            })
        : Fields<CaseFields, _>)
        |> field "caseReference" _.CaseReference acceptedReference
        |> businessFields
        |> build

    /// Private compartment values borrow their immutable reference from the public root.
    let privateCaseFields reference =
        (record (fun status registration finance ->
            {
                CaseFields.CaseReference = reference
                Status = status
                Registration = registration
                Finance = finance
            })
        : Fields<CaseFields, _>)
        |> businessFields
        |> build

    let private liveStateWith name codec =
        let fields = record id |> field "fields" id codec

        tagged
            name
            [
                variant
                    (Lifecycle.token Lifecycle.Active)
                    LiveCase.Active
                    (function
                    | LiveCase.Active fields -> Some fields
                    | _ -> None)
                    fields
                variant
                    (Lifecycle.token Lifecycle.Voided)
                    LiveCase.Voided
                    (function
                    | LiveCase.Voided fields -> Some fields
                    | _ -> None)
                    fields
            ]

    let privateLiveState reference =
        liveStateWith "PrivateCaseState" (privateCaseFields reference)

    let liveState = liveStateWith "SnapshotRecord.State" caseFields
