namespace ClaimCore.ContractGeneration

open ClaimCore.Contract
open ClaimCore.Domain

module ActPresentation =
    let private groupFields group =
        FieldTable.rows
        |> List.filter (fun row -> row.Group = group)
        |> List.map (fun row -> InputTarget.Field row.Field)

    let private registrationFields = groupFields FieldGroup.Registration

    let private blank fieldName : FieldInputDefinition =
        {
            FieldName = fieldName
            Prefill = PrefillSource.Blank
        }

    let private current fieldName : FieldInputDefinition =
        {
            FieldName = fieldName
            Prefill =
                match fieldName with
                | InputTarget.Field field -> PrefillSource.CurrentField field
                | InputTarget.Reason -> PrefillSource.Blank
                | _ -> invalidArg "fieldName" "Expected a business or stewardship scalar."
        }

    let private fields values = ActInputShape.Fields values

    let private correctionGroups =
        Vocabulary.all CorrectionGroup.vocabulary
        |> List.map (fun group ->
            let fields =
                match group with
                | CorrectionGroup.Registration -> FieldGroup.Registration
                | CorrectionGroup.Decision -> FieldGroup.Decision
                | CorrectionGroup.Payment -> FieldGroup.Payment

            {
                Name = group
                LabelId = Vocabulary.messageKey CorrectionGroup.vocabulary group "label"
                MeaningId = Vocabulary.messageKey CorrectionGroup.vocabulary group "meaning"
                Actions = ActDefinitions.correctionActions group
                ReplaceFields = groupFields fields |> List.map current
            })
        |> ActInputShape.CorrectionGroups

    let private definition kind inputs : ActDefinition =
        {
            Kind = kind
            LabelId = Vocabulary.messageKey CaseActKinds.vocabulary kind "label"
            MeaningId = Vocabulary.messageKey CaseActKinds.vocabulary kind "meaning"
            ConfirmationId = "ui.reviewConfirmation"
            Inputs = inputs
        }

    let private registrationDefinitions =
        [
            definition
                (CaseActKind.Business BusinessActKind.Open)
                (registrationFields |> List.map blank |> fields)
            definition
                (CaseActKind.Business BusinessActKind.AmendRegistration)
                (registrationFields |> List.map current |> fields)
        ]

    let private correctionDefinition =
        definition (CaseActKind.Business BusinessActKind.Correct) correctionGroups

    let private paymentAndStatusDefinitions =
        [
            definition
                (CaseActKind.Business BusinessActKind.Decide)
                (fields (groupFields FieldGroup.Decision |> List.map current))
            definition (CaseActKind.Business BusinessActKind.WithdrawDecision) (fields [])
            definition
                (CaseActKind.Business BusinessActKind.RecordPayment)
                (groupFields FieldGroup.Payment |> List.map blank |> fields)
            definition (CaseActKind.Business BusinessActKind.ClearPayment) (fields [])
            definition (CaseActKind.Business BusinessActKind.Close) (fields [])
            definition (CaseActKind.Business BusinessActKind.Reopen) (fields [])
        ]

    let private stewardshipDefinitions =
        [
            definition
                (CaseActKind.Stewardship StewardshipActKind.Void)
                (fields [ blank InputTarget.Reason ])
            definition
                (CaseActKind.Stewardship StewardshipActKind.Reinstate)
                (fields [ blank InputTarget.Reason ])
            definition (CaseActKind.Stewardship StewardshipActKind.Erase) (fields [])
        ]

    let all =
        registrationDefinitions
        @ [ correctionDefinition ]
        @ paymentAndStatusDefinitions
        @ stewardshipDefinitions

    let forKind kind =
        all |> List.find (fun definition -> definition.Kind = kind)
