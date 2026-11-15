namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type ApplicationTaskId =
    | Operation of ActKind
    | Query of QueryTaskId
    | Command of CommandTaskId

module ApplicationTaskId =
    let ofOperation input =
        ApplicationTaskId.Operation(ActKind.ofInput input)

    let key =
        function
        | ApplicationTaskId.Operation act -> ActKind.token act
        | ApplicationTaskId.Query query -> QueryTaskId.key query
        | ApplicationTaskId.Command command -> CommandTaskId.key command

    let all =
        let operation (declaration: TaskDeclaration) =
            let token = TaskId.token declaration.Id

            match ActKind.parse token with
            | Some act -> ApplicationTaskId.Operation act
            | None -> invalidOp ("A declared operation has no command adapter: " + token)

        let endpoint (declaration: EndpointDeclaration) =
            match declaration.Kind with
            | EndpointKind.Query ->
                match Vocabulary.tryParse QueryTaskId.vocabulary declaration.Id with
                | Some query -> ApplicationTaskId.Query query
                | None -> invalidOp ("A declared query has no endpoint: " + declaration.Id)
            | EndpointKind.Command ->
                CommandTaskId.all
                |> List.tryFind (fun command -> CommandTaskId.key command = declaration.Id)
                |> Option.map ApplicationTaskId.Command
                |> Option.defaultWith (fun () ->
                    invalidOp ("A declared command has no endpoint: " + declaration.Id))

        (ClaimCore.Domain.Tasks.Registry.clientDeclarations |> List.map operation)
        @ (EndpointDeclarations.all |> List.map endpoint)
