module ClaimCore.Tests.FrameDeliveryTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Cli
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.DiagnosticTestStreams

let private id = Guid.Parse("91000000-0000-4000-8000-000000000001")

let private frame =
    $"""{{"protocolVersion":3,"endpoint":"command.execute","input":{{"operationId":"{id:D}","caseReference":"DELIVERY-001","expectedRevision":"0","command":{{"kind":"OPEN","values":{{"incidentDate":"2026-08-01","incidentNotificationDate":"2026-08-03","incidentCountry":"Lithuania","claimantName":"Example Claimant Ltd","insurerName":"Example Insurer","claimedAmount":"1000.00","claimedCurrency":"EUR"}}}}}}}}"""

let private fixture () =
    let store = new CoreStore.Store()
    let recovery = new CoreRecoveryStore.Store()
    recovery.AttachClaimStore(store :> IClaimStore)

    let core =
        CoreApi.create (store :> IClaimStore) (recovery :> IRecoveryStore) (businessTime today)

    let supplier =
        { new ICoreSupplier with
            member _.Acquire _ = Task.FromResult(Ok core)
        }

    supplier, store

let private assertPrivacy errors =
    for forbidden in [ "PRIVATE-"; "Example Claimant"; "DELIVERY-001"; "IOException" ] do
        Expect.isFalse
            ((text errors).Contains(forbidden, StringComparison.Ordinal))
            "No exception or authored payload in diagnostics"

let private laterReadFailure () =
    let supplier, store = fixture ()
    use input = new EndReadFailure(bytes (frame + "\n"))
    use output = new MemoryStream()
    use errors = new MemoryStream()

    let code =
        FrameProcessing.session supplier input output errors CancellationToken.None

    let value = parsed errors
    Expect.equal code 3 "Read failure is not an earlier operation's unknown completion"
    Expect.equal (diagnosticId value) "CLI_INPUT_READ_FAILED" "Frame-local reason"

    Expect.equal
        (value.GetProperty("operationId").ValueKind)
        System.Text.Json.JsonValueKind.Null
        "Previous ID cleared"

    Expect.isFalse
        (value.GetProperty("potentiallyChanged").GetBoolean())
        "No second mutation was dispatched"

    Expect.equal store.TransactionCalls 1 "Exactly one admitted claim transaction"
    assertPrivacy errors

