namespace ClaimCore.ContractGeneration

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

open System.Text
open System.Text.Json

/// The browser receives types and immutable core policy data for generic interpreters.
module TypeScript =
    let private quote (value: string) = JsonSerializer.Serialize value
    let private identifier (value: string) = value.Replace('.', '_')

    let private objectType encode properties optional =
        if List.isEmpty properties then
            "Readonly<Record<string, never>>"
        else
            properties
            |> List.map (fun (name, schema) ->
                "readonly "
                + quote name
                + (if List.contains name optional then "?" else "")
                + ": "
                + encode schema
                + ";")
            |> String.concat " "
            |> fun fields -> "{ " + fields + " }"

    let rec private value =
        function
        | Schema.Object(properties, optional) -> objectType value properties optional
        | Schema.Array(item, _, _) -> "ReadonlyArray<" + value item + ">"
        | Schema.String _ -> "string"
        | Schema.Integer(minimum, maximum) -> if minimum = maximum then string minimum else "number"
        | Schema.Boolean -> "boolean"
        | Schema.Null -> "null"
        | Schema.Const token -> quote token
        | Schema.OneOf(variants, _) -> variants |> List.map value |> String.concat " | "
        | Schema.Ref name -> identifier name
        | Schema.Vocabulary(_, schema) -> value schema
        | Schema.EnumPartition(schema, _, _, _, _) -> value schema

    let private endpointMap name response =
        Api.all
        |> List.map (fun endpoint ->
            let schema =
                (if response then
                     endpoint.OutputSchema
                 else
                     endpoint.InputSchema)

            "  readonly " + quote (Api.id endpoint) + ": " + value schema + ";")
        |> String.concat "\n"
        |> fun members -> "export type " + name + " = {\n" + members + "\n};\n"

    let private endpoints () =
        Api.all
        |> List.map (fun endpoint ->
            {|
                id = Api.id endpoint
                method = (Api.method endpoint).Method
                path = Api.path endpoint
                effect = EndpointEffect.token (Api.effect endpoint)
                requiresInstallation = Api.requiresInstallation endpoint
                requiresRepresentation =
                    match endpoint.Id with
                    | EndpointId.Actor _ -> true
                    | EndpointId.Host _ -> false
            |})
        |> JsonSerializer.Serialize

    let private correlationMap () =
        Api.definitions
        |> List.map (fun definition ->
            let enabled =
                definition.Accept
                    { new IEndpointVisitor<bool> with
                        member _.Visit<'input, 'output>(endpoint: Endpoint<'input, 'output>) =
                            endpoint.Correlation.IsSome
                    }

            quote (EndpointIds.token definition.Id)
            + ": "
            + (if enabled then "true" else "false"))
        |> String.concat ", "
        |> fun members -> "{ " + members + " }"

    let private profile () =
        {|
            maximumResponseBytes = Limits.MaximumResponseBytes
            maximumRequestBytes = Limits.MaximumRequestBytes
            maximumInvocationBytes = Limits.MaximumInvocationBytes
            maximumDepth = StrictJson.MaximumDepth
            maximumSafeInteger = ClaimCore.Json.JsonValue.MaximumInteger
        |}
        |> JsonSerializer.Serialize

    let private metadataTypes () =
        let codec = CatalogCodecs.codec

        let definitions =
            codec.Definitions
            |> Map.toList
            |> List.map (fun (name, schema) ->
                "export type " + identifier name + " = " + value schema + ";")

        String.concat "\n" definitions
        + "\nexport type CatalogDocument = "
        + value codec.Schema
        + ";\n"
        + "export type ProductMetadata = { readonly catalog: CatalogDocument; readonly productVersion: string; readonly contractIdentity: string };"

    let private presentationConstants () =
        [
            "export const listedCaseFieldNames = "
            + JsonSerializer.Serialize(
                FieldPresentation.declarations
                |> List.filter (fun field -> FieldPresentation.listed field.Field)
                |> List.map _.Key
            )
            + " as const;"
            "export const presentationLanguageOptions = "
            + JsonSerializer.Serialize(ClaimCore.Presentation.LocalizationPolicy.languageOptions)
            + " as const;"
            "export const presentationDisplayOptions = "
            + JsonSerializer.Serialize(ClaimCore.Presentation.LocalizationPolicy.displayOptions)
            + " as const;"
            "export const presentationLanguageAliases = "
            + JsonSerializer.Serialize(LocalizationPolicy.languageAliases |> Map.toSeq |> dict)
            + " as const;"
            "export const presentationPseudolanguage = "
            + quote ClaimCore.Presentation.LocalizationPolicy.pseudolanguage
            + ";"
            "export const presentationLanguages = "
            + JsonSerializer.Serialize(
                ClaimCore.Presentation.LocalizationPolicy.languages
                @ [ ClaimCore.Presentation.LocalizationPolicy.pseudolanguage ]
            )
            + " as const;"
            "export const presentationDisplayLocales = "
            + JsonSerializer.Serialize(ClaimCore.Presentation.LocalizationPolicy.displayLocales)
            + " as const;"
            "export const resourceUnavailableDiagnostic = "
            + (DiagnosticCodecs.diagnostic.Write(Refusal.Access AccessRefusal.ResourceUnavailable)
               |> ClaimCore.Json.WireWriter.bytes
               |> System.Text.Encoding.UTF8.GetString)
            + " as const;"
            "export const capabilityRoleRequirements = "
            + JsonSerializer.Serialize(
                Capability.all
                |> List.map (fun capability ->
                    Capability.token capability,
                    Authority.rolesFor capability |> List.map Role.token)
                |> dict
            )
            + " as const;"
            "export const presentationAccents = "
            + JsonSerializer.Serialize(ClaimCore.Presentation.LocalizationPolicy.accents)
            + " as const;"
            "export const presentationModes = "
            + JsonSerializer.Serialize(ClaimCore.Presentation.LocalizationPolicy.modes)
            + " as const;"
            "export const presentationDefaults = { language: "
            + quote ClaimCore.Presentation.LocalizationPolicy.defaultLanguage
            + ", displayLocale: "
            + quote ClaimCore.Presentation.LocalizationPolicy.defaultDisplayLocale
            + " } as const;"
        ]

    let source identity =
        let definitions =
            ClaimCore.Wire.OpenApi.definitions
            |> Map.toList
            |> List.map (fun (name, schema) ->
                "export type " + identifier name + " = " + value schema + ";")

        [
            "/* Generated from ClaimCore task declarations and Wire codecs. Do not edit. */"
            yield! definitions
            metadataTypes ()
            TaskAssemblers.source ()
            ClientValidation.source ()
            endpointMap "RequestByEndpoint" false
            endpointMap "ResponseByEndpoint" true
            "export type OutcomeByEndpoint = { readonly [E in keyof ResponseByEndpoint]: ResponseByEndpoint[E][\"outcome\"] };"
            "export const representationHeader = " + quote Representation.HeaderName + ";"
            "export const representationOptions = "
            + JsonSerializer.Serialize(
                Vocabulary.all Representation.vocabulary |> List.map Representation.token
            )
            + " as const;"
            "export type Representation = (typeof representationOptions)[number];"
            "export const endpoints = " + endpoints () + " as const;"
            "export const correlationOf = " + correlationMap () + " as const;"
            "export type EndpointId = (typeof endpoints)[number][\"id\"];"
            yield! presentationConstants ()
            "export const consumerContractIdentity = " + quote identity + ";"
            "export const strictByteProfile = " + profile () + " as const;"
            "export const limits = { maximumPageSize: "
            + string Paging.MaximumPageSize
            + ", requestBytes: "
            + string Limits.MaximumRequestBytes
            + ", responseBytes: "
            + string Limits.MaximumResponseBytes
            + " } as const;"
        ]
        |> String.concat "\n"
        |> fun text -> Encoding.UTF8.GetBytes(text + "\n")
