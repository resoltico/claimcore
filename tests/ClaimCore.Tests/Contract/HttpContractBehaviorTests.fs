module ClaimCore.Tests.HttpContractBehaviorTests

open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open global.Json.Schema
open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Contract.Protocol

let private document () =
    ClaimCore.Contract.Protocol.OpenApi.document
    |> WireWriter.bytes
    |> Encoding.UTF8.GetString
    |> JsonNode.Parse
    |> nonNull

let private child (name: string) (node: JsonNode) = node[name] |> nonNull

let private validator (document: JsonNode) (selected: JsonNode) =
    let root = JsonObject()
    root["components"] <- (document |> child "components").DeepClone()
    root["allOf"] <- JsonArray(selected)
    JsonSchema.FromText(root.ToJsonString(), BuildOptions(Dialect = Dialect.Draft202012))

let private evaluates (schema: JsonSchema) bytes =
    use value = JsonDocument.Parse(bytes: byte array)
    schema.Evaluate(value.RootElement, EvaluationOptions(RequireFormatValidation = true)).IsValid

let private resolvedResponse (document: JsonNode) status (response: JsonNode) =
    if status = "200" then
        response
    else
        let pointer = response |> child "$ref" |> fun value -> value.GetValue<string>()
        let prefix = "#/components/responses/"

        Expect.isTrue
            (pointer.StartsWith(prefix, System.StringComparison.Ordinal))
            "The host response references a local component"

        document
        |> child "components"
        |> child "responses"
        |> child (pointer.Substring(prefix.Length))

let private statuses () =
    let document = document ()

    let responses =
        document
        |> child "paths"
        |> child "/api/context"
        |> child "post"
        |> child "responses"

    for status in HostFailures.all |> List.map HostFailures.status |> List.distinct do
        let selected =
            responses
            |> child (string status)
            |> resolvedResponse document (string status)
            |> child "content"
            |> child "application/json"
            |> child "schema"

        let schema = validator document (selected.DeepClone())

        for reason in HostFailures.all do
            let failure = HostFailureCodecs.ofReason reason

            Expect.equal
                (evaluates schema (HostFailureCodecs.encode reason))
                (HostFailures.status reason = status)
                "Only the policy's actual HTTP status admits the body."

            Expect.equal
                (HostFailureCodecs.matchesStatus status failure)
                (HostFailures.status reason = status)
                "Transport applies the same fixed status policy."

let private phases () =
    let document = document ()

    let schema =
        validator
            document
            (JsonNode.Parse "{\"$ref\":\"#/components/schemas/HostFailure\"}" |> nonNull)

    for code in HostFailures.all |> List.map HostFailures.code |> List.distinct do
        for phase in
            [ None; Some ExecutionPhase.NotStarted; Some ExecutionPhase.StartedUnconfirmed ] do
            let value =
                ClaimCore.Json.JsonValue.Object
                    [
                        "code", ClaimCore.Json.JsonValue.String code
                        "phase",
                        (Codec.nullable (Codec.enumeration DeliveryTokens.phases)).Write phase
                    ]

            let expected =
                HostFailures.all
                |> List.exists (fun reason ->
                    HostFailures.code reason = code && HostFailures.phase reason = phase)

            let bytes = WireWriter.bytes value

            Expect.equal
                (HostFailureCodecs.failure.Read "$" value |> Result.isOk)
                expected
                "Decoder refuses invalid code/phase pairs, including INTERNAL/null."

            Expect.equal
                (evaluates schema bytes)
                expected
                "Independent schema admission agrees with fixed policy."

let rec private prose path =
    function
    | ClaimCore.Json.JsonValue.Object fields ->
        fields
        |> List.collect (fun (key, value) ->
            (if key = "description" then [ path, value ] else [])
            @ prose (path @ [ key ]) value)
    | ClaimCore.Json.JsonValue.Array values -> values |> List.collect (prose path)
    | _ -> []

