module ClaimCore.WebTests.WireTests

open System
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures

let private body result =
    let context = DefaultHttpContext()
    context.Response.Body <- new System.IO.MemoryStream()
    execute context result

let private requiredText message (element: JsonElement) =
    element.GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest message)

let private descriptionValue () =
    {
        Contract = SemanticContract.current
        SemanticFingerprint = SemanticContract.fingerprint SemanticContract.current
        Runtime =
            {
                ProductVersion = "0.1.0"
                EffectiveBusinessDate = DateOnly(2026, 9, 9)
                TimeZoneId = "Etc/UTC"
            }
    }

let private sessionEnvelope () =
    use document =
        JsonDocument.Parse(body (WebWire.session "session" false "fresh-token"))

    let root = document.RootElement

    Expect.equal (root.GetProperty("endpoint").GetString()) "session" "Endpoint tag is explicit"

    let data = root.GetProperty("outcome").GetProperty("data")

    Expect.isFalse
        (data.GetProperty("authenticated").GetBoolean())
        "Logout is a SessionSnapshot, not an incompatible scalar envelope"

    Expect.equal
        (data.GetProperty("antiforgeryToken").GetString())
        "fresh-token"
        "The anonymous snapshot carries a fresh antiforgery token"

let private definitionEnvelope () =
    let description = descriptionValue ()

    use document = JsonDocument.Parse(body (WebWire.description description))

    let data = document.RootElement.GetProperty("outcome").GetProperty("data")

    Expect.equal
        (data.GetProperty("semanticFingerprint")
         |> requiredText "Semantic fingerprint is required")
            .Length
        64
        "Semantic core fingerprint is distinct and fixed width"

    Expect.equal
        (data.GetProperty("webFingerprint") |> requiredText "Web fingerprint is required").Length
        64
        "Web wire fingerprint is distinct and fixed width"

    Expect.equal
        (data.GetProperty("definition").GetProperty("contractKind").GetString())
        "SEMANTIC_CORE_V1"
        "Definition comes from the shared Contracts projection"

let private failureEnvelope () =
    let context = DefaultHttpContext()
    context.Response.Body <- new System.IO.MemoryStream()

    use document =
        WebWire.hostFailure WebHostFailure.BodyTooLarge
        |> execute context
        |> JsonDocument.Parse

    Expect.equal
        context.Response.StatusCode
        413
        "Admission failure status stays an HTTP host status"

    Expect.equal
        (document.RootElement.GetProperty("kind").GetString())
        "HOST_FAILURE"
        "Host failure bodies have a separate typed tag"

let private contractOwnedTransport () =
    Expect.equal
        (WebContract.path "session")
        "/api/v2/session"
        "Session routing comes from ClaimCore.Contracts"

    Expect.equal
        (WebContract.jsonPath "command.execute")
        "/api/v2/operations/submit"
        "The submit path follows the generated endpoint ID"

    let path, mediaType, maximumBytes, headers =
        WebContract.raw "recovery.importEnvelopeRetain"

    Expect.equal path "/api/v2/recovery/import-envelope/retain" "Raw endpoint path is generated"

    Expect.equal
        mediaType
        "application/vnd.claimcore.recovery+json"
        "Raw recovery media type is generated"

    Expect.equal maximumBytes 131072 "Raw recovery limit is generated"
    Expect.equal headers [ "X-ClaimCore-Source-Sha256" ] "Raw retain proof header is generated"

let private commandEndpointIdentity () =
    let operationId = System.Guid.Parse("40000000-0000-4000-8000-000000000001")

    use document =
        WebWire.prepare "command.prepare" (PrepareOutcome.CancelledBeforeAdmission operationId)
        |> body
        |> JsonDocument.Parse

    Expect.equal
        (document.RootElement.GetProperty("endpoint").GetString())
        "command.prepare"
        "The host preserves the generated command endpoint ID rather than an adapter alias"

let private same label (contract: byte array) result =
    Expect.equal (body result) (Encoding.UTF8.GetString(contract)) label

