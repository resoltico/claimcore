namespace ClaimCore.Application.Execution

open System
open ClaimCore.Domain

/// Lookup hints do not admit inputs; the selected task still parses and validates every value.
module internal InputSelection =
    let text input (values: Map<TaskInputId, InputValue>) =
        match Map.tryFind input values with
        | Some(InputValue.Text value) -> Some value
        | _ -> None

    let target input (inputs: InputMap option) =
        inputs
        |> Option.bind (fun inputs -> text (TaskInputId.Target input) inputs.Values)

    let principal inputs =
        match
            target InputTarget.PrincipalKind inputs,
            target InputTarget.PrincipalIssuer inputs,
            target InputTarget.PrincipalSubject inputs
        with
        | Some kind, Some issuer, Some subject ->
            Vocabulary.tryParse PrincipalKind.vocabulary kind
            |> Option.bind (fun kind -> PrincipalKey.ofKind kind issuer subject |> Result.toOption)
        | _ -> None

    let private guid create text =
        match Guid.TryParse(text: string) with
        | true, value -> create value
        | _ -> None

    let responsible inputs =
        target (InputTarget.Field BusinessField.ResponsibleActor) inputs
        |> Option.bind (guid ActorId.create)

    let grantTargets (inputs: InputMap option) =
        inputs
        |> Option.map (fun inputs ->
            inputs.Values
            :: (inputs.Collections
                |> Map.values
                |> Seq.collect id
                |> Seq.map _.Values
                |> Seq.toList)
            |> List.collect (fun values ->
                [
                    text
                        (TaskInputId.Target(InputTarget.Field BusinessField.CaseReference))
                        values
                    |> Option.map GrantTarget.CaseReference
                    text TaskInputId.CaseId values
                    |> Option.bind (guid CaseId.create)
                    |> Option.map GrantTarget.CaseId
                ]
                |> List.choose id)
            |> List.distinct)
        |> Option.defaultValue []
