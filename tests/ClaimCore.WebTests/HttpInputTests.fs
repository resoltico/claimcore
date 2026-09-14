module ClaimCore.WebTests.HttpInputTests

open System
open System.Globalization
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Web

let private bytes value = Encoding.UTF8.GetBytes(value: string)

let private expectError result message =
    match result with
    | Ok _ -> failtest message
    | Error _ -> ()

let private validDraft =
    """{"operationId":"40000000-0000-4000-8000-000000000001","caseReference":"WEB-V2-001","expectedRevision":"0","command":{"kind":"OPEN","values":{"incidentDate":"2026-09-01","incidentNotificationDate":"2026-09-02","incidentCountry":"Latvia","claimantName":"Synthetic claimant","insurerName":"Synthetic insurer","claimedAmount":"12.34","claimedCurrency":"EUR"}}}"""

let private flatDraftTests () =
    match HttpInput.draft (bytes validDraft) with
    | Error message -> failtestf "Expected v2 draft acceptance: %s" message
    | Ok draft ->
        match draft.Command with
        | DraftCommand.Flat(CommandKind.Open, values) ->
            Expect.equal values.Length 7 "The semantic OPEN input set is retained"
        | _ -> failtest "The typed command token is retained"

        Expect.equal draft.ExpectedVersion 0L "Revision remains canonical text at the wire"

    let revision value =
        validDraft.Replace(
            "\"expectedRevision\":\"0\"",
            "\"expectedRevision\":\"" + value + "\"",
            StringComparison.Ordinal
        )

    let largest = (Int64.MaxValue - 1L).ToString(CultureInfo.InvariantCulture)
    let maximum = Int64.MaxValue.ToString(CultureInfo.InvariantCulture)

    match HttpInput.draft (bytes (revision largest)) with
    | Ok draft -> Expect.equal draft.ExpectedVersion (Int64.MaxValue - 1L) "Largest revision"
    | Error message -> failtestf "Expected largest revision acceptance: %s" message

    expectError (HttpInput.draft (bytes (revision maximum))) "Int64.MaxValue is reserved"

    [
        """{"caseReference":"WEB-V2-001","expectedRevision":"0","command":{"type":"OPEN"}}"""
        """{"operationId":"40000000-0000-4000-8000-000000000001","caseReference":"WEB-V2-001","expectedRevision":"00","command":{"kind":"CLOSE","values":{}}}"""
        """{"operationId":"40000000-0000-4000-8000-000000000001","caseReference":"WEB-V2-001","expectedRevision":"0","command":{"kind":"CLOSE","values":{"extra":"x"}}}"""
        """{"operationId":"40000000-0000-4000-8000-000000000001","caseReference":"WEB-V2-001","expectedRevision":"0","command":{"kind":"CLOSE","values":{}},"caseReference":"duplicate"}"""
    ]
    |> List.iter (fun value -> expectError (HttpInput.draft (bytes value)) "Invalid v2 draft")

let private correctionDraftTests () =
    let correction =
        """{"operationId":"40000000-0000-4000-8000-000000000001","caseReference":"WEB-V2-001","expectedRevision":"2","command":{"kind":"CORRECT_CASE","groups":{"registration":{"mode":"KEEP"},"decision":{"mode":"REPLACE","values":{"paymentDecisionDate":"2026-09-03","payableAmount":"12.34","payableCurrency":"EUR"}},"payment":{"mode":"CLEAR"}}}}"""

    match HttpInput.draft (bytes correction) with
    | Ok {
             Command = DraftCommand.Correction(keep, replace, clear)
         } ->
        Expect.equal keep CorrectionDraftAction.Keep "Correction keeps an explicit group"

        match replace with
        | CorrectionDraftAction.Replace values ->
            Expect.equal values.Length 3 "Correction replacement keeps its semantic input tuple"
        | _ -> failtest "Correction replacement remains explicit"

        Expect.equal clear CorrectionDraftAction.Clear "Correction clearing remains explicit"
    | Ok _ -> failtest "Correction groups remain a typed draft variant"
    | Error message -> failtestf "Expected correction draft acceptance: %s" message

    let unknownMode =
        correction.Replace("\"KEEP\"", "\"UNKNOWN\"", StringComparison.Ordinal)

    let forbiddenClear =
        correction.Replace("\"KEEP\"", "\"CLEAR\"", StringComparison.Ordinal)

    expectError (HttpInput.draft (bytes unknownMode)) "Correction mode must belong to its group"
    expectError (HttpInput.draft (bytes forbiddenClear)) "Correction groups have closed action sets"

