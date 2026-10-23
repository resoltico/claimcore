namespace ClaimCore.ContractGeneration

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Application

/// Structural identity checks happen before publication; Node additionally validates ICU and arguments.
module CatalogValidation =
    let private authoredEnglish () =
        AuthoredMessages.templates "en" |> Map.keys |> Set.ofSeq

    let validateIdentity known identity =
        if not (Set.contains identity known) then
            invalidOp "Unknown core presentation identity."

    let validate () =
        let known = authoredEnglish ()
        let validate = validateIdentity known

        for field in FieldDefinitions.all do
            validate (FieldTable.labelId field.Field)
            validate (FieldTable.meaningId field.Field)

        for command in CommandDefinitions.all do
            validate command.LabelId
            validate command.MeaningId
            validate command.ConfirmationId

            match command.Inputs with
            | CommandInputShape.Fields _ -> ()
            | CommandInputShape.CorrectionGroups groups ->
                for group in groups do
                    validate group.LabelId
                    validate group.MeaningId

        validate AuthorityOperationDefinitions.target.LabelId

        for command in AuthorityOperationDefinitions.all do
            validate command.LabelId
            validate command.MeaningId
            validate command.ConfirmationId

            for input in command.Inputs do
                validate input.LabelId
