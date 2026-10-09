module ClaimCore.Tests.ScalarInvocationDiagnosticTests

open System
open System.Text.Json
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Cli
open ClaimCore.Contracts
open ClaimCore.Application

let private source () =
    JsonNode.Parse(
        """{"protocolVersion":4,"endpoint":"command.prepare","input":{"operationId":"10000000-0000-4000-8000-000000000001","caseReference":"SCALAR-DIAGNOSTIC","expectedRevision":"0","command":{"kind":"OPEN","values":{"incidentDate":"2026-08-01","incidentNotificationDate":"2026-08-03","incidentCountry":"Latvia","claimantName":"Synthetic Claimant","insurerName":"Synthetic Insurer","claimedAmount":"1","claimedCurrency":"EUR"}}}}"""
    )
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Synthetic JSON root is required.")

let private decodeText (value: string) =
    match StrictJson.parseDocument 131072 (Encoding.UTF8.GetBytes(value)) with
    | Error failure -> Error failure
    | Ok document ->
        use owner = document
        CliRemoteInvocation.decode owner.RootElement

let private decode (value: JsonNode) = decodeText (value.ToJsonString())

let private objectAt (value: JsonNode) (name: string) =
    value[name]
    |> Option.ofObj
    |> Option.map _.AsObject()
    |> Option.defaultWith (fun () -> failtest "Synthetic declared object is required.")

let private input frame = objectAt frame "input"
let private command frame = objectAt (input frame) "command"
let private values frame = objectAt (command frame) "values"

let private businessScalars () =
    for field, value, id, parameters in
        [
            "claimedAmount",
            "-1",
            "INPUT_DECIMAL_FORMAT",
            [ "maximumIntegerDigits", 18; "maximumFractionalDigits", 4 ]
            "claimedAmount",
            "1e3",
            "INPUT_DECIMAL_FORMAT",
            [ "maximumIntegerDigits", 18; "maximumFractionalDigits", 4 ]
            "claimedAmount",
            "01",
            "INPUT_DECIMAL_FORMAT",
            [ "maximumIntegerDigits", 18; "maximumFractionalDigits", 4 ]
            "incidentDate", "2026-02-30", "INPUT_CALENDAR_DATE_REQUIRED", []
            "claimedCurrency", "eur", "INPUT_CURRENCY_FORMAT", []
            "claimantName", String('a', 201), "INPUT_TEXT_TOO_LONG", [ "maximumCharacters", 200 ]
            "incidentCountry", String('a', 101), "INPUT_TEXT_TOO_LONG", [ "maximumCharacters", 100 ]
            "claimantName", "", "INPUT_TEXT_REQUIRED", []
            "claimantName", String('a', 201) + " ", "INPUT_SURROUNDING_WHITESPACE", []
            "claimedCurrency", "EURO", "INPUT_TEXT_TOO_LONG", [ "maximumCharacters", 3 ]
            "claimedAmount", String('9', 24), "INPUT_TEXT_TOO_LONG", [ "maximumCharacters", 23 ]
        ] do
        let frame = source ()
        (values frame)[field] <- JsonValue.Create(value)

        match decode frame with
        | Error failure ->
            Expect.equal
                failure.Path
                ("/input/command/values/" + field)
                "Actual registered scalar field"

            Expect.equal
                failure.Reason
                ProtocolProblem.InvalidScalar
                "Scalar admission has a distinct transport cause"

            let diagnostic =
                failure.ScalarDiagnostic
                |> Option.defaultWith (fun () -> failtest "Scalar constraint is required.")

            Expect.equal
                (RejectionDiagnostics.identifier diagnostic |> RejectionDiagnosticIds.token)
                id
                "Shared business constraint identity"

            Expect.equal
                (RejectionDiagnostics.values diagnostic |> Map.ofList)
                (Map.ofList parameters)
                "Constraint limits originate at the scalar owner"
        | Ok _ -> failtest "Invalid scalar must not reach service dispatch."

