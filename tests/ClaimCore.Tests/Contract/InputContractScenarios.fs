module ClaimCore.Tests.InputContractScenarios

open ClaimCore.Cli

open ClaimCore.Protocol
open ClaimCore.Json

open System
open System.Text
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.ContractScenarioInputs
open ClaimCore.Tests.OutcomeContractScenarios


let internal rejectFrames () =
    for value in
        [
            "{}"
            "{\"endpoint\":\"context\",\"input\":{},\"extra\":0}"
            "{\"endpoint\":\"context\",\"endpoint\":\"context\",\"input\":{}}"
            "not-json"
        ] do
        Expect.isError
            (ClaimCore.Cli.Frames.admit (bytes value))
            "Malformed, duplicate and unknown members refuse"

    Expect.isError (ClaimCore.Cli.Frames.admit [| 0xffuy |]) "UTF8 is strict"

    Expect.isError
        (ClaimCore.Cli.Frames.admit (Array.create (Limits.MaximumInvocationBytes + 1) 32uy))
        "Frames are bounded"

    let original = bytes "{ \"endpoint\": \"context\", \"input\": { } }"
    let invocation = ClaimCore.Cli.Frames.admit original |> accepted
    let copy = ClaimCore.Cli.Frames.inputBytes invocation
    copy[0] <- 0uy

    Expect.equal
        (ClaimCore.Cli.Frames.inputBytes invocation)
        (bytes "{}")
        "Stored prepared input bytes survive caller mutation"

let internal integerLexemes () =
    let wire =
        Codec.encode Api.operationExecute.Input (OperationInput.Case(caseRequest registration))
        |> Encoding.UTF8.GetString

    for value in [ "-1"; "9007199254740992"; "1.0"; "1e0"; "\"1\"" ] do
        Expect.isError
            (Codec.decode
                Limits.MaximumRequestBytes
                Api.operationExecute.Input
                (bytes (wire.Replace("\"expectedRevision\":0", "\"expectedRevision\":" + value))))
            "Strict lexical request integers"

    for value in [ "0"; "-0"; "9007199254740991" ] do
        Expect.isOk
            (Codec.decode
                Limits.MaximumRequestBytes
                Api.operationExecute.Input
                (bytes (wire.Replace("\"expectedRevision\":0", "\"expectedRevision\":" + value))))
            "Safe integer boundaries"

    for sequence in [ 0L; 9007199254740992L ] do
        Expect.throws
            (fun () ->
                Codec.encode
                    Api.operationExecute.Output
                    {

                        OperationId = id
                        Outcome =
                            ExecuteResult.Accepted
                                { receipt with
                                    Position = { position with Sequence = sequence }
                                }
                    }
                |> ignore)
            "Accepted sequence bounds"

let internal retainedText () =
    for name in [ String.replicate 201 "A"; "Earlier‮Name" ] do
        let original = Case.snapshot (opened ())

        let fields =
            { viewFields original with
                Registration =
                    { (viewFields original).Registration with
                        ClaimantName = CaseFixtures.name name
                    }
            }

        let snapshot =
            { original with
                State = ClaimCore.Domain.CaseState.Active(fields)
            }

        let prior = Case.ofAccepted snapshot |> accepted

        admitted
            Api.caseGet.Output
            (Outcome.Succeeded
                {
                    Snapshot = snapshot
                    Advice = (CaseAdvice.create [] None [] |> Result.defaultWith invalidOp)
                })

        |> ignore

        let values =
            { registration with
                ClaimantName = name
                InsurerName = "Changed insurer"
            }

        let reaffirm = request 1L (AuthoredCaseAct.AmendRegistration values)

        let input =
            { caseRequest values with
                ExpectedRevision = 1L
                Act = ClaimCore.Domain.AuthoredCaseAct.AmendRegistration values
            }

        Codec.decode
            Limits.MaximumRequestBytes
            Api.operationExecute.Input
            (Codec.encode Api.operationExecute.Input (OperationInput.Case input))
        |> accepted
        |> ignore

        CaseFixtures.decide today reaffirm (Some prior) |> accepted |> ignore

        let changed =
            { reaffirm with
                Act =
                    (AuthoredCaseAct.AmendRegistration
                        { values with
                            ClaimantName = name + "B"
                        })
            }

        Expect.isError
            (CaseFixtures.decide today changed (Some prior))
            "New assertions follow current admission"

let internal authorityWire kind issuer subject =
    JsonSerializer.SerializeToUtf8Bytes
        {|
            kind = "AUTHORITY"
            operationId = id.ToString("D")
            target =
                {|
                    kind = kind
                    issuer = issuer
                    subject = subject
                |}
            act = {| kind = "REGISTER" |}
        |}

