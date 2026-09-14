module ClaimCore.WebTests.TestServerRecoveryOutcomeTests

open System.Net.Http
open Expecto
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures
open ClaimCore.WebTests.TestServerFixture
open ClaimCore.WebTests.TestServerOutcomeValues

let private operationInput =
    $"""{{"operationId":"{operationId:D}","attemptLimit":10}}"""

let private resolutionInput =
    $"""{{"operationId":"{operationId:D}","requestSha256":"{digest}"}}"""

let private dismissInput =
    $"""{{"operationId":"{operationId:D}","requestSha256":"{digest}","confirmed":true}}"""

let private listOutcomes (host: Host) token =
    for value, expected in
        [
            RecoveryQueryOutcome.RecoverySucceeded
                {
                    View = RecoveryListView.Pending
                    Items = [ RetainedRecoveryItem preparationSummary ]
                    NextCursor = None
                    PendingPreparationCount = 1
                    PendingCanonicalRequestBytes = 128L
                    MaximumPendingPreparations = 1024
                    MaximumPendingCanonicalRequestBytes = 64L * 1024L * 1024L
                    NearCapacity = false
                },
            "SUCCEEDED"
            RecoveryQueryOutcome.RecoveryRejected recoveryRejection, "REJECTED"
            RecoveryQueryOutcome.RecoveryFailed fault, "FAILED"
            RecoveryQueryOutcome.RecoveryCancelled, "CANCELLED"
        ] do
        host.Runtime.RecoveryListOutcome <- Some value

        let data =
            postJson host token "recovery.list" """{"limit":10}"""
            |> tagged "recovery.list" expected

        if expected = "SUCCEEDED" then
            Expect.equal
                (data.GetProperty("items").GetArrayLength())
                1
                "Recovery list carries summaries"

let private inspectionOutcomes (host: Host) token =
    for value, expected, lookup in
        [
            RecoveryQueryOutcome.RecoverySucceeded(
                Lookup.Found(RecoveryInspection.RetainedInspection recoveryDetails)
            ),
            "SUCCEEDED",
            Some "FOUND"
            RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId),
            "SUCCEEDED",
            Some "NOT_FOUND"
            RecoveryQueryOutcome.RecoveryRejected recoveryRejection, "REJECTED", None
            RecoveryQueryOutcome.RecoveryFailed fault, "FAILED", None
            RecoveryQueryOutcome.RecoveryCancelled, "CANCELLED", None
        ] do
        host.Runtime.RecoveryInspectOutcome <- Some value

        let data =
            postJson host token "recovery.inspect" operationInput
            |> tagged "recovery.inspect" expected

        lookup
        |> Option.iter (fun branch ->
            Expect.equal
                (data.GetProperty("tag").GetString())
                branch
                "Exact recovery inspection lookup")

let private resolutionOutcomes (host: Host) token =
    for value, expected in
        [
            ResolveOutcome.ResolveObservedAccepted receipt, "OBSERVED_ACCEPTED"
            ResolveOutcome.ResolveCompleted(
                preparationSummary,
                attemptId,
                DefiniteExecution.Accepted receipt,
                SettlementConfirmation.Confirmed
            ),
            "COMPLETED"
            ResolveOutcome.RefusedBeforeAttempt(Some preparationSummary, recoveryRejection),
            "REFUSED_BEFORE_ATTEMPT"
            ResolveOutcome.ResolveFailedBeforeAttempt(Some preparationSummary, fault),
            "FAILED_BEFORE_ATTEMPT"
            ResolveOutcome.ResolveCancelledBeforeAdmission operationId, "CANCELLED_BEFORE_ADMISSION"
            ResolveOutcome.ResolveCancelledBeforeAttempt preparationSummary,
            "CANCELLED_BEFORE_ATTEMPT"
            ResolveOutcome.ResolveAttemptAdmissionUnknown(preparationSummary, fault),
            "ATTEMPT_ADMISSION_UNKNOWN"
            ResolveOutcome.ResolveAttemptUnresolved(preparationSummary, attemptId, fault),
            "ATTEMPT_UNRESOLVED"
        ] do
        host.Runtime.ResolveOutcome <- Some value

        let data =
            postJson host token "recovery.resolve" resolutionInput
            |> tagged "recovery.resolve" expected

        if expected = "COMPLETED" then
            Expect.equal
                (data.GetProperty("settlement").GetString())
                "CONFIRMED"
                "Resolution keeps settlement distinct"

let private dismissalOutcomes (host: Host) token =
    for value, expected in
        [
            RecoveryDismissOutcome.DismissedPreparation preparationDetails, "DISMISSED"
            RecoveryDismissOutcome.AlreadyDismissedPreparation preparationDetails,
            "ALREADY_DISMISSED"
            RecoveryDismissOutcome.DismissNotFound operationId, "NOT_FOUND"
            RecoveryDismissOutcome.DismissRefused(Some preparationDetails, recoveryRejection),
            "REFUSED"
            RecoveryDismissOutcome.DismissFailed fault, "FAILED"
            RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId,
            "CANCELLED_BEFORE_ADMISSION"
            RecoveryDismissOutcome.DismissStateUnknown(operationId, digest, fault),
            "DISMISS_STATE_UNKNOWN"
        ] do
        host.Runtime.DismissOutcome <- Some value

        let data =
            postJson host token "recovery.dismiss" dismissInput
            |> tagged "recovery.dismiss" expected

        if expected = "DISMISS_STATE_UNKNOWN" then
            Expect.equal
                (data.GetProperty("requestSha256").GetString())
                digest
                "Dismiss ambiguity retains exact digest"

