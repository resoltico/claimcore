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

    let principalFor kind inputs =
        match
            kind,
            target InputTarget.PrincipalIssuer inputs,
            target InputTarget.PrincipalSubject inputs
        with
        | Some kind, Some issuer, Some subject ->
            PrincipalKey.ofKind kind issuer subject |> Result.toOption
        | _ -> None

    let principal inputs =
        let kind =
            target InputTarget.PrincipalKind inputs
            |> Option.bind (Vocabulary.tryParse PrincipalKind.vocabulary)

        principalFor kind inputs

    let personPrincipal exposure existing inputs =
        let inferred =
            existing
            |> Option.map (PrincipalKey.parts >> fun (kind, _, _) -> kind)
            |> Option.orElse (
                if exposure = TaskExposure.Operator then
                    Some PrincipalKind.Human
                else
                    None
            )

        principal inputs |> Option.orElseWith (fun () -> principalFor inferred inputs)

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
