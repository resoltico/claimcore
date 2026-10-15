namespace ClaimCore.Contracts

open System
open System.Security.Cryptography
open System.Text
open WireSchema

type Endpoint =
    {
        Identifier: string
        Method: string
        Path: string
        Mutates: bool
        Request: SchemaDocument
        Response: SchemaDocument
    }

module EndpointCatalog =
    let private endpoint (identifier, mutates) =
        {
            Identifier = identifier
            Method = (if identifier = "session" then "GET" else "POST")
            Path = "/api/" + identifier
            Mutates = mutates
            Request =
                document
                    ("endpoint." + identifier + ".request.schema.json")
                    (RequestSchemas.forEndpoint identifier)
                    []
            Response =
                document
                    ("endpoint." + identifier + ".response.schema.json")
                    (ResponseSchemas.envelope identifier)
                    ValueSchemas.definitions
        }

    let all =
        [
            "session", false
            "session.logout", false
            "context", false
            "case.get", false
            "case.list", false
            "case.history", false
            "case.preview", false
            "case.execute", true
            "operation.observe", false
            "authority.execute", true
            "authority.list", false
        ]
        |> List.map endpoint

    let tryFind identifier =
        all |> List.tryFind (fun value -> value.Identifier = identifier)

    let schema document =
        CanonicalJson.renderSchema document document.Root

    let responseDefinitions =
        ValueSchemas.definitions
        @ (all |> List.map (fun e -> e.Identifier, e.Response.Root))

    let responses =
        document
            "responses.schema.json"
            (all |> List.map _.Response.Root |> Schema.oneOf)
            responseDefinitions

    let invocation =
        document
            "invocation.schema.json"
            (all
             |> List.map (fun e ->
                 objectOf
                     [ property "endpoint" (token e.Identifier); property "input" e.Request.Root ])
             |> Schema.oneOf)
            []

    let frameResponse =
        document
            "frame-response.schema.json"
            (FrameSchemas.response (
                all
                |> List.map (fun e ->
                    e.Identifier, e.Request.Root, ResponseSchemas.outcome e.Identifier)
            ))
            ValueSchemas.definitions

    let metadata () =
        ContractJson.contract (fun writer ->
            writer.WriteStartArray()

            for endpoint in all do
                writer.WriteStartObject()
                writer.WriteString("id", endpoint.Identifier)
                writer.WriteString("method", endpoint.Method)
                writer.WriteString("path", endpoint.Path)
                writer.WriteBoolean("mutates", endpoint.Mutates)

                writer.WriteString(
                    "requestSchema",
                    "endpoint." + endpoint.Identifier + ".request.schema.json"
                )

                writer.WriteString(
                    "responseSchema",
                    "endpoint." + endpoint.Identifier + ".response.schema.json"
                )

                writer.WriteEndObject()

            writer.WriteEndArray())

    let identityInputs () =
        [
            "catalogue-metadata", metadata ()
            "descriptors.json", ContractDescriptors.render ()
            "default-presentation.en.json", DefaultPresentation.render ()
        ]
        @ (all
           |> List.collect (fun endpoint ->
               [
                   "endpoint." + endpoint.Identifier + ".request.schema.json",
                   schema endpoint.Request
                   "endpoint." + endpoint.Identifier + ".response.schema.json",
                   schema endpoint.Response
               ]))
        @ [
            "responses.schema.json", schema responses
            "invocation.schema.json", schema invocation
            "frame-response.schema.json", schema frameResponse
            "host-failure.schema.json", schema HostFailures.schema
        ]
        |> List.map (fun (name, value) -> name, CanonicalContract.bytes value)

    let fingerprint =
        identityInputs ()
        |> ContractIdentity.fingerprint
        |> WireContractFingerprint.create

    let matches value =
        value = Some(WireContractFingerprint.value fingerprint)

    let admitRequest endpoint bytes =
        match tryFind endpoint with
        | None -> Error HttpInputProblem.InvalidAction
        | Some item ->
            HttpInputSupport.parse bytes (fun root ->
                if not (SchemaValueValidation.verify item.Request root) then
                    HttpInputSupport.fail HttpInputProblem.InvalidJson

                ServiceInputs.read endpoint root)

    let admitResponse endpoint bytes =
        match tryFind endpoint with
        | None -> Error HttpInputProblem.InvalidAction
        | Some item ->
            HttpInputSupport.parse bytes (fun root ->
                if not (SchemaValueValidation.verify item.Response root) then
                    HttpInputSupport.fail HttpInputProblem.InvalidJson

                root.Clone())
