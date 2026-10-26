namespace ClaimCore.Contract

module Catalog =
    let document: CatalogDocument =
        {
            Fields =
                FieldTable.rows
                |> List.map (fun row ->
                    {
                        Name = row.Field
                        Group =
                            (match row.Placement with
                             | FieldPlacement.TopLevel -> None
                             | FieldPlacement.Grouped group -> Some group)
                        LabelId = FieldTable.labelId row.Field
                        MeaningId = FieldTable.meaningId row.Field
                        Scalar = row.Scalar
                        AllowsAbsence = row.Optional
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
                                Some(ScalarRule.Text FieldRules.freeText)
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
