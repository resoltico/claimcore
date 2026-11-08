module ClaimCore.Tests.ClientAdmissionTests

open ClaimCore.Cli

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ClaimCore.TestSupport
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

let private fixture name =
    let root = ClaimCore.TestSupport.RepositoryRoot.find ()
    JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, name)))

let private text (name: string) (sample: JsonElement) =
    sample.GetProperty(name).GetString() |> nonNull

let private payload (sample: JsonElement) =
    match sample.TryGetProperty "hex" with
    | true, value -> Convert.FromHexString(value.GetString() |> nonNull)
    | _ -> Encoding.UTF8.GetBytes(text "text" sample)

let private admits endpoint direction (bytes: byte array) =
    if direction = "response" then
        StrictJson.read Limits.MaximumResponseBytes bytes
        |> Result.bind (Api.admitOutput (Api.tryFind endpoint |> Option.get))
        |> Result.isOk
    else
        let prefix = Encoding.UTF8.GetBytes("{\"endpoint\":\"" + endpoint + "\",\"input\":")

        Array.concat [ prefix; bytes; Encoding.UTF8.GetBytes "}" ]
        |> ClaimCore.Cli.Frames.admit
        |> Result.isOk

let private independentByteCases () =
    use document = fixture "tests/fixtures/client-byte-conformance.json"
    let cases = document.RootElement.GetProperty("cases").EnumerateArray() |> Seq.toList
    Expect.isNonEmpty cases "Literal byte controls are present"

    for sample in cases do
        Expect.equal
            (admits (text "endpoint" sample) (text "direction" sample) (payload sample))
            (sample.GetProperty("valid").GetBoolean())
            (text "id" sample)

let private authoritativeIdentityAdmission () =
    use document = fixture "tests/fixtures/client-byte-conformance.json"
    let mutable count = 0

    for sample in document.RootElement.GetProperty("cases").EnumerateArray() do
        match sample.TryGetProperty "serverIdentityValid" with
        | true, expected ->
            count <- count + 1

            Expect.equal
                (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input (payload sample)
                 |> Result.mapError _.Reason
                 |> Result.bind (function
                     | OperationInput.Authority request ->
                         ClaimCore.Domain.Authority.parse request
                         |> Result.mapError (fun _ -> "invalid principal")
                     | _ -> Error "Expected authority request")
                 |> Result.isOk)
                (expected.GetBoolean())
                ("Unchanged authoritative identity admission: " + text "id" sample)
        | _ -> ()

    Expect.equal count 22 "All independent principal cases reach authoritative typed binding"

let private exactUuid () =
    let valid = "10000000-0000-7000-8000-00000000000a"

    for value, expected in
        [
            valid, true
            valid + "\n", false
            valid + "x", false
            valid.ToUpperInvariant(), false
            "00000000-0000-0000-0000-000000000000", false
        ] do
        let bytes = JsonSerializer.Serialize(value) |> Encoding.UTF8.GetBytes

        Expect.equal
            (Codec.decode 100 Codec.uuid bytes |> Result.isOk)
            expected
            "Unbounded UUID format consumes exactly one canonical nonempty UUID"

let private reminderRevisionAdmission () =
    let operationId = Guid.Parse "10000000-0000-7000-8000-000000000001"
    let reminderId = "30000000-0000-7000-8000-000000000001"

    let raw revision =
        sprintf
            """{"endpoint":"operation.execute","input":{"kind":"REMINDER","operationId":"%s","target":{"kind":"REFERENCE","caseReference":"CASE-001"},"act":{"kind":"COMPLETE","reminderId":"%s","expectedRevision":%d}}}"""
            (operationId.ToString("D"))
            reminderId
            revision
        |> Encoding.UTF8.GetBytes

    let refusal =
        Codec.encode
            Api.operationExecute.Output
            {
                OperationId = operationId
                Outcome =
                    PresentedExecuteResult.Rejected(
                        Refusal.Access AccessRefusal.ResourceUnavailable
                    )
            }

    let mutable exchanges = 0

    use handler =
        new CliTransportFixture.Handler(fun _ ->
            exchanges <- exchanges + 1
            Task.FromResult(CliTransportFixture.response 200 refusal))

    use client = new HttpClient(handler, false)

    let access: RemoteServiceCall.Access =
        {
            InstallationId = Some(Guid.Parse "90000000-0000-7000-8000-000000000001")
            Service = Uri "https://localhost/"
            Authorization = AuthenticationHeaderValue("Bearer", "synthetic-validation-test")
        }

    let invoke frame beforeSend cancelled =
        RemoteServiceCall.send client access frame beforeSend cancelled

    let zeroCode, zeroBytes, _ = CliTransportFixture.call (raw 0) invoke
    let zero = Frames.decode zeroBytes |> Fixtures.accepted
    Expect.equal zeroCode 2 "An invalid raw reminder frame refuses cleanly"
    Expect.equal zero.Delivery DeliveryState.NotSent "Invalid revision never dispatches"

    match zero.Result with
    | FrameResult.LocalFailure CliFailure.InvalidFrame -> ()
    | other -> failtestf "Expected existing invalid-frame refusal: %A" other

    Expect.equal exchanges 0 "Actual in-memory HTTP handler sees no exchange"
    let oneCode, oneBytes, _ = CliTransportFixture.call (raw 1) invoke
    let one = Frames.decode oneBytes |> Fixtures.accepted
    Expect.equal oneCode 2 "A genuine delivered service refusal retains its exit code"
    Expect.equal one.Delivery DeliveryState.Delivered "Positive revision reaches the transport"

    match one.Result with
    | FrameResult.Reply reply ->
        Expect.equal
            (Frames.outcome reply.Wire)
            (Some "REJECTED")
            "Transport reply is not substituted by local failure"
    | other -> failtestf "Expected genuine service response: %A" other

    Expect.equal exchanges 1 "Revision one dispatches exactly once"

    let arguments revision =
        [
            "reminder"
            "complete"
            "CASE-001"
            "--reminder-id"
            reminderId
            "--expected-reminder-revision"
            revision
            "--dry-run"
        ]

    let targeted =
        function
        | Error [ UsageError.InvalidValue("--expected-reminder-revision", "0") ] -> ()
        | other -> failtestf "Expected targeted reminder option refusal: %A" other

    Grammar.prepare (fun () -> operationId.ToString("D")) (arguments "0")
    |> targeted

    Grammar.reminderIntent (arguments "0") |> targeted

    Expect.isOk
        (Grammar.prepare (fun () -> operationId.ToString("D")) (arguments "1"))
        "The valid human command stays preparable"

    for arguments in
        [
            CliExamples.complete [ "case"; "open"; "INITIAL-CASE" ] @ [ "--dry-run" ]
            [
                "work"
                "update"
                "INITIAL-WORK"
                "--expected-work-revision"
                "0"
                "--handling-summary"
                "Initial handling"
                "--dry-run"
            ]
        ] do
        let prepared =
            Grammar.prepare (fun () -> operationId.ToString("D")) arguments
            |> Fixtures.accepted

        Expect.isOk
            (Frames.admit prepared.Bytes)
            "Case creation and initial work retain legitimate zero revisions"

