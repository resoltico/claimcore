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
        | ClientTaskKind.Binary ->
            invalidArg (nameof task) "Binary reads have a separate transport."

    let act task =
        (TaskDeclarations.describe task).Ledger |> Option.map _.Id

    let operationDeclaration task =
        ClaimCore.Domain.Tasks.Registry.findDeclaration task
        |> Option.defaultWith (fun () -> invalidArg (nameof task) "The operation is not declared.")

    let creates task =
        (TaskDeclarations.describe task).Creates

    let consequence task =
        (TaskDeclarations.describe task).Consequence

    let private messages task =
        let declaration = TaskDeclarations.describe task

        match declaration.Endpoint with
        | Some endpoint -> endpoint.LabelId, endpoint.MeaningId
        | None -> "task." + task + ".label", "task." + task + ".meaning"

    let labelId task = messages task |> fst
    let meaningId task = messages task |> snd
