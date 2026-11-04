namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts

module Catalog =
    let document: CatalogDocument =
        {
            Fields =
                CaseFields.declarations
                |> List.map (fun row ->
                    {
                        Name = row.Field
                        Group = row.Group
                        Section = row.Section
                        Pairs = row.Pairs
                        LabelId = FieldPresentation.labelId row.Field
                        HintId = FieldPresentation.hintId row.Field
                        RuleId = FieldPresentation.ruleId row.Field
                        Scalar = FieldPresentation.descriptor row.Field
                        AllowsAbsence = row.AllowsAbsence
                        Prominence = row.Prominence
                        Listed = row.Listed
                    })
            InputFields =
                InteractionInput.technical
                |> List.map (fun input ->
                    {
                        Name = InteractionInput.key input
                        LabelId = InteractionInput.messageKey input "label"
                        HintId = Some(InteractionInput.messageKey input "hint")
                        RuleId = Some(InteractionInput.messageKey input "rule")
                        Scalar =
                            if input = InteractionInput.Target InputTarget.ProfileDisplayName then
                                Some(ScalarRule.Text PartyName.descriptor)
                            elif input = InteractionInput.Target InputTarget.ProfileContact then
                                Some(ScalarRule.Text ContactText.descriptor)
                            elif input = InteractionInput.Target InputTarget.Reason then
                                Some(ScalarRule.Text PartyName.descriptor)
                            elif input = InteractionInput.Target InputTarget.ReminderAction then
                                Some(ScalarRule.PlainText ReminderAction.constraints)
                            elif input = InteractionInput.Target InputTarget.ReminderDueDate then
                                Some(ScalarRule.PlannedDate CalendarDate.descriptor)
                            elif input = InteractionInput.Target InputTarget.ActorNameSearch then
                                Some(
                                    ScalarRule.Text
                                        { PartyName.descriptor with
                                            MaximumCharacters =
                                                Some ActorNameSearchText.MaximumCharacters
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
                }
            Roles =
                Role.all
                |> List.map (fun role ->
                    {
                        Role = role
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
