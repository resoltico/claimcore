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
            "definition", false
            "case.get", false
            "case.list", false
            "case.history", false
            "operation.observe", false
            "command.preview", false
            "command.execute", true
            "case.steward", true
            "authority.register", true
            "authority.setGrant", true
            "authority.setEnabled", true
            "authority.observe", false
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

    let private identity () =
        all
        |> List.map (fun e ->
            e.Identifier
            + "\n"
            + e.Method
            + "\n"
            + e.Path
            + "\n"
            + string e.Mutates
            + "\n"
            + (schema e.Request |> CanonicalContract.text)
            + (schema e.Response |> CanonicalContract.text))
        |> String.concat ""
        |> fun endpoints ->
            endpoints
            + (schema frameResponse |> CanonicalContract.text)
            + (schema HostFailures.schema |> CanonicalContract.text)

    let fingerprint =
        identity ()
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower
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