let private draftTests () =
    flatDraftTests ()
    correctionDraftTests ()

let private endpointInputTests () =
    match HttpInput.page 50 (bytes """{"limit":50}""") with
    | Ok input ->
        Expect.isNone input.Cursor "A cursor is absent rather than an overloaded null sentinel"
        Expect.equal input.Limit 50 "Page limits remain endpoint-local integers"
    | Error message -> failtestf "Expected page acceptance: %s" message

    expectError (HttpInput.page 50 (bytes """{"limit":51}""")) "Page bounds are exact"

    match
        HttpInput.history 50 (bytes """{"caseReference":"WEB-V2-001","limit":1,"detail":"FULL"}""")
    with
    | Ok input ->
        Expect.equal input.CaseReference "WEB-V2-001" "History preserves its explicit target"
        Expect.isNone input.Cursor "History starts without an implicit revision cursor"
        Expect.equal input.Limit 1 "History page limit remains local"
    | Error message -> failtestf "Expected history acceptance: %s" message

    expectError
        (HttpInput.resolve (
            bytes
                """{"operationId":"40000000-0000-4000-8000-000000000001","requestSha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}"""
        ))
        "Recovery digests are lowercase canonical SHA-256"

let private unicodeInputTests () =
    for source in
        [
            """{"caseReference":"\uD800"}"""
            """{"caseReference":"\uDFFF"}"""
            """{"\uD800":"synthetic"}"""
        ] do
        expectError (HttpInput.caseReference (bytes source)) "Unpaired escape is rejected"

    expectError
        (HttpInput.login (bytes """{"credential":"\uD800","antiforgeryToken":"token"}"""))
        "Credentials cannot contain malformed Unicode"

    Expect.equal
        (HttpInput.caseReference (bytes """{"caseReference":"\uD83D\uDE00"}"""))
        (Ok "😀")
        "A valid escaped surrogate pair remains one Unicode scalar"

    Expect.equal
        (HttpInput.caseReference (bytes """{"caseReference":"\\uD800"}"""))
        (Ok "\\uD800")
        "A literal backslash-u sequence is not an escaped surrogate"

    let marker = "SYNTHETIC-PRIVATE-MARKER"
    let unknown = "{\"caseReference\":\"synthetic\",\"" + marker + "\":\"synthetic\"}"

    match HttpInput.caseReference (bytes unknown) with
    | Error message ->
        Expect.isFalse
            (message.Contains(marker, StringComparison.Ordinal))
            "No input name in errors"
    | Ok _ -> failtest "An unknown synthetic property must be refused."

let private sessionInputTests () =
    match HttpInput.login (bytes """{"credential":"synthetic","antiforgeryToken":"token"}""") with
    | Ok input ->
        Expect.equal input.Credential "synthetic" "Login keeps the submitted credential private"
        Expect.equal input.AntiforgeryToken "token" "Login body declares the matching token"
    | Error message -> failtestf "Expected login acceptance: %s" message

    Expect.isOk (HttpInput.logout (bytes "{}")) "Logout requires the explicit empty object"
    expectError (HttpInput.logout (bytes """{"unexpected":true}""")) "Logout rejects extra data"

let private scalarShapeRefusals () =
    for source in
        [
            "null"
            """{"caseReference":null}"""
            """{"caseReference":42}"""
            """{"caseReference":"synthetic","extra":true}"""
            """{"caseReference":"synthetic","caseReference":"other"}"""
        ] do
        expectError (HttpInput.caseReference (bytes source)) "Case-target shape is closed"

    for source in [ """{"limit":"1"}"""; """{"limit":1.5}"""; """{"limit":0}""" ] do
        expectError (HttpInput.page 50 (bytes source)) "Page limits are bounded JSON integers"

    expectError
        (HttpInput.page 50 (bytes """{"cursor":null,"limit":1}"""))
        "A present optional cursor cannot be JSON null"

