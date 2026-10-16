module ClaimCore.Tests.ContractExtensionTests

open System
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.ClientProtocol
open ClaimCore.ContractGeneration
open ClaimCore.Contracts
open ClaimCore.ContractExtension

let private operationId = "30000000-0000-4000-8000-000000000001"

let private request =
    "{\"operationId\":\""
    + operationId
    + "\",\"caseReference\":\"EXT-001\",\"expectedRevision\":4,\"act\":{\"kind\":\"SYNTHETIC_CORRECTION\",\"groups\":{\"syntheticGroup\":{\"mode\":\"REPLACE\",\"values\":{\"syntheticText\":\" exact authored value \"}}}}}"

let private invocation endpoint =
    Encoding.UTF8.GetBytes("{\"endpoint\":\"" + endpoint + "\",\"input\":" + request + "}")
    |> Frames.parse
    |> Fixtures.accepted

let private nativeDiscovery () =
    use catalogue = JsonDocument.Parse(ReadOnlyMemory(ContractDiscovery.catalogue ()))

    Expect.equal
        (catalogue.RootElement.GetProperty("endpoints").GetArrayLength())
        2
        "Injected preview and execution are discovered"

    use descriptors =
        JsonDocument.Parse(ReadOnlyMemory(ContractDiscovery.descriptors ()))

    Expect.equal
        ((descriptors.RootElement.GetProperty("commands").EnumerateArray() |> Seq.head)
            .GetProperty("kind")
            .GetString())
        ContractExtension.operation
        "Injected operation appears in discovery"

    let frame = invocation "extension.execute"

    Expect.equal
        (Frames.endpoint frame).Identifier
        "extension.execute"
        "No native operation mapping is required"

    Expect.equal
        (Frames.inputBytes frame)
        (Encoding.UTF8.GetBytes request)
        "Extra field and group retain authored bytes"

    Expect.isSome
        (ContractDiscovery.schema "extension.execute.request")
        "Injected schema is discoverable"

let private nativeInteraction () =
    let preview =
        "{\"endpoint\":\"extension.preview\",\"operationId\":\""
        + operationId
        + "\",\"outcome\":{\"tag\":\"REVIEWED\",\"data\":{\"review\":{\"after\":{\"caseReference\":\"EXT-001\",\"revision\":5}}}}}"

    let execution =
        "{\"endpoint\":\"extension.execute\",\"operationId\":\""
        + operationId
        + "\",\"outcome\":{\"tag\":\"ACCEPTED\",\"data\":{\"operationId\":\""
        + operationId
        + "\",\"scope\":\"CASE\",\"act\":\"SYNTHETIC_CORRECTION\",\"caseReference\":\"EXT-001\",\"revision\":5}}}"

    for endpoint, response, knowledge in
        [
            "extension.preview", preview, "READ_SUCCEEDED"
            "extension.execute", execution, "ACCEPTED"
        ] do
        let output =
            Frames.result (invocation endpoint) (Encoding.UTF8.GetBytes response)
            |> Fixtures.accepted

        let decoded = Frames.decode output |> Fixtures.accepted

        Expect.equal
            (decoded.GetProperty("attemptKnowledge").GetString())
            knowledge
            "Injected interaction uses production correlation and delivery handling"

let private unsupportedPrimitive () =
    let original = ContractExtension.endpoints |> List.head

    let unsupported =
        { original with
            Request =
                { original.Request with
                    Root = Schema.string (Some "unimplemented-format") None None None
                }
        }

    Expect.throws
        (fun () -> BrowserArtifacts.sourceFor [ unsupported ] |> ignore)
        "Browser generation rejects an unsupported primitive"

    Expect.throws
        (fun () -> NativeCatalogue.sourceFor "Test" [ unsupported ] [||] [||] |> ignore)
        "Native generation rejects the same unsupported primitive"

    Expect.isNonEmpty
        (BrowserArtifacts.sourceFor [ original ])
        "Existing primitive accepted control"

    Expect.isNonEmpty
        (NativeCatalogue.sourceFor "Test" [ original ] [||] [||])
        "Native existing primitive accepted control"

let private unsupportedPattern () =
    let original = ContractExtension.endpoints |> List.head

    let unsupported =
        { original with
            Request =
                { original.Request with
                    Root = Schema.string None (Some @"\Atext\z") None None
                }
        }

    Expect.throws
        (fun () -> BrowserArtifacts.sourceFor [ unsupported ] |> ignore)
        "Browser generation refuses a .NET-only regex dialect"

    Expect.throws
        (fun () -> NativeCatalogue.sourceFor "Test" [ unsupported ] [||] [||] |> ignore)
        "Native catalogue projection refuses rules the browser cannot interpret"

let private generatedCorpus () =
    let root = ClaimCore.TestSupport.RepositoryRoot.find ()
    let path = System.IO.Path.Combine(root, "contracts", "extension-corpus.json")
    use document = JsonDocument.Parse(System.IO.File.ReadAllBytes path)
    let cases = document.RootElement.GetProperty("cases").EnumerateArray() |> Seq.toList
    Expect.equal cases.Length 8 "Both directions and endpoints include rejecting cases"

    for sample in cases do
        let endpoint = sample.GetProperty("endpoint").GetString() |> nonNull
        let definition = EndpointCatalog.tryFind endpoint |> Option.get

        let schema =
            if sample.GetProperty("direction").GetString() = "request" then
                definition.Request
            else
                definition.Response

        let bytes = sample.GetProperty("value").GetRawText() |> Encoding.UTF8.GetBytes

        let admitted =
            HttpInputSupport.parse bytes (SchemaValueValidation.verify schema)
            |> Result.defaultValue false

        Expect.equal
            admitted
            (sample.GetProperty("valid").GetBoolean())
            "Generated native catalogue consumes each extension case"

    let missing = request.Replace("\"syntheticText\":\" exact authored value \"", "")
    let frame = "{\"endpoint\":\"extension.execute\",\"input\":" + missing + "}"

    Expect.isError
        (Frames.parse (Encoding.UTF8.GetBytes frame))
        "Extra primitive remains a required input without handwritten native rules"

