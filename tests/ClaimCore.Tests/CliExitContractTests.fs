module ClaimCore.Tests.CliExitContractTests

open System
open System.Text.Json
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private remote endpoint (bytes: byte array) =
    use document = JsonDocument.Parse(ReadOnlyMemory bytes)
    CliRemoteWireCodec.result endpoint document.RootElement

let private wireFixtures () =
    let clock = businessTime today
    let claims = new CoreStore.Store()
    let recovery = new CoreRecoveryStore.Store()
    recovery.AttachClaimStore(claims :> IClaimStore)

    let core =
        ActorCoreFixture.create (claims :> IClaimStore) (recovery :> IRecoveryStore) clock

    let draft: CommandDraft =
        {
            OperationId = Guid.Parse("60000000-0000-4000-8000-000000000002")
            CaseReference = "CLI-PREPARE-WIRE-001"
            ExpectedVersion = 0L
            Command =
                DraftCommand.Flat(
                    CommandKind.Open,
                    [
                        "incidentDate", registration.IncidentDate
                        "incidentNotificationDate", registration.IncidentNotificationDate
                        "incidentCountry", registration.IncidentCountry
                        "claimantName", registration.ClaimantName
                        "insurerName", registration.InsurerName
                        "claimedAmount", registration.ClaimedAmount
                        "claimedCurrency", registration.ClaimedCurrency
                    ]
                )
        }

    let details =
        match core.Prepare(boundRequest draft, CancellationToken.None).Result with
        | PrepareOutcome.Prepared(details, _) -> details
        | _ -> failtest "Expected a synthetic retained preparation."

    let receipt =
        match core.Execute(boundRequest draft, CancellationToken.None).Result with
        | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted receipt, _) -> receipt
        | _ -> failtest "Expected a synthetic accepted receipt."

    details, receipt

let private lookupAbsence =
    testCase "recovery lookup absence exits two while an empty recovery page succeeds" (fun () ->
        let operationId = Guid.Parse("60000000-0000-4000-8000-000000000001")

        let inspection: RecoveryQueryOutcome<Lookup<RecoveryInspection, Guid>> =
            RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)

        let export: RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>> =
            RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)

        let page: RecoveryQueryOutcome<RecoveryPage> =
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

        Expect.equal
            (WebWireCodec.recoveryInspect inspection |> remote "recovery.inspect").ExitCode
            2
            "Unobserved retained material is an explicit not-found result"

        Expect.equal
            (WebWireCodec.recoveryExport export |> remote "recovery.export").ExitCode
            2
            "Absent export identity has the same not-found exit"

        Expect.equal
            (WebWireCodec.recoveryList page |> remote "recovery.list").ExitCode
            0
            "An empty successful recovery page is not absence"

        Expect.equal
            (WebWireCodec.get (QueryOutcome.Succeeded(Lookup.NotFound "MISSING"))
             |> remote "case.get")
                .ExitCode
            2
            "A case lookup miss is not an empty success"

        Expect.equal
            (WebWireCodec.history (QueryOutcome.Succeeded(Lookup.NotFound "MISSING"))
             |> remote "case.history")
                .ExitCode
            2
            "A history lookup miss is an explicit refusal"

        Expect.equal
            (WebWireCodec.observe (QueryOutcome.Succeeded(Lookup.NotFound operationId))
             |> remote "operation.observe")
                .ExitCode
            2
            "An operation lookup miss cannot be misread as acceptance")

let private prepareReplayWire =
    testCase
        "[CC-REC-001] exact prepare replay wires observed acceptance and retained recovery distinctly"
        (fun () ->
            let details, receipt = wireFixtures ()

            let accepted =
                WebWireCodec.prepare (PrepareOutcome.ObservedAccepted receipt)
                |> remote "command.prepare"

            Expect.equal accepted.ExitCode 0 "Accepted observation is a definite success"
            use acceptedJson = JsonDocument.Parse(accepted.Bytes)

            let acceptedValue =
                acceptedJson.RootElement.GetProperty("service").GetProperty("outcome")

            Expect.equal (acceptedValue.GetProperty("tag").GetString()) "OBSERVED_ACCEPTED" "Tag"

            Expect.equal
                (acceptedValue
                    .GetProperty("data")
                    .GetProperty("receipt")
                    .GetProperty("operationId")
                    .GetString())
                (receipt.OperationId.ToString("D"))
                "Accepted receipt identity"

            let reason: Rejection = Rejection.Domain(DomainError.VersionConflict 1L)

            let retained =
                WebWireCodec.prepare (PrepareOutcome.RetainedForRecovery(details, reason))
                |> remote "command.prepare"

            Expect.equal retained.ExitCode 2 "Non-reviewable exact retention is a refusal"
            use retainedJson = JsonDocument.Parse(retained.Bytes)

            let retainedValue =
                retainedJson.RootElement.GetProperty("service").GetProperty("outcome")

            Expect.equal
                (retainedValue.GetProperty("tag").GetString())
                "RETAINED_FOR_RECOVERY"
                "Recovery tag"

            Expect.equal
                (retainedValue
                    .GetProperty("data")
                    .GetProperty("rejection")
                    .GetProperty("code")
                    .GetString())
                "VERSION_CONFLICT"
                "Typed reason"

            Expect.equal
                (retainedValue
                    .GetProperty("data")
                    .GetProperty("details")
                    .GetProperty("summary")
                    .GetProperty("operationId")
                    .GetString())
                (receipt.OperationId.ToString("D"))
                "Original preparation identity")

