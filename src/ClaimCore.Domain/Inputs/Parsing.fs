namespace ClaimCore.Domain.Inputs

open ClaimCore.Domain
open ClaimCore.Domain.Tasks

/// Syntax adaptation and independent issue accumulation, shared by task-local typed parsers.
module internal Parsing =
    let target value = TaskInputId.Target value
    let field value = target (InputTarget.Field value)
    let issue input violation = Refusal.invalidInput (input, violation)

    let at input =
        Result.mapError (fun violation -> issue input violation)

    let text input (values: InputMap) =
        match Map.tryFind (target input) values.Values with
        | Some(InputValue.Text value) -> Ok value
        | _ -> Error(issue input Violation.TextRequired)

    let optionalText input (values: InputMap) =
        match Map.tryFind (target input) values.Values with
        | None
        | Some InputValue.Null -> Ok None
        | Some(InputValue.Text value) -> Ok(Some value)
        | _ -> Error(issue input Violation.TextRequired)

    let scalar input parse values =
        text input values |> Result.bind (parse >> at input)

    let group result =
        result
        |> Result.mapError (fun issues ->
            issues
            |> List.map (fun (field, violation) ->
                {
                    InputIssue.Target = InputTarget.Field field
                    Violation = violation
                    GrantRow = None
                })
            |> InputIssues.ofList
            |> Option.get
            |> Refusal.InvalidInput)

    let collect results =
        results
        |> List.fold
            (fun current next -> Admission.map2 (fun values value -> value :: values) current next)
            (Ok [])
        |> Result.map List.rev

    let check parse values =
        match parse values with
        | Ok _ -> []
        | Error(Refusal.InvalidInput issues) ->
            InputIssues.toList issues |> List.map TaskInputIssue.ofDomain
        | Error _ -> []

    let reject result =
        result |> Result.mapError Rejection.ofRefusal

    let workValue input parse (values: InputMap) =
        match Map.tryFind (target input) values.Values with
        | None -> Ok WorkValue.Keep
        | Some InputValue.Null -> Ok WorkValue.Clear
        | Some(InputValue.Text value) -> parse value |> at input |> Result.map WorkValue.Set
        | _ -> Error(issue input Violation.TextRequired)