let private fixedDescriptions () =
    let document = document ()

    for path in (document |> child "paths").AsObject() do
        for operation in (nonNull path.Value).AsObject() do
            for entry in (nonNull operation.Value |> child "responses").AsObject() do
                let response = resolvedResponse document entry.Key (nonNull entry.Value)

                Expect.equal
                    (response |> child "description" |> fun value -> value.GetValue<string>())
                    ("HTTP " + entry.Key)
                    "Description is the referenced HTTP status"

let private responseLabels () =
    let descriptions = prose [] ClaimCore.Contract.Protocol.OpenApi.document
    Expect.isNonEmpty descriptions "Required response labels are present."

    for path, value in descriptions do
        match List.rev path, value with
        | _ :: "responses" :: _, ClaimCore.Json.JsonValue.String text ->
            Expect.isTrue
                (System.Text.RegularExpressions.Regex.IsMatch(text, "^HTTP [1-5][0-9]{2}$"))
                "Descriptions contain only a fixed status label"
        | _ -> failtest "Optional prose must not enter the structural document."

    fixedDescriptions ()

    let document = document ()

    let response =
        document
        |> child "paths"
        |> child "/api/context"
        |> child "post"
        |> child "responses"
        |> child "200"

    response.AsObject().Remove "description" |> ignore

    let parsed =
        Microsoft.OpenApi.OpenApiDocument.Parse(document.ToJsonString(), "json")

    Expect.isNonEmpty
        (nonNull parsed.Diagnostic).Errors
        "Removing a required response description is rejected."

let private reachable () =
    let definitions =
        Map.ofList [ "Used", Schema.String(None, None, None, None); "Unused", Schema.Boolean ]

    Expect.equal
        (SchemaJson.reachable definitions [ Schema.Ref "Used" ] |> Map.keys |> Seq.toList)
        [ "Used" ]
        "Unreferenced definitions do not affect the published contract."

    Expect.throws
        (fun () -> SchemaJson.reachable definitions [ Schema.Ref "Missing" ] |> ignore)
        "A dangling reference cannot produce a contract."

    Expect.throws
        (fun () ->
            SchemaJson.reachable definitions [ Schema.OneOf([], Some("kind", [ "X", "Missing" ])) ]
            |> ignore)
        "Discriminator targets must resolve too."

    Expect.isFalse
        (Map.containsKey "CaseRequest" ClaimCore.Contract.Protocol.OpenApi.definitions)
        "Unscoped request definitions are not published."

let private diagnosticRevisions () =
    let document = document ()

    let selected =
        SchemaJson.value "#/components/schemas/" DiagnosticCodecs.diagnostic.Schema
        |> WireWriter.bytes
        |> Encoding.UTF8.GetString
        |> JsonNode.Parse
        |> nonNull

    let schema = validator document selected

    let value =
        """{"code":"CASE_REVISION_CONFLICT","action":"READ_CURRENT","target":null,"actualRevision":0,"limits":{}}"""

    for revision, expected in [ "0", true; "1", true; "-1", false ] do
        let changed =
            value.Replace("\"actualRevision\":0", "\"actualRevision\":" + revision)
            |> Encoding.UTF8.GetBytes

        Expect.equal
            (Codec.decode 10000 DiagnosticCodecs.diagnostic changed |> Result.isOk)
            expected
            "Diagnostic revisions can identify absent or accepted state"

        Expect.equal
            (evaluates schema changed)
            expected
            "Independent schema agrees about diagnostic revisions"

[<Tests>]
let tests =
    testList
        "registry independent conformance"
        [
            testCase "HTTP status constrains host bodies" statuses
            testCase "absent and accepted state revisions remain distinct" diagnosticRevisions
            testCase "host code and phase are constrained together" phases
            testCase "only required fixed response descriptions are emitted" responseLabels
            testCase "schema publication follows reference reachability" reachable
        ]
