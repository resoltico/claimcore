namespace ClaimCore.ContractGeneration

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Contract.Protocol

/// Admit the single Template grammar, language parity and declared producer contracts before publication.
module CatalogValidation =
    let validateIdentity known identity =
        if not (Set.contains identity known) then
            invalidOp "Unknown core presentation identity."

    let private presentationIds () =
        let rec collect =
            function
            | JsonValue.Array items -> items |> List.collect collect
            | JsonValue.Object fields ->
                fields
                |> List.collect (fun (name, value) ->
                    match name, value with
                    | ("labelId" | "meaningId" | "confirmationId"), JsonValue.String key -> [ key ]
                    | _ -> collect value)
            | _ -> []

        ContractCatalog.render ()
        |> StrictJson.read System.Int32.MaxValue
        |> Result.defaultWith (fun _ -> invalidOp "Invalid generated catalog.")
        |> collect
        |> Set.ofList

    let private renderedTokens () =
        let scopes =
            OperationCatalog.mutations
            |> List.collect (fun operation ->
                OperationCatalog.derive operation
                |> fst
                |> List.collect (fun input ->
                    match input.Spec.Name, input.Spec.Atom with
                    | CatalogInputName.Technical TechnicalInput.Scope, CatalogAtom.Choice choices ->
                        choices
                    | _ -> []))

        let erased =
            ResponseCodecs.caseState.Write CaseView.Erased |> JsonValue.tryMember "tag"

        [
            yield! CaseStatuses.all |> List.map CaseStatuses.token
            yield! ActKinds.all |> List.map ActKinds.token
            yield! RoleTokens.all |> List.map RoleTokens.token
            yield! Dispositions.all |> List.map Dispositions.token
            yield! CorrectionActions.all |> List.map CorrectionActions.token
            yield! PrincipalKinds.all
            yield! scopes
            match erased with
            | Some(JsonValue.String tag) -> yield tag
            | _ -> invalidOp "Missing erased state tag."
        ]
        |> List.map (fun token -> "token." + token)
        |> Set.ofList

    let private producers templates =
        let matches key expected =
            validateIdentity (templates |> Map.keys |> Set.ofSeq) key

            if Template.arguments templates[key] <> expected then
                invalidOp ("Message producer arguments differ: " + key)

        for definition in Refusals.definitions do
            matches
                ("diagnostic." + definition.Id)
                (definition.Parameters |> List.map _.Name |> Set.ofList)

        for _, code in CoreFaults.all do
            matches ("fault." + code) Set.empty

        for failure in HostFailures.all do
            matches ("host." + HostFailures.code failure) Set.empty

    let validateCatalogs (authored: Map<string, (string * string) list>) =
        if (authored |> Map.keys |> Set.ofSeq) <> Set.ofList LocalizationPolicy.languages then
            invalidOp "Localization languages differ."

        let catalogs =
            authored
            |> Map.map (fun language entries ->
                entries
                |> Messages.catalog
                |> Result.defaultWith (fun issue -> invalidOp (language + ": " + issue))
                |> Messages.templates)

        let english = catalogs["en"]
        let keys = english |> Map.keys |> Set.ofSeq

        let required = presentationIds ()
        let tokens = renderedTokens ()

        let families =
            [
                "field"
                "act"
                "group"
                "authority"
                "input"
                "diagnostic"
                "fault"
                "host"
                "notice"
                "token"
                "ui"
                "cli"
            ]

        for key in keys do
            if
                not (
                    families
                    |> List.exists (fun family ->
                        key.StartsWith(family + ".", System.StringComparison.Ordinal))
                )
            then
                invalidOp ("Unknown message family: " + key)

        if
            (keys
             |> Set.filter (fun key -> key.StartsWith("token.", System.StringComparison.Ordinal)))
            <> tokens
        then
            invalidOp "Rendered token catalogs differ from their producer vocabularies."

        for KeyValue(language, templates) in catalogs do
            if (templates |> Map.keys |> Set.ofSeq) <> keys then
                invalidOp ("Localization keys differ: " + language)

            for KeyValue(key, template) in templates do
                if Template.arguments template <> Template.arguments english[key] then
                    invalidOp ("Localization arguments differ: " + language + " " + key)

            required |> Set.iter (validateIdentity keys)
            producers templates

        catalogs

    let private admitted = lazy (AuthoredMessages.all () |> validateCatalogs)

    let validate () = admitted.Value
