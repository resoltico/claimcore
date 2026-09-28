module ClaimCore.Tests.FrameDeliveryTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Cli
open ClaimCore.Contracts
open ClaimCore.Tests.DiagnosticTestStreams

let private operationId = Guid.Parse("91000000-0000-4000-8000-000000000001")

let private commandInput =
    $"""{{"operationId":"{operationId:D}","caseReference":"SYNTHETIC","expectedRevision":"0","command":{{"kind":"CLOSE","values":{{}}}}}}"""

let private input text =
    use document = JsonDocument.Parse(text: string)
    document.RootElement.Clone()

let private assertPrivacy errors =
    for forbidden in [ "SYNTHETIC"; "PRIVATE-SOURCE"; "IOException" ] do
        Expect.isFalse
            ((text errors).Contains(forbidden, StringComparison.Ordinal))
            "No payload in diagnostics"

let private completedFrame () =
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let delivery = FrameDelivery(output, errors)
    delivery.BeginFrame()
    delivery.BeforeRemoteDispatch("command.execute", input commandInput)
    delivery.ObserveRendered()

    delivery.Write(
        CliRemoteWireCodec.localFailure "command.execute" CliRemoteProblem.ServiceUnavailable
    )

    delivery.BeginFrame()

    Expect.equal
        (delivery.Failure(IOException("PRIVATE-SOURCE")))
        3
        "Later input is not an earlier mutation"

    let value = parsed errors
    Expect.equal (value.GetProperty("operationId").ValueKind) JsonValueKind.Null "Identity reset"
    Expect.isFalse (value.GetProperty("potentiallyChanged").GetBoolean()) "No new mutation"
    assertPrivacy errors

let private malformedNextFrame () =
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let delivery = FrameDelivery(output, errors)
    delivery.BeginFrame()
    delivery.BeforeRemoteDispatch("command.execute", input commandInput)
    delivery.ObserveRendered()

    delivery.Write(
        CliRemoteWireCodec.localFailure "command.execute" CliRemoteProblem.ServiceUnavailable
    )

    delivery.BeginFrame()

    let failure =
        ProtocolFailure.create ProtocolProblem.InvalidJson ProtocolLocation.root

    delivery.Write(CliRemoteWireCodec.protocolFailure 2 failure)
    let lines = (text output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
    Expect.equal lines.Length 2 "One response per frame"
    Expect.isFalse (lines[1].Contains(operationId.ToString("D"))) "No prior identity"
    Expect.equal errors.Length 0L "Protocol refusal is a frame result"

let private failedDelivery flush =
    use output = new BrokenOutput(flush)
    use errors = new MemoryStream()
    let delivery = FrameDelivery(output, errors)
    delivery.BeginFrame()
    delivery.BeforeRemoteDispatch("command.execute", input commandInput)
    delivery.ObserveRendered()

    let response =
        CliRemoteWireCodec.localFailure "command.execute" CliRemoteProblem.DeliveryUnconfirmed

    let code =
        try
            delivery.Write(response)
            failtest "Synthetic output must fail"
        with error ->
            delivery.Failure error

    Expect.equal code 4 "Output loss cannot prove no mutation"
    let value = parsed errors
    Expect.equal (diagnosticId value) "CLI_OUTPUT_DELIVERY_FAILED" "Delivery cause"

    Expect.equal
        (value.GetProperty("operationId").GetString())
        (operationId.ToString("D"))
        "Current ID"

    Expect.equal output.Writes 1 "No second output frame"
    assertPrivacy errors

let private acquireFailure () =
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let delivery = FrameDelivery(output, errors)
    delivery.BeginFrame()
    delivery.BeforeAcquire()
    Expect.equal (delivery.Failure(IOException("PRIVATE-SOURCE"))) 3 "No dispatch began"
    let value = parsed errors
    Expect.equal (diagnosticId value) "CLI_SERVICE_ACQUIRE_FAILED" "Acquisition cause"
    Expect.isFalse (value.GetProperty("potentiallyChanged").GetBoolean()) "No mutation"
    assertPrivacy errors

let private observedEncodingFailure () =
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let delivery = FrameDelivery(output, errors)
    delivery.BeginFrame()
    delivery.BeforeRemoteDispatch("command.execute", input commandInput)
    delivery.ObserveRendered()

    Expect.equal
        (delivery.Failure(IOException("PRIVATE-SOURCE")))
        4
        "Observed mutation is unresolved to caller"

    Expect.equal
        (diagnosticId (parsed errors))
        "CLI_RESULT_ENCODING_FAILED"
        "Observed response phase"

    Expect.equal output.Length 0L "No partial response"
    assertPrivacy errors

let private importWithoutIdentity () =
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let delivery = FrameDelivery(output, errors)
    delivery.BeginFrame()

    delivery.BeforeRemoteDispatch(
        "recovery.importEnvelopeRetain",
        input
            """{"source":"PRIVATE-SOURCE","sourceSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","confirmed":true}"""
    )

    Expect.equal (delivery.Failure(IOException("PRIVATE-SOURCE"))) 4 "Retention may have started"
    let value = parsed errors
    Expect.equal (value.GetProperty("operationId").ValueKind) JsonValueKind.Null "No invented ID"
    assertPrivacy errors

let private brokenDiagnostics () =
    use output = new BrokenOutput(false)
    use errors = new BrokenOutput(false)
    let delivery = FrameDelivery(output, errors)
    delivery.BeginFrame()
    delivery.BeforeRemoteDispatch("command.execute", input commandInput)
    delivery.ObserveRendered()

    try
        delivery.Write(
            CliRemoteWireCodec.localFailure "command.execute" CliRemoteProblem.DeliveryUnconfirmed
        )
    with error ->
        Expect.equal (delivery.Failure error) 4 "Bounded failure"

    Expect.equal errors.Writes 1 "One stderr attempt"

let private everyMutationKeepsUncertainty () =
    for endpoint in (ContractProjection.current ()).CliEndpoints do
        use output = new MemoryStream()
        use errors = new MemoryStream()
        let delivery = FrameDelivery(output, errors)
        delivery.BeginFrame()
        delivery.BeforeRemoteDispatch(endpoint.Identifier, input "{}")
        let code = delivery.Failure(IOException("PRIVATE-SOURCE"))
        let expected = if endpoint.Cancellable then 3 else 4
        Expect.equal code expected $"Delivery class for {endpoint.Identifier}"
        assertPrivacy errors

let tests =
    testList
        "remote frame-local delivery diagnostics"
        [
            testCase
                "a later input failure cannot inherit a delivered operation identity"
                completedFrame
            testCase "a malformed next frame stays independent of completed work" malformedNextFrame
            testCase "a partial write preserves the current exact context without replay" (fun () ->
                failedDelivery false)
            testCase "a failed flush preserves the current operation identity" (fun () ->
                failedDelivery true)
            testCase "service acquisition failure is separate from dispatch failure" acquireFailure
            testCase "validated response observation precedes output" observedEncodingFailure
            testCase
                "uncertain import without an operation ID does not invent one"
                importWithoutIdentity
            testCase "broken diagnostic output cannot loop or repeat an operation" brokenDiagnostics
            testCase
                "[CC-CLI-003] every generated CLI mutation retains delivery uncertainty"
                everyMutationKeepsUncertainty
        ]