let private coreHostWrappers () =
    let missingCase: QueryOutcome<Lookup<CurrentCase, string>> =
        QueryOutcome.Succeeded(Lookup.NotFound "SYNTHETIC-CASE")

    let missingHistory: QueryOutcome<Lookup<HistoryResultPage, string>> =
        QueryOutcome.Succeeded(Lookup.NotFound "SYNTHETIC-CASE")

    let missingOperation: QueryOutcome<Lookup<OperationReceipt, Guid>> =
        QueryOutcome.Succeeded(Lookup.NotFound operationId)

    same
        "Session wrapper"
        (WebWireCodec.session "session.login" true (Some "synthetic-token"))
        (WebWire.session "session.login" true "synthetic-token")

    same
        "Definition wrapper"
        (WebWireCodec.description (descriptionValue ()))
        (WebWire.description (descriptionValue ()))

    same "Case wrapper" (WebWireCodec.get missingCase) (WebWire.get missingCase)

    let casePage: QueryOutcome<CaseSummaryPage> =
        QueryOutcome.Succeeded
            {
                Items = []
                NextAfterReference = None
            }

    same "List wrapper" (WebWireCodec.list casePage) (WebWire.list casePage)
    same "History wrapper" (WebWireCodec.history missingHistory) (WebWire.history missingHistory)

    same
        "Observation wrapper"
        (WebWireCodec.observe missingOperation)
        (WebWire.observe missingOperation)

    let prepare = PrepareOutcome.CancelledBeforeAdmission operationId

    same
        "Prepare wrapper"
        (WebWireCodec.prepare prepare)
        (WebWire.prepare "command.prepare" prepare)

let private recoveryPage () : RecoveryQueryOutcome<RecoveryPage> =
    RecoveryQueryOutcome.RecoverySucceeded
        {
            View = RecoveryListView.Pending
            Items = []
            NextCursor = None
            PendingPreparationCount = 0
            PendingCanonicalRequestBytes = 0L
            MaximumPendingPreparations = 1024
            MaximumPendingCanonicalRequestBytes = 64L * 1024L * 1024L
            NearCapacity = false
        }

let private recoveryQueryWrappers () =
    let missingDetails: RecoveryQueryOutcome<Lookup<RecoveryInspection, Guid>> =
        RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)

    same
        "Recovery list wrapper"
        (WebWireCodec.recoveryList (recoveryPage ()))
        (WebRecoveryWire.list (recoveryPage ()))

    same
        "Recovery inspect wrapper"
        (WebWireCodec.recoveryInspect missingDetails)
        (WebRecoveryWire.inspect missingDetails)

let private recoveryResolutionWrappers () =
    let resolve =
        ResolveOutcome.RefusedBeforeAttempt(None, RecoveryRejection.PreparationNotFound)

    same
        "Resolve wrapper"
        (WebWireCodec.resolve "recovery.resolve" resolve)
        (WebRecoveryWire.resolve "recovery.resolve" resolve)

    let cancelledResolve = ResolveOutcome.ResolveCancelledBeforeAdmission operationId

    same
        "Cancelled resolve wrapper"
        (WebWireCodec.resolve "recovery.resolve" cancelledResolve)
        (WebRecoveryWire.resolve "recovery.resolve" cancelledResolve)

    let dismiss = RecoveryDismissOutcome.DismissNotFound operationId
    same "Dismiss wrapper" (WebWireCodec.recoveryDismiss dismiss) (WebRecoveryWire.dismiss dismiss)

let private recoveryTransferWrappers () =
    let missingExport: RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>> =
        RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)

    same
        "Export wrapper"
        (WebWireCodec.recoveryExport missingExport)
        (WebRecoveryWire.export missingExport)

    let preview = RecoveryQueryOutcome.RecoveryCancelled

    same
        "Import-preview wrapper"
        (WebWireCodec.importPreview "recovery.importEnvelopePreview" preview)
        (WebRecoveryWire.importQuery "recovery.importEnvelopePreview" preview)

    let retain = RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission

    same
        "Import-retain wrapper"
        (WebWireCodec.importRetain "recovery.importEnvelopeRetain" retain)
        (WebRecoveryWire.importRetain "recovery.importEnvelopeRetain" retain)

let private recoveryHostWrappers () =
    recoveryQueryWrappers ()
    recoveryResolutionWrappers ()
    recoveryTransferWrappers ()

let private allHostWrappersUseContractBytes () =
    coreHostWrappers ()
    recoveryHostWrappers ()

let tests =
    testList
        "Web HTTP-v2 wire projection"
        [
            testCase
                "[CC-WEB-001] serializes logout as an anonymous session snapshot"
                sessionEnvelope
            testCase
                "[CC-WEB-001] exposes independently fingerprinted semantic and Web contracts"
                definitionEnvelope
            testCase
                "[CC-WEB-001] keeps host admission errors outside application outcomes"
                failureEnvelope
            testCase
                "[CC-WEB-001] consumes HTTP routes and raw constraints from ClaimCore.Contracts"
                contractOwnedTransport
            testCase
                "[CC-WEB-001] preserves command endpoint identities in typed outcome bodies"
                commandEndpointIdentity
            testCase
                "[CC-WEB-001] all JSON host wrappers deliver Contracts-owned codec bytes"
                allHostWrappersUseContractBytes
        ]
