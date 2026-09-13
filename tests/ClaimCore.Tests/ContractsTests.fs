module ClaimCore.Tests.ContractsTests

open System
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain

let private cliEndpointIds =
    [
        "command.prepare"
        "command.execute"
        "case.get"
        "case.list"
        "case.history"
        "operation.observe"
        "recovery.list"
        "recovery.inspect"
        "recovery.resolve"
        "recovery.dismiss"
        "recovery.export"
        "recovery.importEnvelopePreview"
        "recovery.importEnvelopeRetain"
        "recovery.importRecordPreview"
        "recovery.importRecordRetain"
    ]

let private webEndpointIds =
    [
        "session"
        "session.login"
        "session.logout"
        "definition"
        "case.get"
        "case.list"
        "case.history"
        "operation.observe"
        "command.prepare"
        "command.execute"
        "recovery.list"
        "recovery.inspect"
        "recovery.resolve"
        "recovery.dismiss"
        "recovery.export"
        "recovery.importEnvelopePreview"
        "recovery.importEnvelopeRetain"
        "recovery.importRecordPreview"
        "recovery.importRecordRetain"
    ]

let private projection () = ContractProjection.current ()

let rec private containsEmptyPrefixItems (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.Object ->
        let mutable prefixItems = JsonElement()

        (element.TryGetProperty("prefixItems", &prefixItems)
         && prefixItems.ValueKind = JsonValueKind.Array
         && prefixItems.GetArrayLength() = 0)
        || (element.EnumerateObject()
            |> Seq.exists (fun property -> containsEmptyPrefixItems property.Value))
    | JsonValueKind.Array -> element.EnumerateArray() |> Seq.exists containsEmptyPrefixItems
    | _ -> false

let private expectCanonicalJson (contract: CanonicalContract) =
    let bytes = CanonicalContract.bytes contract

    Expect.isGreaterThan bytes.Length 1 "Canonical contract has content"
    Expect.equal bytes.[bytes.Length - 1] (byte '\n') "One final LF"
    Expect.isFalse (bytes.[0] = 0xEFuy) "UTF-8 BOM is forbidden"

    use parsed = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    Expect.equal parsed.RootElement.ValueKind JsonValueKind.Object "Canonical JSON object"

    Expect.isFalse
        (containsEmptyPrefixItems parsed.RootElement)
        "Draft 2020-12 forbids an empty prefixItems array"

let private expectFingerprintSensitivity model cli web =
    let changedCli =
        { model with
            CliResponses = model.CliResponses |> Map.add "case.list" Schema.nullValue
        }
        |> ContractRenderers.cliFingerprint
        |> CliWireContractFingerprint.value

    let first = model.WebEndpoints |> List.head

    let changedWeb =
        { model with
            WebEndpoints =
                { first with
                    Response = Schema.nullValue
                }
                :: (model.WebEndpoints |> List.tail)
        }
        |> ContractRenderers.webFingerprint
        |> WebWireContractFingerprint.value

    Expect.notEqual changedCli cli "CLI response schema participates in its fingerprint"
    Expect.notEqual changedWeb web "Web response schema participates in its fingerprint"

let private endpointCatalogTests =
    testList
        "endpoint inventories"
        [
            testCase "projects every CLI v3 endpoint in its stable order" (fun () ->
                let actual = (projection ()).CliEndpoints |> List.map _.Identifier
                Expect.equal actual cliEndpointIds "Complete CLI endpoint catalog")
            testCase "projects every HTTP v2 endpoint in its stable order" (fun () ->
                let actual = (projection ()).WebEndpoints |> List.map _.Identifier
                Expect.equal actual webEndpointIds "Complete HTTP endpoint catalog")
            testCase
                "models Web imports as raw media and keeps browser export free of CLI paths"
                (fun () ->
                    let model = projection ()

                    let raw =
                        model.WebEndpoints
                        |> List.filter (fun endpoint ->
                            endpoint.Identifier.StartsWith("recovery.import"))

                    raw
                    |> List.iter (fun endpoint ->
                        match endpoint.Body with
                        | Some(RawBody(_, maximumBytes, _)) ->
                            Expect.isGreaterThan maximumBytes 0 "Bounded raw import"
                        | _ -> failtest "Recovery imports must be raw media bodies.")

                    Expect.isFalse
                        (ContractRenderers.web model
                         |> CanonicalContract.text
                         |> _.Contains("\"destination\""))
                        "Web export is a download, not a CLI destination write")
        ]

let private canonicalRenderingTests =
    testList
        "canonical renderers"
        [
            testCase "renders deterministic strict UTF-8 semantic and wire contracts" (fun () ->
                let model = projection ()
                let semantic = ContractRenderers.semantic model
                let semanticSchema = ContractRenderers.semanticSchema model
                let cli = ContractRenderers.cli model
                let web = ContractRenderers.web model

                [ semantic; semanticSchema; cli; web ] |> List.iter expectCanonicalJson

                Expect.equal
                    (ContractRenderers.cli model |> CanonicalContract.bytes)
                    (ContractRenderers.cli model |> CanonicalContract.bytes)
                    "CLI canonical bytes are stable")
            testCase "uses Application's semantic fingerprint authority" (fun () ->
                let model = projection ()

                Expect.equal
                    (ContractRenderers.semanticFingerprint model)
                    (SemanticContract.fingerprint model.Semantic)
                    "No divergent semantic fingerprint")
            testCase "keeps CLI and Web wire fingerprints distinct and lowercase SHA-256" (fun () ->
                let model = projection ()

                let cli =
                    ContractRenderers.cliFingerprint model |> CliWireContractFingerprint.value

                let web =
                    ContractRenderers.webFingerprint model |> WebWireContractFingerprint.value

                let hexadecimal = System.Text.RegularExpressions.Regex("^[0-9a-f]{64}$")

                Expect.isTrue (hexadecimal.IsMatch(cli)) "CLI fingerprint shape"
                Expect.isTrue (hexadecimal.IsMatch(web)) "Web fingerprint shape"
                Expect.notEqual cli web "Distinct wire contracts cannot share a fingerprint"
                expectFingerprintSensitivity model cli web)
        ]

let private semanticProjectionTests =
    testList
        "semantic projection"
        [
            testCase
                "derives field, command, status, and rule inventories from the semantic core"
                (fun () ->
                    let semantic = (projection ()).Semantic

                    Expect.equal semantic.Fields FieldDefinitions.all "Field descriptors"
                    Expect.equal semantic.Commands CommandDefinitions.all "Command descriptors"
                    Expect.equal semantic.Statuses CaseStatuses.all "Status vocabulary"
                    Expect.equal semantic.Rules DomainRules.all "Rule inventory")
        ]

let tests =
    testList
        "Contracts foundation"
        [ endpointCatalogTests; canonicalRenderingTests; semanticProjectionTests ]
