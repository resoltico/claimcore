namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module OperationCatalog =
    let inputKey = InteractionInput.key
    let inputMessage = InteractionInput.messageKey

    let all =
        (CaseActKind.all |> List.map (ActKind.Case >> CatalogOperation.Act))
        @ (AuthorityKind.all
           |> List.filter ((<>) AuthorityKind.ProvisionOwner)
           |> List.map (ActKind.Authority >> CatalogOperation.Act))
        @ (ReadCatalog.declarations
           |> List.map (fun declaration -> CatalogOperation.Query declaration.Endpoint))

    let mutations = all |> List.filter (fun operation -> operation.SelectedAct.IsSome)

    let authorityKinds =
        mutations
        |> List.choose (fun operation ->
            match operation.SelectedAct with
            | Some(ActKind.Authority kind) -> Some kind
            | _ -> None)

    let private message (entry: CatalogOperation) part =
        match entry.SelectedAct with
        | Some act -> Vocabulary.messageKey ActKind.vocabulary act part
        | None -> Vocabulary.messageKey ActorEndpoint.vocabulary entry.Endpoint part

    let labelId operation = message operation "label"
    let meaningId operation = message operation "meaning"

    let confirmationId (operation: CatalogOperation) =
        match operation.SelectedAct, operation.Endpoint with
        | Some _, _ -> Some(message operation "confirmation")
        | None, ActorEndpoint.Query QueryEndpoint.Resolve -> Some(message operation "confirmation")
        | _ -> None

    let private structure (operation: CatalogOperation) =
        let route = Api.forActor operation.Endpoint

        let selection =
            match operation.SelectedAct with
            | Some(ActKind.Case kind) ->
                [
                    RequestSchemas.OperationInput, ActSubject.token ActSubject.Case
                    RequestSchemas.CaseAct, CaseActKind.token kind
                ]
            | Some(ActKind.Authority kind) ->
                [
                    RequestSchemas.OperationInput, ActSubject.token ActSubject.Authority
                    RequestSchemas.AuthorityAct, AuthorityKind.token kind
                ]
            | None -> []

        Bindings.walk
            route.InputDefinitions
            (Map.ofList ((RequestSchemas.CaseSelector, "REFERENCE") :: selection))
            route.InputSchema

    let private derived =
        let structures = all |> List.map (fun operation -> operation, structure operation)
        let annotations = CatalogAnnotations.create RequestCodecs.operationInput.Definitions
        let actual = structures |> List.map (snd >> Bindings.keys) |> Set.unionMany
        Bindings.validateRegistry annotations actual

        structures
        |> List.map (fun (operation, nodes) ->
            let keys = Bindings.keys nodes

            let selected =
                annotations
                |> Map.filter (fun key _ -> Set.contains key keys)
                |> Map.map (fun _ create -> create operation)

            operation, Bindings.bind selected nodes)
        |> Map.ofList

    let derive (operation: CatalogOperation) = Map.find operation derived

    let entries =
        all
        |> List.map (fun operation ->
            let inputs, constants = derive operation
            let family, sections, effect = CatalogOperation.interaction operation

            {
                Kind = operation
                Family = family
                AffectedSections = sections
                Effect = effect
                LabelId = labelId operation
                MeaningId = meaningId operation
                ConfirmationId = confirmationId operation
                Inputs = inputs |> List.map _.Binding
                Constants = constants
            })

    let operation (entry: CatalogEntry) = entry.Kind