let private operationAndDigestRefusals () =
    let operation value = $"""{{"operationId":"{value}"}}"""

    for value in
        [
            "00000000-0000-0000-0000-000000000000"
            "40000000-0000-4000-8000-00000000000A"
            "not-a-uuid"
        ] do
        expectError (HttpInput.operationId (bytes (operation value))) "Operation ID is canonical"

    let valid = String.replicate 64 "a"
    Expect.equal (HttpInput.sourceDigest valid) (Ok valid) "Canonical source digest is retained"

    for value in [ String.replicate 63 "a"; String.replicate 64 "A"; String.replicate 64 "g" ] do
        expectError (HttpInput.sourceDigest value) "Source digest must be exact lowercase SHA-256"

let private recoveryAndDraftRefusals () =
    let operation = "40000000-0000-4000-8000-000000000001"
    let digest = String.replicate 64 "a"

    let dismiss confirmed =
        $"""{{"operationId":"{operation}","requestSha256":"{digest}","confirmed":{confirmed}}}"""

    match HttpInput.dismiss (bytes (dismiss "false")) with
    | Ok value -> Expect.isFalse value.Confirmed "A Boolean false remains a value for core policy"
    | Error message -> failtestf "Expected syntactically valid false confirmation: %s" message

    expectError (HttpInput.dismiss (bytes (dismiss "0"))) "Confirmation is a JSON Boolean"
    expectError (HttpInput.dismiss (bytes (dismiss "null"))) "Null is not a Boolean"

    let unknownKind =
        validDraft.Replace("\"OPEN\"", "\"UNKNOWN\"", StringComparison.Ordinal)

    expectError (HttpInput.draft (bytes unknownKind)) "A command kind must belong to Domain"

    let missingValue =
        validDraft.Replace(",\"claimedCurrency\":\"EUR\"", "", StringComparison.Ordinal)

    expectError (HttpInput.draft (bytes missingValue)) "Required Domain command values are present"

type private BrokenStream() =
    inherit MemoryStream()

    override _.ReadAsync(_: byte array, _: int, _: int, _: CancellationToken) =
        Task.FromException<int>(IOException("Synthetic read failure."))

let private transportTests () =
    use accepted = new MemoryStream(bytes "abc")

    Expect.equal
        (HttpInput.readBounded 3 accepted |> Async.AwaitTask |> Async.RunSynchronously)
        (Ok(bytes "abc"))
        "The exact endpoint byte bound is accepted"

    use rejected = new MemoryStream(bytes "abcd")

    expectError
        (HttpInput.readBounded 3 rejected |> Async.AwaitTask |> Async.RunSynchronously)
        "Over-limit streaming input is rejected before JSON allocation"

    use broken = new BrokenStream()

    expectError
        (HttpInput.readBounded 8 broken |> Async.AwaitTask |> Async.RunSynchronously)
        "Read failures have one safe transport classification"

let tests =
    testList
        "Web HTTP-v2 input"
        [
            testCase "[CC-WEB-001] decodes one semantic command-draft shape" draftTests
            testCase "[CC-WEB-001] keeps endpoint request bodies exact and typed" (fun () ->
                endpointInputTests ()
                unicodeInputTests ())
            testCase "[CC-WEB-001] gives session mutations closed body schemas" sessionInputTests
            testCase
                "[CC-WEB-001] rejects malformed case-target and page scalar shapes"
                scalarShapeRefusals
            testCase
                "[CC-WEB-001] validates canonical operation and source-digest scalars"
                operationAndDigestRefusals
            testCase
                "[CC-WEB-001] distinguishes Boolean recovery confirmation and Domain command inputs"
                recoveryAndDraftRefusals
            testCase "[CC-WEB-001] enforces streaming byte limits before decoding" transportTests
        ]
