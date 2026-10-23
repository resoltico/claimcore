namespace ClaimCore.ContractGeneration

open ClaimCore.Contract

open System.Text
open System.Text.Json
open ClaimCore.Contract.Protocol

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

    let private endpointMap name schema =
        Endpoint.all
        |> List.map (fun endpoint ->
            "  readonly "
            + quote (Endpoint.id endpoint)
            + ": "
            + value (schema endpoint |> fst)
            + ";")
        |> String.concat "\n"
        |> fun members -> "export type " + name + " = {\n" + members + "\n};\n"

    let private endpoints () =
        Endpoint.all
        |> List.map (fun endpoint ->
            {|
                id = Endpoint.id endpoint
                method = Endpoint.method endpoint
                path = Endpoint.path endpoint
                mutates = Endpoint.mutates endpoint
                receiptScope =
                    Endpoint.receiptScope endpoint
                    |> Option.map (function
                        | ReceiptScopeKind.Case -> "CASE"
                        | ReceiptScopeKind.Authority -> "AUTHORITY")
            |})
        |> JsonSerializer.Serialize

    let private hostPolicies () =
        HostFailures.all
        |> List.map (fun reason ->
            {|
                code = HostFailures.code reason
                status = HostFailures.status reason
                diagnosticId = HostFailures.diagnosticId reason
                executionPhase = HostFailures.phase reason |> Option.map DeliveryTokens.phase
            |})
        |> JsonSerializer.Serialize

    let private profile () =
        {|
            maximumResponseBytes = Limits.MaximumResponseBytes
            maximumRequestBytes = Limits.MaximumRequestBytes
            maximumInvocationBytes = Limits.MaximumInvocationBytes
            maximumDepth = StrictJson.MaximumDepth
            maximumSafeInteger = ClaimCore.Contract.JsonValue.MaximumInteger
        |}
        |> JsonSerializer.Serialize

    let rec private descriptorType name (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            value.EnumerateObject()
            |> Seq.map (fun property ->
                "readonly "
                + quote property.Name
                + ": "
                + descriptorType property.Name property.Value
                + ";")
            |> String.concat " "
            |> fun fields -> "{ " + fields + " }"
        | JsonValueKind.Array ->
            let alternatives =
                value.EnumerateArray()
                |> Seq.map (descriptorType name)
                |> Seq.distinct
                |> String.concat " | "

            "ReadonlyArray<" + (if alternatives = "" then "never" else alternatives) + ">"
        | JsonValueKind.String ->
            if name = "label" || name = "meaning" || name = "labelId" || name = "meaningId" then
                "string"
            else
                value.GetString() |> nonNull |> quote
        | JsonValueKind.Number -> "number"
        | JsonValueKind.True
        | JsonValueKind.False -> "boolean"
        | JsonValueKind.Null -> "null"
        | _ -> invalidOp "Unsupported descriptor value."

    let private metadataTypes () =
        use document =
            JsonDocument.Parse(System.ReadOnlyMemory<byte>(ContractDescriptors.render ()))

        "export type Descriptors = "
        + descriptorType "" document.RootElement
        + ";\n"
        + "export type ProductMetadata = { readonly descriptors: Descriptors; readonly productVersion: string; readonly contractIdentity: string };"

    let private presentationConstants () =
        [
            "export const presentationLanguageOptions = "
            + JsonSerializer.Serialize(ClaimCore.Contract.LocalizationPolicy.languageOptions)
            + " as const;"
            "export const presentationDisplayOptions = "
            + JsonSerializer.Serialize(ClaimCore.Contract.LocalizationPolicy.displayOptions)
            + " as const;"
            "export const presentationPseudolanguage = "
            + quote ClaimCore.Contract.LocalizationPolicy.pseudolanguage
            + ";"
            "export const presentationLanguages = "
            + JsonSerializer.Serialize(
                ClaimCore.Contract.LocalizationPolicy.languages
                @ [ ClaimCore.Contract.LocalizationPolicy.pseudolanguage ]
            )
            + " as const;"
            "export const presentationDisplayLocales = "
            + JsonSerializer.Serialize(ClaimCore.Contract.LocalizationPolicy.displayLocales)
            + " as const;"
            "export const presentationDefaults = { language: "
            + quote ClaimCore.Contract.LocalizationPolicy.defaultLanguage
            + ", displayLocale: "
            + quote ClaimCore.Contract.LocalizationPolicy.defaultDisplayLocale
            + " } as const;"
        ]

    let source identity =
        let definitions =
            OpenApi.definitions ()
            |> Map.toList
            |> List.map (fun (name, schema) ->
                "export type " + identifier name + " = " + value schema + ";")

        [
            "/* Generated from ClaimCore.Contract.Protocol codecs. Do not edit. */"
            yield! definitions
            metadataTypes ()
            endpointMap "RequestByEndpoint" Admission.schema
            endpointMap "ResponseByEndpoint" Replies.schema
            "export type OutcomeByEndpoint = { readonly [E in keyof ResponseByEndpoint]: ResponseByEndpoint[E][\"outcome\"] };"
            "export const correlationPolicy = "
            + Encoding.UTF8.GetString(DeliveryTables.correlation ())
            + " as const;"
            "export const deliveryPolicy = "
            + Encoding.UTF8.GetString(DeliveryTables.render ())
            + " as const;"
            "export const endpoints = " + endpoints () + " as const;"
            "export type EndpointId = (typeof endpoints)[number][\"id\"];"
            yield! presentationConstants ()
            "export const consumerContractIdentity = " + quote identity + ";"
            "export const hostFailurePolicies = " + hostPolicies () + " as const;"
            "export const strictByteProfile = " + profile () + " as const;"
            "export const limits = { maximumPageSize: "
            + string Limits.MaximumPageSize
            + ", requestBytes: "
            + string Limits.MaximumRequestBytes
            + ", responseBytes: "
            + string Limits.MaximumResponseBytes
            + " } as const;"
        ]
        |> String.concat "\n"
        |> fun text -> Encoding.UTF8.GetBytes(text + "\n")
