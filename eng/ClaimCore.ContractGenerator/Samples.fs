namespace ClaimCore.ContractGeneration

open System.Text.Json.Nodes
open ClaimCore.Contract
open ClaimCore.Contract.Protocol

/// Samples exercise each declared alternative without multiplying unrelated field alternatives.
module SchemaSamples =
    let private json value =
        (JsonValue.Create(value: string) |> nonNull) :> JsonNode

    let private text (pattern: string option) (format: string option) minimum =
        match format, pattern with
        | Some "uuid", _ -> "10000000-0000-4000-8000-000000000001"
        | Some "date-time", _ -> "2026-01-01T00:00:00.000000Z"
        | Some "date", _ -> "2026-01-01"
        | Some "uri", _ -> "https://identity.test/"
        | _, Some value when value.Contains("[0-9a-f]{64}") -> System.String('0', 64)
        | _, Some value when value.Contains("[A-Z]{2}") -> "ZZ"
        | _, Some value when value.Contains("[A-Z]{3}") -> "EUR"
        | _, Some value when value.Contains("[!-~]") -> "CASE-001"
        | _, Some value when value.Contains("[1-9][0-9]") -> "0"
        | _ -> System.String('x', defaultArg minimum 1)

    let private clone (value: JsonNode | null) : JsonNode | null =
        match value with
        | null -> null
        | value -> value.DeepClone()

    let private array values =
        let node = JsonArray()

        for item in values do
            node.Add(clone item)

        node :> JsonNode

    let private objectOf (values: (string * (JsonNode | null)) list) =
        let node = JsonObject()

        for name, item in values do
            node[name] <- clone item

        node :> JsonNode

    let private objects properties =
        let baseline = properties |> List.map (fun (name, values) -> name, List.head values)

        [
            yield objectOf baseline
            for name, values in properties do
                for value in List.tail values do
                    yield
                        baseline
                        |> List.map (fun (key, original) ->
                            key, if key = name then value else original)
                        |> objectOf
        ]

    let rec values definitions schema : (JsonNode | null) list =
        match schema with
        | Schema.Ref name -> values definitions (Map.find name definitions)
        | Schema.Const value -> [ json value ]
        | Schema.Null -> [ null ]
        | Schema.Boolean ->
            [ JsonValue.Create(false) :> JsonNode; JsonValue.Create(true) :> JsonNode ]
        | Schema.Integer(minimum, maximum) ->
            [ JsonValue.Create(minimum) :> JsonNode; JsonValue.Create(maximum) :> JsonNode ]
        | Schema.String(pattern, format, minimum, _) -> [ text pattern format minimum |> json ]
        | Schema.OneOf(alternatives, _) -> alternatives |> List.collect (values definitions)
        | Schema.Array(item, minimum, maximum) ->
            [
                if minimum = 0 then
                    yield array []
                if maximum > 0 then
                    for sample in values definitions item do
                        yield array (List.replicate (max 1 minimum) sample)
            ]
        | Schema.Object(properties, _) ->
            properties
            |> List.map (fun (name, schema) -> name, values definitions schema)
            |> objects

    let private distinct (values: (JsonNode | null) list) =
        values
        |> List.distinctBy (fun value ->
            match value with
            | null -> "null"
            | value -> value.ToJsonString())

    let private cases endpoint direction (schema, definitions) =
        let samples = values definitions schema |> distinct

        [
            for index, sample in List.indexed samples do
                let node = JsonObject()
                node["id"] <- json (Endpoint.id endpoint + "-" + direction + "-" + string index)
                node["endpoint"] <- json (Endpoint.id endpoint)
                node["direction"] <- json direction
                node["valid"] <- JsonValue.Create(true)
                node["value"] <- clone sample
                yield node :> JsonNode
            for value in [ JsonValue.Create(false) :> JsonNode; array [] ] do
                let node = JsonObject()

                node["id"] <-
                    json (Endpoint.id endpoint + "-" + direction + "-wrong-kind-" + string value)

                node["endpoint"] <- json (Endpoint.id endpoint)
                node["direction"] <- json direction
                node["valid"] <- JsonValue.Create(false)
                node["value"] <- value
                yield node :> JsonNode
        ]

    let corpus () =
        let root = JsonObject()

        root["cases"] <-
            Endpoint.all
            |> List.collect (fun endpoint ->
                cases endpoint "request" (Admission.schema endpoint)
                @ cases endpoint "response" (Replies.schema endpoint))
            |> array

        root.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = false))
        + "\n"
        |> System.Text.Encoding.UTF8.GetBytes
