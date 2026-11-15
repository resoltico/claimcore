namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire

/// CLI delivery and wording adapt semantic tasks without inspecting a wire schema.
module TaskRouting =
    let endpoint =
        function
        | ApplicationTaskId.Operation _ -> ActorEndpoint.Operation OperationEndpoint.Execute
        | ApplicationTaskId.Query query -> ActorEndpoint.Query query
        | ApplicationTaskId.Command command -> ActorEndpoint.Command command

    let act =
        function
        | ApplicationTaskId.Operation act -> Some act
        | ApplicationTaskId.Query _
        | ApplicationTaskId.Command _ -> None

    let operationDeclaration act =
        ActKind.token act
        |> TaskId.ofLiteral
        |> ClaimCore.Domain.Tasks.Registry.findDeclaration
        |> Option.defaultWith (fun () -> invalidArg (nameof act) "The operation is not declared.")

    let creates =
        function
        | ApplicationTaskId.Operation act -> (operationDeclaration act).Creates
        | ApplicationTaskId.Query _
        | ApplicationTaskId.Command _ -> false

    let consequence =
        function
        | ApplicationTaskId.Operation act -> (operationDeclaration act).Consequence
        | ApplicationTaskId.Query _
        | ApplicationTaskId.Command _ -> Consequence.Routine

    let private message task part =
        match task with
        | ApplicationTaskId.Operation act -> Vocabulary.messageKey ActKind.vocabulary act part
        | ApplicationTaskId.Query _
        | ApplicationTaskId.Command _ ->
            Vocabulary.messageKey ActorEndpoint.vocabulary (endpoint task) part

    let labelId task = message task "label"
    let meaningId task = message task "meaning"