let private pageBounds () =
    let directory =
        match (ResponseExamples.defaultReply Api.authorityGrants) with
        | Outcome.Succeeded page -> page.Data
        | _ -> failtest "Successful directory input"

    let timeline =
        {
            CaseTimelineEntry.OperationId = ResponseExamples.receipt.OperationId
            Position = ResponseExamples.receipt.Position
            Subject = TimelineSubject.CaseFact(Revision.first, CaseActKind.Open)
            RecordedAt = Inputs.workBasis.ObservedAt
            RecordedBy = directory.Actor.Actor
            Changes = TimelineChanges.Withheld
        }

    let pages =
        [
            "authority.list",
            Codec.encode
                Api.authorityList.Output
                (Presented.outcome
                    (fun (page: ActorReviewPage) -> PeopleScreen.rows page.Items)
                    (Outcome.Succeeded
                        {
                            ActorReviewPage.Basis = Inputs.workBasis
                            Items = [ directory.Actor ]
                            NextCursor = None
                        }))
            "authority.grants",
            Codec.encode
                Api.authorityGrants.Output
                (Presented.outcome
                    (fun (page: AuthorityGrantPage) -> PersonScreen.view page)
                    (Outcome.Succeeded
                        { directory with
                            Items =
                                [
                                    {
                                        Role = Role.CaseReader
                                        Scope = GrantScope.Installation
                                    }
                                ]
                        }))
            "case.timeline",
            Codec.encode
                Api.caseTimeline.Output
                (Presented.outcome
                    (fun (page: CaseTimelinePage) -> TimelineScreen.rows page.Items)
                    (Outcome.Succeeded
                        {
                            CaseTimelinePage.Disclosure = HistoryDisclosure.Full
                            Basis = Inputs.workBasis
                            CaseId = ResponseExamples.snapshot.CaseId
                            Reference = ResponseExamples.snapshot.Reference
                            Items = [ timeline ]
                            NextCursor = None
                        }))
        ]

    for endpoint, baseline in pages do
        let maximum = if endpoint = "case.timeline" then 10 else 50

        for count, expected in [ 0, true; maximum, true; maximum + 1, false ] do
            let root = JsonNode.Parse baseline |> nonNull
            let data = root["outcome"] |> nonNull |> fun outcome -> outcome["data"] |> nonNull
            let item = (data["items"] |> nonNull).AsArray()[0] |> nonNull
            let rows = JsonArray()

            for _ in 1..count do
                rows.Add(item.DeepClone())

            data["items"] <- rows

            Expect.equal
                (admits endpoint "response" (Encoding.UTF8.GetBytes(root.ToJsonString())))
                expected
                (sprintf
                    "%s admits %d items exactly according to its declared page bound"
                    endpoint
                    count)

[<Tests>]
let tests =
    testList
        "production client decoder conformance"
        [
            testCase "timeline, directory and grant pages enforce their declared bounds" pageBounds
            testCase "independent positive and negative raw bytes" independentByteCases
            testCase
                "wire admission leaves authoritative identity policy unchanged"
                authoritativeIdentityAdmission
            testCase "UUID codec requires exact canonical input" exactUuid
            testCase
                "reminder revisions zero and one preserve local admission and no-exchange boundaries"
                reminderRevisionAdmission
        ]