let private structuralSafety () =
    let frame = source ()
    (command frame)["kind"] <- JsonValue.Create("UNDECLARED")

    match decode frame with
    | Error failure ->
        Expect.equal failure.Path "/input/command/kind" "Known discriminator is located"
    | _ -> failtest "Unknown command discriminator must refuse."

    let hostile = source ()
    (values hostile)["private/value"] <- JsonValue.Create("secret-like-input")

    match decode hostile with
    | Error failure ->
        Expect.equal
            failure.Path
            "/input/command/values"
            "Unknown key collapses to its known container"

        Expect.isNone failure.ScalarDiagnostic "Unknown members cannot invent a scalar cause"
    | _ -> failtest "Unknown member must refuse."

let private futureRemainsRemote () =
    let frame = source ()
    (values frame)["incidentDate"] <- JsonValue.Create("9999-12-30")

    (values frame)["incidentNotificationDate"] <- JsonValue.Create("9999-12-31")

    Expect.isOk
        (decode frame)
        "Transport syntax cannot decide the stored installation's business date"

let private protocolCorpus () =
    let model = ContractProjection.current ()
    let _, bytes = ClaimCore.ContractGeneration.CliResponseCorpus.artifact model
    use corpus = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    let mutable accepted = 0
    let mutable refused = 0

    for item in corpus.RootElement.GetProperty("cases").EnumerateArray() do
        let id =
            item.GetProperty("id").GetString()
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failtest "Corpus identity is required.")

        if id.StartsWith("protocol-scalar-", StringComparison.Ordinal) then
            let expected = item.GetProperty("valid").GetBoolean()

            Expect.equal
                (SchemaValueValidation.verify
                    (CliSchemas.responseDocument model)
                    (item.GetProperty("value")))
                expected
                id

            if expected then
                accepted <- accepted + 1
            else
                refused <- refused + 1

    Expect.isGreaterThan accepted 0 "Actual scalar protocol frames were admitted"
    Expect.isGreaterThan refused accepted "Hostile metadata counterparts were refused"

let private groupDiscriminators () =
    for group in [ "registration"; "decision"; "payment" ] do
        let frame = source ()

        (input frame)["command"] <-
            JsonNode.Parse(
                """{"kind":"CORRECT_CASE","groups":{"registration":{"mode":"KEEP"},"decision":{"mode":"KEEP"},"payment":{"mode":"KEEP"}}}"""
            )

        let target = objectAt (objectAt (command frame) "groups") group
        target["mode"] <- JsonValue.Create("UNKNOWN_MODE")

        match decode frame with
        | Error failure ->
            Expect.equal
                failure.Path
                ("/input/command/groups/" + group + "/mode")
                "Selected group discriminator is known"
        | _ -> failtest "Unknown correction mode must refuse."

    for raw in
        [
            """{"protocolVersion":4,"endpoint":"case.get","input":{"caseReference":"A","caseReference":"B"}}"""
            """{"protocolVersion":4,"endpoint":"case.get","input":{"caseReference":"\ud800"}}"""
        ] do
        Expect.isError
            (decodeText raw)
            "Actual strict parsing refuses duplicates and malformed Unicode"

let tests =
    testList
        "native scalar admission diagnostics"
        [
            testCase
                "[CC-CLI-001] every correction group diagnoses its discriminator through strict parsing"
                groupDiscriminators
            testCase
                "[CC-CLI-001] scalar protocol metadata is correlated and excludes nonlocal diagnostic causes"
                protocolCorpus
            testCase
                "[CC-CLI-001] malformed business scalars identify their known field and shared constraint"
                businessScalars
            testCase
                "[CC-CLI-001] discriminator and unknown member diagnostics never reflect authored keys"
                structuralSafety
            testCase
                "[CC-DOM-001] future-date policy remains service-owned after local scalar admission"
                futureRemainsRemote
        ]