let private nestedRecoveryFaults =
    testCase "[CC-CLI-003] nested faults preserve earlier exact-operation uncertainty" (fun () ->
        let details, _ = wireFixtures ()
        let operationId = details.Summary.OperationId

        for fault, expected in [ CoreFault.CommitOutcomeUnknown, 4; CoreFault.StoreUnavailable, 3 ] do
            let replies =
                [
                    "command.prepare",
                    WebWireCodec.prepare (PrepareOutcome.PrepareFailed(operationId, fault))
                    "command.execute",
                    WebWireCodec.submit (SubmissionOutcome.FailedBeforeAttempt(None, fault))
                    "command.execute",
                    WebWireCodec.submit (
                        SubmissionOutcome.Completed(
                            details.Summary,
                            Guid.Parse("60000000-0000-4000-8000-000000000003"),
                            DefiniteExecution.FailedBeforeCommit(operationId, fault),
                            SettlementConfirmation.Confirmed
                        )
                    )
                    "recovery.resolve",
                    WebWireCodec.resolve
                        "recovery.resolve"
                        (ResolveOutcome.ResolveFailedBeforeAttempt(None, fault))
                ]

            for endpoint, bytes in replies do
                Expect.equal
                    (remote endpoint bytes).ExitCode
                    expected
                    "Typed core direction governs delivery knowledge")

let private hostKnowledge =
    testCase
        "[CC-CLI-003] typed host phases preserve read and mutation delivery knowledge"
        (fun () ->
            let cases =
                [
                    WebHostFailure.ConnectionRejected, 3
                    WebHostFailure.OriginRejected, 3
                    WebHostFailure.MediaTypeRejected, 3
                    WebHostFailure.BodyTooLarge, 3
                    WebHostFailure.SessionRejected, 3
                    WebHostFailure.SessionForbidden, 3
                    WebHostFailure.MethodRejected, 3
                    WebHostFailure.AntiforgeryRejected, 3
                    WebHostFailure.EndpointMissing, 3
                    WebHostFailure.Busy, 3
                    WebHostFailure.BeforeDispatchFailed, 3
                    WebHostFailure.DispatchUnconfirmed, 4
                    WebHostFailure.CompletedResponseFailed, 4
                    WebHostFailure.ExportMetadataInvalid, 4
                ]

            for reason, expected in cases do
                use document = JsonDocument.Parse(WebWireCodec.hostFailure reason)

                for endpoint in [ "command.execute"; "recovery.export"; "unclassified.future" ] do
                    Expect.equal
                        (CliRemoteWireCodec.hostFailure endpoint document.RootElement).ExitCode
                        expected
                        "Only known non-dispatch permits definite mutation delivery failure"

                Expect.equal
                    (CliRemoteWireCodec.hostFailure "case.get" document.RootElement).ExitCode
                    3
                    "Read-only dispatch has no mutation uncertainty"

            for reason in HttpInputProblems.all do
                use document =
                    JsonDocument.Parse(WebWireCodec.hostFailure (WebHostFailure.Input reason))

                Expect.equal
                    (document.RootElement.GetProperty("executionPhase").GetString())
                    "NOT_STARTED"
                    "Input refusal precedes dispatch")

let private refusalKnowledge =
    testCase "[CC-CLI-003] a refused dismissal preserves earlier submission uncertainty" (fun () ->
        for reason, expected in
            [
                RecoveryRejection.SubmissionAlreadyStarted, 4
                RecoveryRejection.DismissalConfirmationRequired, 2
            ] do
            let outcome = RecoveryDismissOutcome.DismissRefused(None, reason)
            let reply = WebWireCodec.recoveryDismiss outcome |> remote "recovery.dismiss"
            Expect.equal reply.ExitCode expected "Refusal does not settle earlier attempts")

let tests =
    testList
        "CLI-v4 service exit contract"
        [
            lookupAbsence
            prepareReplayWire
            nestedRecoveryFaults
            hostKnowledge
            refusalKnowledge
        ]
