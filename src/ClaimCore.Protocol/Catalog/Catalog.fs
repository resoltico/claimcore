namespace ClaimCore.Protocol

open ClaimCore.Domain

module Catalog =
    let document: CatalogDocument =
        {
            Fields =
                CaseFields.declarations
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
                InteractionInput.technical
                |> List.map (fun input ->
                    {
                        Name = InteractionInput.key input
                        LabelId = InteractionInput.messageKey input "label"
                        MeaningId = Some(InteractionInput.messageKey input "meaning")
                        Scalar =
                            if input = InteractionInput.Target InputTarget.Reason then
                                Some(ScalarRule.Text PartyName.descriptor)
                            elif input = InteractionInput.Target InputTarget.Text then
                                Some(
                                    ScalarRule.Text
                                        { PartyName.descriptor with
                                            MaximumCharacters =
                                                Some CaseSearchText.MaximumCharacters
                                        }
                                )
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