let internal principalAdmission () =
    let expected =
        [
            true, "HUMAN", "https://issuer.example/", String.replicate 512 "a"
            false, "HUMAN", "https://issuer.example/", String.replicate 513 "a"
            true, "HUMAN", "https://issuer.example/", String.replicate 256 "\U0001f642"
            false, "HUMAN", "https://issuer.example/", String.replicate 257 "\U0001f642"
            true, "SERVICE", "https://issuer.example/", String.replicate 256 "a"
            false, "SERVICE", "https://issuer.example/", String.replicate 257 "a"
            true,
            "HUMAN",
            "https://issuer.example/"
            + String.replicate (2048 - "https://issuer.example/".Length) "a",
            "subject"
            false,
            "HUMAN",
            "https://issuer.example/"
            + String.replicate (2049 - "https://issuer.example/".Length) "a",
            "subject"
            false, "HUMAN", "https://issuer.example", "subject"
            false, "HUMAN", "https://user@issuer.example/", "subject"
            false, "HUMAN", "https://issuer.example/?q=x", "subject"
            false, "HUMAN", "https://issuer.example/#x", "subject"
            false, "HUMAN", "https://issuer.example/", " "
            true, "HUMAN", "https://issuer.example/", "Unicode \U0001f642 subject"
        ]

    for admitted, kind, issuer, subject in expected do
        let wire = authorityWire kind issuer subject

        let syntax =
            Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input wire
            |> accepted

        Expect.equal
            (match syntax with
             | OperationInput.Authority request -> Authority.parse request |> Result.isOk
             | _ -> false)
            admitted
            "Only authoritative principal admission judges identity semantics"

    let invalidKind = authorityWire "UNKNOWN" "https://issuer.example/" "subject"

    Expect.isError
        (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input invalidKind)
        "Closed principal kind"

let internal policyHints () =
    use catalog = artifact "catalog.json"

    let claimant =
        catalog.RootElement.GetProperty("fields").EnumerateArray()
        |> Seq.find (fun field -> field.GetProperty("name").GetString() = "claimantName")

    Expect.equal
        (claimant.GetProperty("scalar").GetProperty("maximumCharacters").GetInt32())
        200
        "Current policy is a nonvalidating hint"

let internal authorityFieldPresentation () =
    use document = artifact "catalog.json"
    let root = document.RootElement
    let fields = root.GetProperty("inputFields").EnumerateArray() |> Seq.toList

    let names =
        fields
        |> List.map (fun field -> field.GetProperty("name").GetString() |> nonNull)

    Expect.equal names.Length (Set.ofList names).Count "Presentation identifiers are unique"

    Expect.equal
        (root.GetProperty("fields").GetArrayLength())
        13
        "Protocol labels add no business fields"

    Expect.equal
        (Set.ofList names)
        (Set.ofList
            [
                "operationId"
                "expectedRevision"
                "principal.kind"
                "principal.issuer"
                "principal.subject"
                "reason"
                "role"
                "cursor"
                "limit"
                "scope"
                "actorId"
                "detail"
            ])
        "Every catalog-owned technical input has presentation metadata"

    for field in fields do
        let name = field.GetProperty("name").GetString() |> nonNull
        let labelId = field.GetProperty("labelId").GetString() |> nonNull

        let identity =
            InteractionInput.technical
            |> List.find (fun input -> InteractionInput.key input = name)

        let prefix =
            match identity with
            | InteractionInput.Target _ -> "field."
            | InteractionInput.Scope
            | InteractionInput.ActorId
            | InteractionInput.Detail -> "input."

        Expect.equal
            labelId
            (prefix + name + ".label")
            "The input owner determines its message namespace"

        Expect.isFalse
            (field.TryGetProperty "label" |> fst)
            "CatalogDocument carry keys, not authored prose."

let internal decoderProperty =
    let seed =
        Codec.encode Api.operationExecute.Input (OperationInput.Case(caseRequest registration))

    let generator = Gen.choice [ DecoderCorpora.jsonLike; DecoderCorpora.mutated seed ]

    property {
        let! value = generator

        let total decoder =
            try
                decoder value |> ignore
                true
            with _ ->
                false

        return
            total ClaimCore.Cli.Frames.admit
            && total ClaimCore.Cli.Frames.decode
            && (Api.all
                |> List.forall (fun endpoint ->
                    total (fun bytes ->
                        StrictJson.read Limits.MaximumRequestBytes bytes
                        |> Result.bind (Api.admitInput endpoint))
                    && total (fun bytes ->
                        StrictJson.read Limits.MaximumResponseBytes bytes
                        |> Result.bind (Api.admitOutput endpoint))))
    }
