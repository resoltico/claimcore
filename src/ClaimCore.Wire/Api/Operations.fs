namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type OperationEndpoint =
    | Preview
    | Execute

[<RequireQualifiedAccess>]
type ActorEndpoint =
    | Operation of OperationEndpoint
    | Query of QueryTaskId
    | Command of string

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActorEndpoint =
    let vocabulary =
        Vocabulary.define
            "endpoint"
            ([
                ActorEndpoint.Operation OperationEndpoint.Preview
                ActorEndpoint.Operation OperationEndpoint.Execute
             ]
             @ (QueryTaskId.all |> List.map ActorEndpoint.Query)
             @ ((EndpointDeclarations.commands |> List.map _.Id)
                |> List.map ActorEndpoint.Command))
            (function
             | ActorEndpoint.Operation OperationEndpoint.Preview -> "operation.preview"
             | ActorEndpoint.Operation OperationEndpoint.Execute -> "operation.execute"
             | ActorEndpoint.Query query -> QueryTaskId.key query
             | ActorEndpoint.Command command -> command)

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