let private malformedNextFrame () =
    let supplier, store = fixture ()
    use input = new MemoryStream(bytes (frame + "\n{\n"))
    use output = new MemoryStream()
    use errors = new MemoryStream()

    Expect.equal
        (FrameProcessing.session supplier input output errors CancellationToken.None)
        0
        "Session delivers both frames"

    let lines = (text output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
    Expect.equal lines.Length 2 "Exactly one result per input frame"

    Expect.isFalse
        (lines[1].Contains(id.ToString("D"), StringComparison.Ordinal))
        "Malformed later frame carries no prior identity"

    Expect.equal store.TransactionCalls 1 "No recovery or new mutation"
    Expect.equal errors.Length 0L "A malformed frame is a protocol result"

let private failedDelivery flush =
    let supplier, store = fixture ()
    use input = new MemoryStream(bytes frame)
    use output = new BrokenOutput(flush)
    use errors = new MemoryStream()
    let code = FrameProcessing.call supplier input output errors CancellationToken.None
    let value = parsed errors
    Expect.equal code 4 "Lost delivery must not authorize safe retry"
    Expect.equal (diagnosticId value) "CLI_OUTPUT_DELIVERY_FAILED" "Delivery, not core failure"

    Expect.equal
        (value.GetProperty("operationId").GetString())
        (id.ToString("D"))
        "Exact current operation retained"

    Expect.equal
        (value.GetProperty("requestSha256").GetString()
         |> Option.ofObj
         |> Option.map String.length
         |> Option.defaultValue 0)
        64
        "Observed typed result supplies its exact digest"

    Expect.equal
        (value.GetProperty("resultExitCode").GetInt32())
        0
        "The returned result was accepted"

    Expect.equal output.Writes 1 "No second stdout frame after partial delivery"
    Expect.equal store.TransactionCalls 1 "The operation is not replayed"
    assertPrivacy errors

let private acquireFailure () =
    let supplier =
        { new ICoreSupplier with
            member _.Acquire _ =
                Task.FromException<Result<IClaimsCore, CoreUnavailable>>(
                    IOException("PRIVATE-SUPPLIER")
                )
        }

    use input = new MemoryStream(bytes frame)
    use output = new MemoryStream()
    use errors = new MemoryStream()

    Expect.equal
        (FrameProcessing.call supplier input output errors CancellationToken.None)
        3
        "Supplier failed before dispatch"

    let value = parsed errors

    Expect.equal
        (diagnosticId value)
        "CLI_RUNTIME_ACQUIRE_FAILED"
        "Not misreported as a stream read"

    Expect.isFalse (value.GetProperty("potentiallyChanged").GetBoolean()) "Core never acquired"
    assertPrivacy errors

let private encodingFailure () =
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let delivery = FrameDelivery(output, errors)

    let draft =
        {
            OperationId = id
            CaseReference = "ENCODING"
            ExpectedVersion = 0L
            Command = DraftCommand.Flat(CommandKind.Open, [])
        }

    let reply =
        EndpointReply.Prepare(PrepareOutcome.PrepareFailed(id, Unchecked.defaultof<CoreFault>))

    delivery.BeginFrame()
    delivery.BeforeDispatch(Endpoint.CommandPrepare, EndpointInput.Draft draft)
    delivery.Observe reply

    let code =
        try
            EndpointReply.encode reply |> ignore
            failtest "Synthetic invalid native value must fail encoding"
        with error ->
            delivery.Failure error

    Expect.equal code 4 "Encoding cannot prove no mutation"

    Expect.equal
        (diagnosticId (parsed errors))
        "CLI_RESULT_ENCODING_FAILED"
        "Returned outcome was observed before serialization"

    Expect.equal output.Length 0L "No corrupt response prefix"
    assertPrivacy errors

let private importIdentity () =
    use output = new MemoryStream()
    use errors = new MemoryStream()
    let delivery = FrameDelivery(output, errors)
    delivery.BeginFrame()

    delivery.BeforeDispatch(
        Endpoint.RecoveryImportEnvelopeRetain,
        EndpointInput.RecoveryImportRetain("PRIVATE-SOURCE", String.replicate 64 "a")
    )

    delivery.Observe(
        EndpointReply.EnvelopeRetain(
            RecoveryImportRetainOutcome.RetainStateUnknown(
                RecoveryArtifactKind.Envelope,
                String.replicate 64 "b",
                Some id,
                CoreFault.RecoveryMutationUnknown
            )
        )
    )

    Expect.equal
        (delivery.Failure(IOException("PRIVATE-FAILURE")))
        4
        "Technical retention is potentially state-changing"

    Expect.equal
        ((parsed errors).GetProperty("operationId").GetString())
        (id.ToString("D"))
        "Import identity comes from the typed outcome"

    assertPrivacy errors

let private brokenDiagnostics () =
    use output = new BrokenOutput(false)
    use errors = new BrokenOutput(false)
    let supplier, store = fixture ()
    use input = new MemoryStream(bytes frame)

    Expect.equal
        (FrameProcessing.call supplier input output errors CancellationToken.None)
        4
        "Failure reporting is nonrecursive and bounded"

    Expect.equal errors.Writes 1 "One stderr attempt"
    Expect.equal store.TransactionCalls 1 "No implicit replay"

let tests =
    testList
        "frame-local delivery diagnostics"
        [
            testCase
                "a later input failure cannot inherit a delivered operation identity"
                laterReadFailure
            testCase "a malformed next frame stays independent of completed work" malformedNextFrame
            testCase "a partial write preserves the current exact context without replay" (fun () ->
                failedDelivery false)
            testCase "a failed flush preserves the observed outcome and request digest" (fun () ->
                failedDelivery true)
            testCase
                "runtime acquisition failure is separate from input and dispatch failure"
                acquireFailure
            testCase "native outcome observation precedes serialization" encodingFailure
            testCase
                "uncertain import retention captures its newly known operation identity"
                importIdentity
            testCase "broken diagnostic output cannot loop or repeat an operation" brokenDiagnostics
        ]
