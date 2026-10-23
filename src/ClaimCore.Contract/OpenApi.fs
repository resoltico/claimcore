namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

open System
open System.IO
open System.Text.Json

/// OpenAPI is a projection of codecs and transport constants. Descriptors and messages stay outside it.
module OpenApi =
    let private prefix = "#/components/schemas/"

    let private text writer name value =
        WriterProfile.writeMember writer name value

    let private merge left right =
        Map.fold
            (fun state key value ->
                match Map.tryFind key state with
                | Some existing when existing <> value ->
                    invalidOp "OpenAPI component definitions disagree."
                | _ -> Map.add key value state)
            left
            right

    let private content (writer: Utf8JsonWriter) schema =
        writer.WritePropertyName "content"
        writer.WriteStartObject()
        writer.WritePropertyName "application/json"
        writer.WriteStartObject()
        writer.WritePropertyName "schema"
        Codec.writeSchema prefix writer schema
        writer.WriteEndObject()
        writer.WriteEndObject()

    let private parameter (writer: Utf8JsonWriter) name required schema =
        writer.WriteStartObject()
        text writer "name" name
        text writer "in" "header"
        writer.WriteBoolean("required", required)
        writer.WritePropertyName "schema"
        Codec.writeSchema prefix writer schema
        writer.WriteEndObject()

    let private parameters (writer: Utf8JsonWriter) endpoint =
        writer.WritePropertyName "parameters"
        writer.WriteStartArray()
        parameter writer "ClaimCore-Contract" true ResponseScalars.digest.Schema

        if endpoint <> Endpoint.Session then
            parameter writer "X-ClaimCore-Antiforgery" false ResponseScalars.nonempty.Schema

        writer.WriteEndArray()

    let private security (writer: Utf8JsonWriter) endpoint =
        writer.WritePropertyName "security"
        writer.WriteStartArray()

        if endpoint = Endpoint.Session then
            writer.WriteStartObject()
            writer.WriteEndObject()

        writer.WriteStartObject()
        writer.WritePropertyName "cookieAuth"
        writer.WriteStartArray()
        writer.WriteEndArray()

        if endpoint <> Endpoint.Session then
            writer.WritePropertyName "antiforgery"
            writer.WriteStartArray()
            writer.WriteEndArray()

        writer.WriteEndObject()

        if endpoint <> Endpoint.SessionLogout then
            writer.WriteStartObject()
            writer.WritePropertyName "bearerAuth"
            writer.WriteStartArray()
            writer.WriteEndArray()
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private responses (writer: Utf8JsonWriter) endpoint =
        writer.WritePropertyName "responses"
        writer.WriteStartObject()
        writer.WritePropertyName "200"
        writer.WriteStartObject()
        text writer "description" "HTTP 200"
        content writer (Replies.schema endpoint |> fst)
        writer.WriteEndObject()

        for status in HostFailures.all |> List.map HostFailures.status |> List.distinct |> List.sort do
            writer.WritePropertyName(status.ToString(Globalization.CultureInfo.InvariantCulture))
            writer.WriteStartObject()
            text writer "description" ("HTTP " + string status)
            content writer (HostFailures.responseSchema status)
            writer.WriteEndObject()

        writer.WriteEndObject()

    let private operation (writer: Utf8JsonWriter) endpoint =
        writer.WritePropertyName((Endpoint.method endpoint).ToLowerInvariant())
        writer.WriteStartObject()
        text writer "operationId" (Endpoint.id endpoint)
        parameters writer endpoint
        security writer endpoint

        if endpoint <> Endpoint.Session then
            writer.WritePropertyName "requestBody"
            writer.WriteStartObject()
            writer.WriteBoolean("required", true)
            content writer (Admission.schema endpoint |> fst)
            writer.WriteEndObject()

        responses writer endpoint
        writer.WriteEndObject()

    let private paths (writer: Utf8JsonWriter) =
        writer.WritePropertyName "paths"
        writer.WriteStartObject()

        for endpoint in Endpoint.all do
            writer.WritePropertyName(Endpoint.path endpoint)
            writer.WriteStartObject()
            operation writer endpoint
            writer.WriteEndObject()

        writer.WriteEndObject()

    let private securitySchemes (writer: Utf8JsonWriter) =
        writer.WritePropertyName "securitySchemes"
        writer.WriteStartObject()
        writer.WritePropertyName "cookieAuth"
        writer.WriteStartObject()
        text writer "type" "apiKey"
        text writer "in" "cookie"
        text writer "name" "__Host-ClaimCoreSession"
        writer.WriteEndObject()
        writer.WritePropertyName "antiforgery"
        writer.WriteStartObject()
        text writer "type" "apiKey"
        text writer "in" "header"
        text writer "name" "X-ClaimCore-Antiforgery"
        writer.WriteEndObject()
        writer.WritePropertyName "bearerAuth"
        writer.WriteStartObject()
        text writer "type" "http"
        text writer "scheme" "bearer"
        writer.WriteEndObject()
        writer.WriteEndObject()

    let definitions () =
        Endpoint.all
        |> List.fold
            (fun state endpoint ->
                state
                |> merge (Admission.schema endpoint |> snd)
                |> merge (Replies.schema endpoint |> snd))
            HostFailures.codec.Definitions

    let private components (writer: Utf8JsonWriter) =
        writer.WritePropertyName "components"
        writer.WriteStartObject()
        securitySchemes writer
        writer.WritePropertyName "schemas"
        writer.WriteStartObject()

        for KeyValue(name, schema) in definitions () do
            writer.WritePropertyName name
            Codec.writeSchema prefix writer schema

        writer.WriteEndObject()
        writer.WriteEndObject()

    let emit () =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, WriterProfile.options true)
        writer.WriteStartObject()
        text writer "openapi" "3.1.0"
        text writer "jsonSchemaDialect" "https://json-schema.org/draft/2020-12/schema"
        writer.WritePropertyName "info"
        writer.WriteStartObject()
        text writer "title" "ClaimCore"
        text writer "version" "1"
        writer.WriteEndObject()
        paths writer
        components writer
        writer.WriteEndObject()
        writer.Flush()
        Array.append (stream.ToArray()) [| byte '\n' |]
