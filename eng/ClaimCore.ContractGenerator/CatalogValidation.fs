namespace ClaimCore.ContractGeneration

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Application

/// Structural identity checks happen before publication; Node additionally validates ICU and arguments.
module CatalogValidation =
    let private authored language =
        AuthoredMessages.templates language |> Map.keys |> Set.ofSeq

    let validateIdentity known identity =
        if not (Set.contains identity known) then
            invalidOp "Unknown core presentation identity."

    let private definitions known =
        let validate = validateIdentity known

        for field in FieldDefinitions.all do
            validate (FieldTable.labelId field.Field)
            validate (FieldTable.meaningId field.Field)

        for command in OperationCatalog.caseDefinitions do
            validate command.LabelId
            validate command.MeaningId
            validate command.ConfirmationId

            match command.Inputs with
            | ActInputShape.Fields _ -> ()
            | ActInputShape.CorrectionGroups groups ->
                for group in groups do
                    validate group.LabelId
                    validate group.MeaningId

        validate OperationCatalog.authorityTarget.LabelId

        for command in OperationCatalog.authorityDefinitions do
            validate command.LabelId
            validate command.MeaningId
            validate command.ConfirmationId

            for input in command.Inputs do
                validate input.LabelId

        for interaction in OperationCatalog.entries do
            validate interaction.LabelId
            validate interaction.MeaningId
            Option.iter validate interaction.ConfirmationId

            for input in interaction.Inputs do
                validate input.LabelId
                Option.iter validate input.MeaningId

    let validate () =
        for language in LocalizationPolicy.languages do
            definitions (authored language)
