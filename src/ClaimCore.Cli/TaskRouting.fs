namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire

/// CLI delivery and wording adapt semantic tasks without inspecting a wire schema.
module TaskRouting =
    let endpoint =
        function
        | TaskId.Operation _ -> ActorEndpoint.Operation OperationEndpoint.Execute
        | TaskId.Query query -> ActorEndpoint.Query query

    let act =
        function
        | TaskId.Operation act -> Some act
        | TaskId.Query _ -> None

    let consequence =
        function
        | TaskId.Operation act -> Consequence.ofAct act
        | TaskId.Query _ -> Consequence.Routine

    let private message task part =
        match task with
        | TaskId.Operation act -> Vocabulary.messageKey ActKind.vocabulary act part
        | TaskId.Query _ -> Vocabulary.messageKey ActorEndpoint.vocabulary (endpoint task) part

    let labelId task = message task "label"
    let meaningId task = message task "meaning"
