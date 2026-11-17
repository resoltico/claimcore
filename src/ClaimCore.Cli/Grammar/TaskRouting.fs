namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire

/// Delivery and wording derive from the declarations' task identity and endpoint.
module TaskRouting =
    let endpoint task =
        let declaration = TaskDeclarations.describe task

        match declaration.Kind with
        | ClientTaskKind.Operation -> ActorEndpoint.Operation OperationEndpoint.Execute
        | ClientTaskKind.Query ->
            QueryTaskId.all
            |> List.find (fun query -> QueryTaskId.key query = task)
            |> ActorEndpoint.Query
        | ClientTaskKind.Command -> CommandTaskId.parse task |> Option.get |> ActorEndpoint.Command

    let act task =
        (TaskDeclarations.describe task).Ledger |> Option.map _.Id

    let operationDeclaration task =
        ClaimCore.Domain.Tasks.Registry.findDeclaration task
        |> Option.defaultWith (fun () -> invalidArg (nameof task) "The operation is not declared.")

    let creates task =
        (TaskDeclarations.describe task).Creates

    let consequence task =
        (TaskDeclarations.describe task).Consequence

    let private message task suffix =
        (if (TaskDeclarations.describe task).Kind = ClientTaskKind.Operation then
             "task."
         else
             "endpoint.")
        + task
        + suffix

    let labelId task = message task ".label"
    let meaningId task = message task ".meaning"
