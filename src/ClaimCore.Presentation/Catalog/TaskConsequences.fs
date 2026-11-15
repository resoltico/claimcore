namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Operation contracts resolve their metadata through the Domain registry.
module TaskConsequences =
    let declaration act =
        ActKind.token act
        |> TaskId.ofLiteral
        |> ClaimCore.Domain.Tasks.Registry.findDeclaration
        |> Option.defaultWith (fun () ->
            invalidArg (nameof act) "The operation has no task declaration.")

    let ofAct act = (declaration act).Consequence