let private unsupportedTimestampProfile () =
    let original = ContractExtension.endpoints |> List.head
    let timestamp = Schema.string (Some "date-time") None None None

    let unsupported =
        { original with
            Request =
                { original.Request with
                    Root = timestamp
                }
        }

    Expect.throws
        (fun () -> BrowserArtifacts.sourceFor [ unsupported ] |> ignore)
        "A general offset timestamp profile is unsupported"

    Expect.throws
        (fun () -> NativeCatalogue.sourceFor "Test" [ unsupported ] [||] [||] |> ignore)
        "Native projection refuses the same unsupported profile"

    let canonical =
        Schema.string
            (Some "date-time")
            (Some ProtocolProfile.CanonicalTimestampPattern)
            (Some 33)
            (Some 33)

    let accepted =
        { original with
            Request =
                { original.Request with
                    Root = canonical
                }
        }

    Expect.isNonEmpty
        (BrowserArtifacts.sourceFor [ accepted ])
        "Owned canonical UTC profile is supported"

    Expect.isNonEmpty
        (NativeCatalogue.sourceFor "Test" [ accepted ] [||] [||])
        "Native canonical UTC profile supported control"

    let document =
        { original.Request with
            Root = canonical
        }

    for value, expected in
        [
            "2000-01-01T00:00:00+14:59", false
            "2000-01-01T00:00:00.0000000+00:00", true
        ] do
        let bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value)

        let actual =
            HttpInputSupport.parse bytes (SchemaValueValidation.verify document)
            |> Result.defaultValue false

        Expect.equal actual expected "Independent canonical UTC timestamp boundary"

let private unboundedPattern () =
    let original = ContractExtension.endpoints |> List.head

    for pattern in [ "(a+)+$"; "[a-z]*"; "[a-z]{1,}"; "([a-z]{1,2}){1,2}"; "a{1,2}" ] do
        let unsupported =
            { original with
                Request =
                    { original.Request with
                        Root = Schema.string None (Some pattern) None None
                    }
            }

        Expect.throws
            (fun () -> BrowserArtifacts.sourceFor [ unsupported ] |> ignore)
            "Unbounded or repeated-group client regex refuses generation"

        Expect.throws
            (fun () -> NativeCatalogue.sourceFor "Test" [ unsupported ] [||] [||] |> ignore)
            "Native projection supports only the same bounded patterns"

    let supported =
        { original with
            Request =
                { original.Request with
                    Root = Schema.string None (Some "^[A-Z]{1,3}$") None None
                }
        }

    Expect.isNonEmpty
        (BrowserArtifacts.sourceFor [ supported ])
        "Bounded character-class control is supported"

    Expect.isNonEmpty
        (NativeCatalogue.sourceFor "Test" [ supported ] [||] [||])
        "Native bounded character-class control is supported"

let private integerSupport () =
    let original = ContractExtension.endpoints |> List.head

    let generated schema =
        let endpoint =
            { original with
                Request = { original.Request with Root = schema }
            }

        [
            (fun () -> BrowserArtifacts.sourceFor [ endpoint ] |> ignore)
            (fun () -> NativeCatalogue.sourceFor "Test" [ endpoint ] [||] [||] |> ignore)
        ]

    for schema in
        [
            Schema.integer None None
            Schema.integer (Some 0L) None
            Schema.integer (Some -9007199254740992L) (Some 9007199254740991L)
            Schema.integer (Some -9007199254740991L) (Some 9007199254740992L)
            Schema.constant (IntegerConstant 9007199254740992L)
            Schema.enumeration [ IntegerConstant -9007199254740992L ]
        ] do
        for generate in generated schema do
            Expect.throws generate "Unsafe or unbounded integer projection is unsupported"

    for schema in
        [
            Schema.integer (Some -9007199254740991L) (Some 9007199254740991L)
            Schema.constant (IntegerConstant 9007199254740991L)
            Schema.enumeration
                [ IntegerConstant -9007199254740991L; IntegerConstant 9007199254740991L ]
        ] do
        generated schema |> List.iter (fun generate -> generate ())

[<Tests>]
let tests =
    testList
        "Generated contract extension"
        [
            testCase
                "injected operation field and correction group reach native discovery"
                nativeDiscovery
            testCase
                "injected preview review and execution use generated admission"
                nativeInteraction
            testCase
                "unsupported client primitive generation fails with valid controls"
                unsupportedPrimitive
            testCase "unsupported regex dialect generation refuses" unsupportedPattern
            testCase
                "client integer projection requires explicit safe bounds and constants"
                integerSupport
            testCase "client regex generation requires bounded class repetitions" unboundedPattern
            testCase
                "only the owned canonical UTC timestamp profile generates"
                unsupportedTimestampProfile
            testCase
                "extension corpus exercises positive and negative native byte admission"
                generatedCorpus
        ]
