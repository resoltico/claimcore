namespace ClaimCore.Protocol

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
                        LabelId = FieldPresentation.labelId row.Field
                        MeaningId = FieldPresentation.meaningId row.Field
                        Scalar = FieldPresentation.descriptor row.Field
                        AllowsAbsence = row.AllowsAbsence
                    })
            InputFields =
                Vocabulary.all TechnicalInput.vocabulary
                |> List.map (fun input ->
                    {
                        Name = TechnicalInput.token input
                        LabelId = Vocabulary.messageKey TechnicalInput.vocabulary input "label"
                        MeaningId =
                            (match input with
                             | TechnicalInput.Target
                             | TechnicalInput.Act -> None
                             | _ ->
                                 Some(
                                     Vocabulary.messageKey
                                         TechnicalInput.vocabulary
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
                    DefaultPageSize = Paging.DefaultPageSize
                    MaximumPageSize = Paging.MaximumPageSize
                    RequestByteLimit = Limits.MaximumRequestBytes
                }
            Entries = OperationCatalog.entries
            ProductVersion = BuildIdentity.current.Version
        }
