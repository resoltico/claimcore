namespace ClaimCore.Contract

open ClaimCore.Domain

module Catalog =
    let document: CatalogDocument =
        {
            Fields =
                Record.fields
                |> List.map (fun row ->
                    {
                        Name = row.Field
                        Group = row.Group
                        LabelId = RecordFields.labelId row.Field
                        MeaningId = RecordFields.meaningId row.Field
                        Scalar = RecordFields.descriptor row.Field
                        AllowsAbsence = row.AllowsAbsence
                    })
            InputFields =
                Vocabulary.all TechnicalInputs.vocabulary
                |> List.map (fun input ->
                    {
                        Name = TechnicalInputs.token input
                        LabelId = Vocabulary.messageKey TechnicalInputs.vocabulary input "label"
                        MeaningId =
                            (match input with
                             | TechnicalInput.Target
                             | TechnicalInput.Act -> None
                             | _ ->
                                 Some(
                                     Vocabulary.messageKey
                                         TechnicalInputs.vocabulary
                                         input
                                         "meaning"
                                 ))
                        Scalar =
                            if input = TechnicalInput.Reason then
                                Some(ScalarRule.Text PartyName.descriptor)
                            else
                                None
                    })
            CharacterWarnings =
                {
                    MaximumCodePoints = TextDisplay.maximumCodePoints
                    Categories = TextDisplay.categoryTokens
                }
            Limits =
                {
                    DefaultPageSize = Limits.DefaultPageSize
                    MaximumPageSize = Limits.MaximumPageSize
                    RequestByteLimit = Limits.MaximumRequestBytes
                }
            Entries = OperationCatalog.entries
            ProductVersion = BuildIdentity.current.Version
        }