let private exportOutcomes (host: Host) token =
    let target = $"""{{"operationId":"{operationId:D}","requestSha256":"{digest}"}}"""

    let artifact =
        {
            Bytes = System.Text.Encoding.UTF8.GetBytes("synthetic-export")
            FileName = $"claimcore-recovery-{operationId:D}.json"
            MediaType = "application/vnd.claimcore.recovery+json"
            RequestSha256 = digest
        }

    host.Runtime.ExportOutcome <-
        Some(RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact))

    let exported = postJson host token "recovery.export" target
    Expect.equal exported.Status 200 "Valid recovery export is an attachment"
    Expect.stringContains exported.ContentType artifact.MediaType "Exact export media type"
    Expect.equal exported.Body "synthetic-export" "Exact synthetic bytes are delivered"

    for value, expected in
        [
            RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId), "NOT_FOUND"
            RecoveryQueryOutcome.RecoveryRejected recoveryRejection, "REJECTED"
            RecoveryQueryOutcome.RecoveryFailed fault, "FAILED"
            RecoveryQueryOutcome.RecoveryCancelled, "CANCELLED"
        ] do
        host.Runtime.ExportOutcome <- Some value

        postJson host token "recovery.export" target
        |> tagged "recovery.export" expected
        |> ignore

let private recoveryRouteOutcomes () =
    use host = Host.Start()
    let token = authenticated host
    listOutcomes host token
    inspectionOutcomes host token
    resolutionOutcomes host token
    dismissalOutcomes host token
    exportOutcomes host token

    Expect.equal
        host.Runtime.RecoveryCalls
        29
        "Every recovery route variant traversed typed workflow"

let private previewOutcomes (host: Host) token endpoint setOutcome =
    for value, expected in
        [
            RecoveryQueryOutcome.RecoverySucceeded importPreview, "SUCCEEDED"
            RecoveryQueryOutcome.RecoveryRejected recoveryRejection, "REJECTED"
            RecoveryQueryOutcome.RecoveryFailed fault, "FAILED"
            RecoveryQueryOutcome.RecoveryCancelled, "CANCELLED"
        ] do
        setOutcome value
        let data = postRaw host token endpoint "{}" |> tagged endpoint expected

        if expected = "SUCCEEDED" then
            Expect.equal
                (data.GetProperty("sourceSha256").GetString())
                digest
                "Preview hashes exact source"

let private retainOutcomes (host: Host) token endpoint artifactKind setOutcome =
    for value, expected in
        [
            RecoveryImportRetainOutcome.RetainedPreparation preparationDetails, "RETAINED"
            RecoveryImportRetainOutcome.ExistingPreparation preparationDetails, "EXISTING"
            RecoveryImportRetainOutcome.ImportRejected recoveryRejection, "REJECTED"
            RecoveryImportRetainOutcome.ImportFailed fault, "FAILED"
            RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission, "CANCELLED_BEFORE_ADMISSION"
            RecoveryImportRetainOutcome.RetainStateUnknown(
                artifactKind,
                digest,
                Some operationId,
                fault
            ),
            "RETAIN_STATE_UNKNOWN"
        ] do
        setOutcome value
        let data = postRaw host token endpoint "{}" |> tagged endpoint expected

        if expected = "RETAIN_STATE_UNKNOWN" then
            Expect.equal
                (data.GetProperty("sourceSha256").GetString())
                digest
                "Retain ambiguity preserves source digest"

let private importRouteOutcomes () =
    use host = Host.Start()
    let token = authenticated host

    previewOutcomes host token "recovery.importEnvelopePreview" (fun value ->
        host.Runtime.EnvelopePreviewOutcome <- Some value)

    previewOutcomes host token "recovery.importRecordPreview" (fun value ->
        host.Runtime.RecordPreviewOutcome <- Some value)

    retainOutcomes
        host
        token
        "recovery.importEnvelopeRetain"
        RecoveryArtifactKind.Envelope
        (fun value -> host.Runtime.EnvelopeRetainOutcome <- Some value)

    retainOutcomes
        host
        token
        "recovery.importRecordRetain"
        RecoveryArtifactKind.UnboundCanonicalRecord
        (fun value -> host.Runtime.RecordRetainOutcome <- Some value)

    Expect.equal
        host.Runtime.RecoveryCalls
        20
        "Both raw artifact formats traverse only typed recovery"

let tests =
    testList
        "Web HTTP-v2 TestServer"
        [
            testCase
                "[CC-WEB-001] recovery list, inspect, resolve, dismiss, and export preserve typed lifecycle outcomes"
                recoveryRouteOutcomes
            testCase
                "[CC-WEB-001] recovery preview and retain routes preserve source digest, refusal, and uncertainty outcomes"
                importRouteOutcomes
        ]
