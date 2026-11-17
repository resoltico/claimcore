namespace ClaimCore.Presentation

open ClaimCore.Domain

module TaskConsequences =
    let declaration task =
        ClaimCore.Domain.Tasks.Registry.findDeclaration task
        |> Option.defaultWith (fun () ->
            invalidArg (nameof task) "The operation has no task declaration.")

    let ofTask task = (declaration task).Consequence
