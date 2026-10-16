namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open System
open System.Security.Cryptography
open System.Text
open WireSchema

type Endpoint =
    {
        Identifier: string
        Method: string
        Path: string
        AcceptingScope: string
        ReceiptScope: string
        Mutates: bool
        Request: SchemaDocument
        Response: SchemaDocument
    }

module EndpointCatalog =
    let private endpoint (identifier, acceptingScope) =
        {
            Identifier = identifier
            Method = (if identifier = "session" then "GET" else "POST")
            Path = "/api/" + identifier
            AcceptingScope = acceptingScope
            ReceiptScope = ResponseSchemas.receiptScope identifier
            Mutates = acceptingScope <> "NONE"
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
            "session", "NONE"
            "session.logout", "NONE"
            "context", "NONE"
            "case.get", "NONE"
            "case.list", "NONE"
            "case.history", "NONE"
            "case.preview", "NONE"
            "case.execute", "CASE"
            "operation.observe", "NONE"
            "authority.execute", "AUTHORITY"
            "authority.list", "NONE"
            "authority.grants", "NONE"
        ]
        |> List.map endpoint

    let tryFind identifier =
        all |> List.tryFind (fun value -> value.Identifier = identifier)

    let schema document =
        SchemaRendering.renderSchema document document.Root

    let responsesFor (endpoints: Endpoint list) =
        let definitions =
            endpoints
            |> List.collect (fun endpoint -> endpoint.Response.Definitions)
            |> List.distinct

        document
            "responses.schema.json"
            (endpoints |> List.map _.Response.Root |> Schema.oneOf)
            (definitions
             @ (endpoints
                |> List.map (fun endpoint -> endpoint.Identifier, endpoint.Response.Root)))

    let responses = responsesFor all

    let invocationFor (endpoints: Endpoint list) =
        document
            "invocation.schema.json"
            (endpoints
             |> List.map (fun e ->
                 objectOf
                     [ property "endpoint" (token e.Identifier); property "input" e.Request.Root ])
             |> Schema.oneOf)
            []

    let invocation = invocationFor all

    let frameResponseFor (endpoints: Endpoint list) =
        let outcome (endpoint: Endpoint) =
            match endpoint.Response.Root with
            | ObjectSchema value ->
                value.Properties
                |> List.find (fun property -> property.Name = "outcome")
                |> _.Schema
            | _ -> invalidArg "endpoints" "An endpoint response must be an object envelope."

        let definitions =
            endpoints
            |> List.collect (fun endpoint -> endpoint.Response.Definitions)
            |> List.distinct

        document
            "frame-response.schema.json"
            (FrameSchemas.response (
                endpoints
                |> List.map (fun endpoint ->
                    endpoint.Identifier, endpoint.Request.Root, outcome endpoint)
            ))
            definitions

    let frameResponse = frameResponseFor all

    let metadataFor (endpoints: Endpoint list) =
        ContractJson.contract (fun writer ->
            writer.WriteStartArray()

            for endpoint in endpoints do
                writer.WriteStartObject()
                writer.WriteString("id", endpoint.Identifier)
                writer.WriteString("method", endpoint.Method)
                writer.WriteString("path", endpoint.Path)
                writer.WriteBoolean("mutates", endpoint.Mutates)
                writer.WriteString("acceptingScope", endpoint.AcceptingScope)
                writer.WriteString("receiptScope", endpoint.ReceiptScope)

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

    let metadata () = metadataFor all

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
        |> fun values ->
            values
            @ [
                "strict-byte-profile.json", ProtocolProfile.strictBytesJson ()
                "delivery-policy.json", ProtocolProfile.deliveryJson ()
            ]

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
