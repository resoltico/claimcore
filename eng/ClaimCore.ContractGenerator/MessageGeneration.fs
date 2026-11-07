namespace ClaimCore.ContractGeneration

open System.Text
open System.Text.Json
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Json

/// Types and runtime argument metadata are projections of admitted templates.
module MessageGeneration =
    let private quote (value: string) = JsonSerializer.Serialize value

    let private json value =
        WireWriter.bytes value |> Encoding.UTF8.GetString

    let private requirements =
        DiagnosticCode.all
        |> Map.toList
        |> List.map (fun (_, entry) ->
            entry.MessageKey,
            JsonValue.Object(
                entry.Parameters
                |> List.map (fun parameter ->
                    parameter.Name,
                    JsonValue.Object(
                        match parameter.Rule with
                        | DiagnosticParameterRule.IntegerRange(minimum, maximum) ->
                            [
                                "minimum", JsonValue.Integer(int64 minimum)
                                "maximum", JsonValue.Integer(int64 maximum)
                            ]
                        | DiagnosticParameterRule.Vocabulary(name, values) ->
                            [
                                "enum", JsonValue.Array(List.map JsonValue.String values)
                                "vocabulary", JsonValue.String name
                            ]
                    ))
            ))
        |> Map.ofList
        |> Map.toList
        |> JsonValue.Object

    let private diagnosticKeys =
        DiagnosticCode.all
        |> Map.toList
        |> List.map (fun (code, entry) -> code, JsonValue.String entry.MessageKey)
        |> JsonValue.Object

    let private generate includeMetadata (catalogs: Map<string, Map<string, Template>>) =
        let english = catalogs["en"]

        let guiDiagnosticKeys =
            DiagnosticCode.all
            |> Map.toList
            |> List.map (fun (code, entry) ->
                let key =
                    if Map.containsKey (entry.MessageKey + ".gui") english then
                        entry.MessageKey + ".gui"
                    else
                        entry.MessageKey

                code, JsonValue.String key)
            |> JsonValue.Object

        let shapes =
            english
            |> Map.toList
            |> List.map (fun (key, template) ->
                key,
                Template.arguments template
                |> Set.toList
                |> List.map (fun name -> name, JsonValue.String "string")
                |> JsonValue.Object)
            |> JsonValue.Object

        let declarations =
            english
            |> Map.toList
            |> List.map (fun (key, template) ->
                let arguments = Template.arguments template |> Set.toList

                let value =
                    if arguments.IsEmpty then
                        "Readonly<Record<string, never>>"
                    else
                        arguments
                        |> List.map (fun name -> "readonly " + quote name + ": string")
                        |> String.concat "; "
                        |> fun value -> "{ " + value + " }"

                "  " + quote key + ": " + value + ";")
            |> String.concat "\n"

        let corrections =
            Catalog.document.Entries
            |> List.collect _.Inputs
            |> List.filter (fun input ->
                match input.Kind with
                | BindingKind.Group _ -> true
                | _ -> false)
            |> List.collect _.DiagnosticTargets
            |> List.distinct
            |> List.map (fun name -> name + ".action", JsonValue.String name)
            |> JsonValue.Object

        let termTypes =
            Terms.all
            |> List.map (fun vocabulary ->
                "  "
                + quote vocabulary.Name
                + ": "
                + (vocabulary.Labels |> List.map (fst >> quote) |> String.concat " | ")
                + ";")
            |> String.concat "\n"

        let termKeys =
            Terms.all
            |> List.map (fun vocabulary ->
                vocabulary.Name,
                JsonValue.Object(
                    vocabulary.Labels |> List.map (fun (token, key) -> token, JsonValue.String key)
                ))
            |> JsonValue.Object

        let guidanceArguments (guidance: ScalarGuidance) =
            guidance.Arguments
            |> Map.toList
            |> List.map (fun (name, value) ->
                name,
                match value with
                | GuidanceArgument.Count count -> JsonValue.Integer(int64 count)
                | GuidanceArgument.Code code -> JsonValue.String code)
            |> JsonValue.Object

        let inputRuleArguments =
            ScalarGuidance.byInput
            |> List.map (fun (name, guidance) -> name, guidanceArguments guidance)
            |> JsonValue.Object

        let ruleArguments =
            ScalarGuidance.byInput
            |> List.map snd
            |> List.groupBy _.Key
            |> List.choose (fun (_, declarations) ->
                let variants = List.distinct declarations
                if variants.Length = 1 then Some variants.Head else None)
            |> List.map (fun guidance ->
                guidance.Key,
                guidance.Arguments
                |> Map.toList
                |> List.map (fun (name, value) ->
                    name,
                    match value with
                    | GuidanceArgument.Count count -> JsonValue.Integer(int64 count)
                    | GuidanceArgument.Code code -> JsonValue.String code)
                |> JsonValue.Object)
            |> JsonValue.Object

        [
            "// Generated by ClaimCore.ContractGenerator. Do not edit."
            "export type MessageArgs = {\n" + declarations + "\n};"
            "export type MessageKey = keyof MessageArgs;"
            "export const messageShapes = " + json shapes + " as const;"
            if includeMetadata then
                "export type Terms = {\n" + termTypes + "\n};"
                "export const termKeys = " + json termKeys + " as const;"
                "export const ruleArguments = " + json ruleArguments + " as const;"
                "export const inputRuleArguments = " + json inputRuleArguments + " as const;"
                "export const diagnosticParameters = " + json requirements + " as const;"
                "export const diagnosticMessageKeys = " + json diagnosticKeys + " as const;"

                "export const diagnosticGuiMessageKeys = "
                + json guiDiagnosticKeys
                + " as const;"

                "export const correctionTargets = " + json corrections + " as const;"
        ]
        |> String.concat "\n"
        |> fun text -> Encoding.UTF8.GetBytes(text + "\n")

    let private select (predicate: string -> bool) (catalogs: Map<string, Map<string, Template>>) =
        catalogs
        |> Map.map (fun _ templates -> templates |> Map.filter (fun key _ -> predicate key))

    let source catalogs =
        catalogs
        |> select (fun key ->
            not (
                key.StartsWith("ui.", System.StringComparison.Ordinal)
                || key.StartsWith("cli.", System.StringComparison.Ordinal)
            ))
        |> generate true

    let chromeSource catalogs =
        catalogs
        |> select (fun key -> key.StartsWith("ui.", System.StringComparison.Ordinal))
        |> generate false

    let private runtimeEntries predicate language =
        AuthoredMessages.all ()
        |> Map.find language
        |> List.filter (fun (key, _) -> predicate key)
        |> Map.ofList

    let private isChrome (key: string) =
        key.StartsWith("ui.", System.StringComparison.Ordinal)

    let private isCore (key: string) =
        not (isChrome key || key.StartsWith("cli.", System.StringComparison.Ordinal))

    let private runtimeCatalogSource predicate =
        LocalizationPolicy.languages
        |> List.map (fun language -> language, runtimeEntries predicate language)
        |> Map.ofList
        |> JsonSerializer.Serialize
        |> fun json ->
            Encoding.UTF8.GetBytes(
                "// Generated by ClaimCore.ContractGenerator. Do not edit.\nexport const catalogs = "
                + json
                + " as const;\n"
            )

    let catalogSource () = runtimeCatalogSource isCore
    let chromeCatalogSource () = runtimeCatalogSource isChrome

    let languageSource language =
        let core = runtimeEntries isCore language |> JsonSerializer.Serialize
        let chrome = runtimeEntries isChrome language |> JsonSerializer.Serialize

        Encoding.UTF8.GetBytes(
            "// Generated by ClaimCore.ContractGenerator. Do not edit.\nexport const core = "
            + core
            + " as const;\nexport const chrome = "
            + chrome
            + " as const;\n"
        )

    let cliSource language =
        runtimeEntries (isChrome >> not) language
        |> JsonSerializer.Serialize
        |> Encoding.UTF8.GetBytes

    let catalogLoaderSource () =
        [
            "// Generated by ClaimCore.ContractGenerator. Do not edit."
            "export const catalogLoaders = {"
            yield!
                LocalizationPolicy.languages
                |> List.map (fun language ->
                    "  "
                    + quote language
                    + ": () => import("
                    + quote ("./catalog-" + language)
                    + ").then(({core, chrome}) => [{default: core}, {default: chrome}] as const),")
            "} as const;"
        ]
        |> String.concat "\n"
        |> fun text -> Encoding.UTF8.GetBytes(text + "\n")

    let chromeBootstrapSource (catalogs: Map<string, Map<string, Template>>) =
        let values =
            catalogs
            |> Map.toList
            |> List.map (fun (language, catalog) ->
                language,
                JsonValue.Object
                    [
                        "loading",
                        JsonValue.String(Template.render catalog["ui.loading"] Map.empty)
                        "retry", JsonValue.String(Template.render catalog["ui.tryAgain"] Map.empty)
                        "reload",
                        JsonValue.String(Template.render catalog["ui.reloadInterface"] Map.empty)
                    ])
            |> JsonValue.Object

        Encoding.UTF8.GetBytes(
            "// Generated by ClaimCore.ContractGenerator. Do not edit.\nexport const bootMessages = "
            + json values
            + " as const;\n"
        )
