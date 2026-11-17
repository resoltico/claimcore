namespace ClaimCore.Domain.Tasks

open ClaimCore.Domain
open ClaimCore.Domain.Inputs

module internal CaseParsing =
    /// Keep independently parsed scalars through current admission and chronology checks.
    let values (inputs: InputMap) =
        inputs.Values
        |> Map.toList
        |> List.choose (fun (input, value) ->
            match input with
            | TaskInputId.Target(InputTarget.Field field) when
                (BusinessField.describe field).Group.IsSome
                ->
                let parsed =
                    match value with
                    | InputValue.Text raw ->
                        FieldValue.parse (BusinessField.describe field).Kind raw |> Result.map Some
                    | InputValue.Null -> Ok None
                    | _ -> Error Violation.TextRequired

                Some(field, parsed)
            | _ -> None)
        |> Map.ofList

    let private fields group (values: Map<BusinessField, Result<Admitted option, Violation>>) =
        BusinessField.ofGroup group
        |> List.choose (fun field ->
            match Map.tryFind field values with
            | None
            | Some(Ok None) when (BusinessField.describe field).AllowsAbsence -> None
            | Some(Ok(Some value)) -> Some(field, Ok value)
            | Some(Error issue) -> Some(field, Error issue)
            | _ -> Some(field, Error Violation.TextRequired))

    let registration admit values =
        fields RecordGroup.Registration values
        |> Registration.parsePreparedWith admit
        |> Parsing.group

    let decision admit values =
        fields RecordGroup.Decision values
        |> Decision.parsePreparedWith admit
        |> Parsing.group

    let payment admit values =
        fields RecordGroup.Payment values
        |> Payment.parsePreparedWith admit
        |> Parsing.group

    let private updates group (values: Map<BusinessField, Result<Admitted option, Violation>>) =
        let fields = BusinessField.ofGroup group

        let supplied =
            fields |> List.filter (fun field -> Map.containsKey field values) |> Set.ofList

        let required =
            fields
            |> List.fold
                (fun required field ->
                    match (BusinessField.describe field).Pairs with
                    | Some pair when Set.contains field supplied || Set.contains pair supplied ->
                        Set.add field (Set.add pair required)
                    | _ -> required)
                supplied

        fields
        |> List.choose (fun field ->
            if not (Set.contains field required) then
                None
            else
                let parsed =
                    match Map.tryFind field values with
                    | Some(Ok None) when not (BusinessField.describe field).AllowsAbsence ->
                        Error Violation.TextRequired
                    | Some value -> value
                    | None -> Error Violation.TextRequired

                Some(field, parsed))

    let registrationUpdate admit values =
        updates RecordGroup.Registration values
        |> RegistrationUpdateInput.parsePreparedWith admit
        |> Parsing.group

    let decisionUpdate admit values =
        updates RecordGroup.Decision values
        |> DecisionUpdateInput.parsePreparedWith admit
        |> Parsing.group

    let chronology
        before
        opening
        clearing
        (values: Map<BusinessField, Result<Admitted option, Violation>>)
        =
        let dates =
            values
            |> Map.toList
            |> List.choose (fun (field, parsed) ->
                if (BusinessField.describe field).Kind = FieldValueKind.CalendarDate then
                    Some(
                        field,
                        parsed |> Result.toOption |> Option.flatten |> Option.map FieldValue.date
                    )
                else
                    None)

        CaseAdmission.preparedChronology before opening clearing dates
