namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Message identities follow the declared concept and its consequence, never a task switch.
module PresentationKeys =
    let code vocabulary token = vocabulary + "." + token + ".label"

    let consequence act =
        let declaration = TaskConsequences.declaration act

        let roleInput (input: TaskInputDeclaration) =
            input.Input = TaskInputId.Target InputTarget.Role

        let changesAccess =
            declaration.Inputs |> List.exists roleInput
            || declaration.Collections
               |> List.exists (fun collection -> collection.Inputs |> List.exists roleInput)

        let part =
            if
                declaration.Consequence <> Consequence.Routine
                || (declaration.Exposure = TaskExposure.Client && changesAccess)
            then
                "consequence"
            else
                "meaning"

        "task." + TaskId.token declaration.Id + "." + part
