namespace ClaimCore.ProtocolGeneration

open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json

open System.Text
open System.Text.Json

/// The browser receives types and immutable core policy data for generic interpreters.
module TypeScript =
    let private quote (value: string) = JsonSerializer.Serialize value
    let private identifier (value: string) = value.Replace('.', '_')

    let rec private value =
        function
        | Schema.Object([], _) -> "Readonly<Record<string, never>>"
        | Schema.Object(properties, optional) ->
            properties
            |> List.map (fun (name, schema) ->
                "readonly "
                + quote name
                + (if List.contains name optional then "?" else "")
                + ": "
                + value schema
                + ";")
            |> String.concat " "
            |> fun fields -> "{ " + fields + " }"
        | Schema.Array(item, _, _) -> "ReadonlyArray<" + value item + ">"
        | Schema.String _ -> "string"
        | Schema.Integer(minimum, maximum) -> if minimum = maximum then string minimum else "number"
        | Schema.Boolean -> "boolean"
        | Schema.Null -> "null"
        | Schema.Const token -> quote token
        | Schema.OneOf(variants, _) -> variants |> List.map value |> String.concat " | "
        | Schema.Ref name -> identifier name

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
            |})
        |> JsonSerializer.Serialize

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
            "export const presentationLanguageOptions = "
            + JsonSerializer.Serialize(ClaimCore.Protocol.LocalizationPolicy.languageOptions)
            + " as const;"
            "export const presentationDisplayOptions = "
            + JsonSerializer.Serialize(ClaimCore.Protocol.LocalizationPolicy.displayOptions)
            + " as const;"
            "export const presentationPseudolanguage = "
            + quote ClaimCore.Protocol.LocalizationPolicy.pseudolanguage
            + ";"
            "export const presentationLanguages = "
            + JsonSerializer.Serialize(
                ClaimCore.Protocol.LocalizationPolicy.languages
                @ [ ClaimCore.Protocol.LocalizationPolicy.pseudolanguage ]
            )
            + " as const;"
            "export const presentationDisplayLocales = "
            + JsonSerializer.Serialize(ClaimCore.Protocol.LocalizationPolicy.displayLocales)
            + " as const;"
            "export const presentationDefaults = { language: "
            + quote ClaimCore.Protocol.LocalizationPolicy.defaultLanguage
            + ", displayLocale: "
            + quote ClaimCore.Protocol.LocalizationPolicy.defaultDisplayLocale
            + " } as const;"
        ]

    let source identity =
        let definitions =
            ClaimCore.Protocol.OpenApi.definitions
            |> Map.toList
            |> List.map (fun (name, schema) ->
                "export type " + identifier name + " = " + value schema + ";")

        [
            "/* Generated from ClaimCore.Protocol registry codecs. Do not edit. */"
            yield! definitions
            metadataTypes ()
            ClientValidation.source ()
            endpointMap "RequestByEndpoint" false
            endpointMap "ResponseByEndpoint" true
            "export type OutcomeByEndpoint = { readonly [E in keyof ResponseByEndpoint]: ResponseByEndpoint[E][\"outcome\"] };"
            "export const endpoints = " + endpoints () + " as const;"
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
