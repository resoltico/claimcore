module ClaimCore.Tests.RemoteResponseBoundsTests

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Cli
open ClaimCore.Contracts
open ClaimCore.Domain

let private fields: CaseFields =
    {
        IncidentDate = "2026-09-07"
        IncidentNotificationDate = "2026-09-07"
        IncidentCountry = String.replicate 100 "🙂"
        ClaimantName = String.replicate 200 "🙂"
        InsurerName = String.replicate 200 "🙂"
        ClaimedAmount = "1"
        ClaimedCurrency = "EUR"
        CaseReference = String.replicate 80 "🙂"
        PaymentDecisionDate = None
        PayableAmount = None
        PayableCurrency = None
        PaymentDate = None
        Status = CaseStatus.Opened
    }

let private historyBytes () =
    let entries =
        [ 1L .. 50L ]
        |> List.map (fun version ->
            HistoryEntry.FullEntry
                {
                    OperationId = Guid.NewGuid()
                    Snapshot = { Fields = fields; Version = version }
                    RecordedAt = DateTimeOffset.Parse("2026-09-07T12:00:00Z")
                    RecordedBy = "synthetic"
                    Replayed = false
                    Command = CommandKind.AmendRegistration
                })

    WebWireCodec.history (
        QueryOutcome.Succeeded(Lookup.Found { Entries = entries; NextCursor = None })
    )

let private request () =
    use source = JsonDocument.Parse("{}")

    match RemoteRequest.create "case.history" source.RootElement with
    | Ok value -> value
    | Error _ -> failtest "Synthetic history request could not be bound."

let private response bytes =
    let result = new HttpResponseMessage(HttpStatusCode.OK)
    result.Content <- new ByteArrayContent(bytes)
    result.Content.Headers.ContentType <- MediaTypeHeaderValue("application/json")
    result

let private maximumFieldHistory () =
    let bytes = historyBytes ()
    Expect.isGreaterThan bytes.Length 131072 "Valid full pages exceed an individual record bound"
    use reply = response bytes

    match
        RemoteServiceCall.readJsonResponse (request ()) reply CancellationToken.None
        |> _.GetAwaiter().GetResult()
    with
    | Ok _ -> ()
    | Error _ -> failtest "A schema-valid full history must be readable."

let private oversizedResponse () =
    use reply = response (Array.zeroCreate<byte>(16 * 1024 * 1024 + 1))

    Expect.throwsT<HttpRequestException>
        (fun () ->
            RemoteServiceCall.readJsonResponse (request ()) reply CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        "Buffering refuses over-limit bytes before JSON parsing"

let private completeArtifactIdentity () =
    let expected = Guid.Parse("00000000-0000-4000-8000-000000000001")

    let source =
        """{"format":"claimcore-recovery-artifact","formatVersion":3,"keyId":"00000000-0000-4000-8000-000000000001","exportId":"00000000-0000-4000-8000-000000000001","installationId":"00000000-0000-4000-8000-000000000001","epoch":1,"caseId":"00000000-0000-4000-8000-000000000001","preparerActorId":"00000000-0000-4000-8000-000000000001","preparerGrantRevision":0,"importerActorId":null,"exporterActorId":"00000000-0000-4000-8000-000000000001","exporterGrantRevision":0,"operationId":"00000000-0000-4000-8000-000000000001","issuedAt":"2026-09-07T12:00:00.0000000+00:00","expiresAt":"2026-09-08T12:00:00.0000000+00:00","canonicalCommandFormat":3,"requestFingerprintVersion":1,"nonceBase64":"AAAAAAAAAAAAAAAA","ciphertextBase64":"AAAA","tagBase64":"AAAAAAAAAAAAAAAAAAAAAAAA","macSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""

    let verify (text: string) =
        RemoteServiceCall.artifactIdentity expected (System.Text.Encoding.UTF8.GetBytes text)

    Expect.isTrue (verify source) "Independent complete structural fixture is accepted"

    let minimal =
        """{"format":"claimcore-recovery-artifact","formatVersion":3,"operationId":"00000000-0000-4000-8000-000000000001"}"""

    Expect.isFalse (verify minimal) "Identity alone is not an encrypted envelope"

    Expect.isFalse
        (verify (source.Replace("\"operationId\":", "\"operationId\":\"wrong\",\"operationId\":")))
        "Duplicate identity refuses before export"

    Expect.isFalse
        (RemoteServiceCall.artifactIdentity
            (Guid.NewGuid())
            (System.Text.Encoding.UTF8.GetBytes source))
        "Another requested operation cannot consume these bytes"

let tests =
    testList
        "service response bounds"
        [
            testCase
                "[CC-CLI-002] native recovery export requires complete structure and exact identity"
                completeArtifactIdentity
            testCase
                "[CC-CLI-001] CLI reads a full history with maximum-length Unicode fields"
                maximumFieldHistory
            testCase
                "[CC-CLI-001] CLI bounds service JSON buffering before parsing"
                oversizedResponse
        ]
