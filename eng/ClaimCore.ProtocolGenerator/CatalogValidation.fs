namespace ClaimCore.ProtocolGeneration

open ClaimCore.Cli
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

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

    let private renderedTerms () =
        Terms.all
        |> List.collect (fun vocabulary -> vocabulary.Labels |> List.map snd)
        |> Set.ofList

    let private producers templates =
        let matches key expected =
            validateIdentity (templates |> Map.keys |> Set.ofSeq) key

            if Template.arguments templates[key] <> expected then
                invalidOp ("Message producer arguments differ: " + key)

        for KeyValue(key, arguments) in CliMessages.contracts do
            matches key arguments

        for KeyValue(key, _) in templates do
            if
                key.StartsWith("cli.", System.StringComparison.Ordinal)
                && not (Map.containsKey key CliMessages.contracts)
            then
                matches key Set.empty

        for KeyValue(_, entry) in DiagnosticCode.all do
            matches entry.MessageKey (entry.Parameters |> List.map _.Name |> Set.ofList)

    let validateReferences owner keys references =
        let unused = Set.difference keys references

        if not unused.IsEmpty then
            invalidOp ("Unreferenced " + owner + " message keys: " + String.concat ", " unused)

    let validateRendererOwnership language entries =
        let renderer (key: string) =
            key.StartsWith("ui.", System.StringComparison.Ordinal)
            || key.StartsWith("cli.", System.StringComparison.Ordinal)

        let core =
            entries
            |> List.filter (fun (key, _) -> not (renderer key))
            |> List.map snd
            |> Set.ofList

        for key, value in entries do
            if renderer key && Set.contains value core then
                invalidOp (language + ": renderer duplicates core wording: " + key)

    let validateCatalogs (authored: Map<string, (string * string) list>) =
        if (authored |> Map.keys |> Set.ofSeq) <> Set.ofList LocalizationPolicy.languages then
            invalidOp "Localization languages differ."

        for KeyValue(language, entries) in authored do
            validateRendererOwnership language entries

        let catalogs =
            authored
            |> Map.map (fun language entries ->
                entries
                |> Messages.catalog
                |> Result.defaultWith (fun issue -> invalidOp (language + ": " + issue))
                |> Messages.templates)

        let english = catalogs["en"]
        let keys = english |> Map.keys |> Set.ofSeq

        let ui =
            keys
            |> Set.filter (fun key -> key.StartsWith("ui.", System.StringComparison.Ordinal))

        let cli =
            keys
            |> Set.filter (fun key -> key.StartsWith("cli.", System.StringComparison.Ordinal))

        validateReferences "browser" ui (AuthoredMessages.references "browser")

        let cliReferences =
            Set.union
                (AuthoredMessages.references "cli")
                (Frames.failureCodes
                 |> List.map (fun code -> "cli.failure." + code)
                 |> Set.ofList)

        validateReferences "CLI" cli cliReferences

        let required = presentationIds ()
        let tokens = renderedTerms ()

        // Rendered vocabulary namespaces come from their validated registry, not a second inventory.
        let families =
            [
                "label"
                "endpoint"
                "authority"
                "diagnostic"
                "fault"
                "host"
                "notice"
                "scalar"
                "ui"
                "cli"
            ]
            @ (Terms.all |> List.map _.Name)

        for key in keys do
            if
                not (
                    families
                    |> List.exists (fun family ->
                        key.StartsWith(family + ".", System.StringComparison.Ordinal))
                )
            then
                invalidOp ("Unknown message family: " + key)

        for KeyValue(language, templates) in catalogs do
            if (templates |> Map.keys |> Set.ofSeq) <> keys then
                invalidOp ("Localization keys differ: " + language)

            for KeyValue(key, template) in templates do
                if Template.arguments template <> Template.arguments english[key] then
                    invalidOp ("Localization arguments differ: " + language + " " + key)

            for key in Set.union required tokens do
                validateIdentity keys key

                if not (Set.isEmpty (Template.arguments templates[key])) then
                    invalidOp ("Catalog metadata must not require arguments: " + key)

            producers templates

        catalogs

    let private admitted = lazy (AuthoredMessages.all () |> validateCatalogs)

    let validate () = admitted.Value
