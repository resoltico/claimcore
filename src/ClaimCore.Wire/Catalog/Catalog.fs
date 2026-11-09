namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

module Catalog =
    let document: CatalogDocument =
        {
            Fields =
                FieldPresentation.declarations
                |> List.map (fun row ->
                    {
                        Name = row.Field
                        ValueKind = row.Kind
                        Group = row.Group
                        Section = row.Section
                        Pairs = row.Pairs
                        LabelId = FieldPresentation.labelId row.Field
                        HintId = FieldPresentation.hintId row.Field
                        RuleId = FieldPresentation.ruleId row.Field
                        Scalar = FieldPresentation.descriptor row.Field
                        AllowsAbsence = row.AllowsAbsence
                        Prominence = FieldPresentation.prominence row.Field
                        Listed = FieldPresentation.listed row.Field
                    })
            InputFields =
                InteractionInput.technical
                |> List.map (fun input ->
                    {
                        Name = InteractionInput.key input
                        LabelId = InteractionInput.messageKey input "label"
                        HintId = InteractionInput.hintId input
                        RuleId = Some(InteractionInput.messageKey input "rule")
                        Scalar =
                            if input = InteractionInput.Target InputTarget.ProfileDisplayName then
                                Some(ScalarRule.Text PartyName.descriptor)
                            elif input = InteractionInput.Target InputTarget.ProfileContact then
                                Some(ScalarRule.Text ContactText.descriptor)
                            elif input = InteractionInput.Target InputTarget.Reason then
                                Some(ScalarRule.Text PartyName.descriptor)
                            elif input = InteractionInput.Target InputTarget.NoteText then
                                Some(ScalarRule.PlainText NoteText.constraints)
                            elif input = InteractionInput.Target InputTarget.ReminderAction then
                                Some(ScalarRule.PlainText ReminderAction.constraints)
                            elif input = InteractionInput.Target InputTarget.ReminderDueDate then
                                Some(ScalarRule.PlannedDate CalendarDate.descriptor)
                            elif input = InteractionInput.Target InputTarget.DirectorySearch then
                                Some(
                                    ScalarRule.Text
                                        { PartyName.descriptor with
                                            MaximumCharacters =
                                                Some DirectorySearchText.MaximumCharacters
                                        }
                                )
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
                    OperationWindowMilliseconds = int OperationLifetime.WindowMilliseconds
                    OperationFutureAllowanceMilliseconds =
                        int OperationLifetime.FutureAllowanceMilliseconds
                }
            Roles =
                Role.all
                |> List.map (fun role ->
                    {
                        Role = role
                        MeaningId = Vocabulary.messageKey Role.vocabulary role "meaning"
                        AdmittedScopes = Authority.roleScopes role
                        AdmittedPrincipalKinds =
                            Vocabulary.all PrincipalKind.vocabulary
                            |> List.filter (fun kind ->
                                kind = PrincipalKind.Human || not (Authority.humanOnlyRole role))
                    })
            Capabilities =
                Capability.all
                |> List.map (fun capability ->
                    {
                        Capability = capability
                        AdmittedScopes = Authority.capabilityScopes capability
                        AdmittedPrincipalKinds =
                            PrincipalKind.vocabulary
                            |> Vocabulary.all
                            |> List.filter (fun kind ->
                                kind = PrincipalKind.Human
                                || not (Authority.humanOnlyCapability capability))
                    })
            Entries = OperationCatalog.entries
            MoneyMinorUnits =
                CurrencyMinorUnits.all
                |> Map.toList
                |> List.map (fun (code, scale) -> { Currency = code; MinorUnits = scale })
            ProductVersion = BuildIdentity.current.Version
        }
