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
                TaskInputId.technical
                |> List.map (fun input ->
                    {
                        Name = TaskInputId.key input
                        LabelId = TaskInputPresentation.messageKey input "label"
                        HintId = TaskInputPresentation.hintId input
                        RuleId = Some(TaskInputPresentation.messageKey input "rule")
                        Scalar = TaskInputPresentation.scalar input
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
                        AdmittedScopes = Access.roleScopes role
                        AdmittedPrincipalKinds =
                            Vocabulary.all PrincipalKind.vocabulary
                            |> List.filter (fun kind ->
                                kind = PrincipalKind.Human || not (Access.humanOnlyRole role))
                    })
            Capabilities =
                Capability.all
                |> List.map (fun capability ->
                    {
                        Capability = capability
                        AdmittedScopes = Access.capabilityScopes capability
                        AdmittedPrincipalKinds =
                            PrincipalKind.vocabulary
                            |> Vocabulary.all
                            |> List.filter (fun kind ->
                                kind = PrincipalKind.Human
                                || not (Access.humanOnlyCapability capability))
                    })
            MoneyMinorUnits =
                CurrencyMinorUnits.all
                |> Map.toList
                |> List.map (fun (code, scale) -> { Currency = code; MinorUnits = scale })
            ProductVersion = BuildIdentity.current.Version
        }
