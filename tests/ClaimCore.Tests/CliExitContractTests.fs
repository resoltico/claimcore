module ClaimCore.Tests.CliExitContractTests

open System
open System.Text.Json
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private wireFixtures () =
    let clock =
        { new IBusinessDate with
            member _.Today() = today
        }

    let core =
        CoreApi.create
            (new CoreStore.Store() :> IClaimStore)
            (new CoreRecoveryStore.Store() :> IRecoveryStore)
            clock

    let draft =
        {
            OperationId = Guid.Parse("60000000-0000-4000-8000-000000000002")
            CaseReference = "CLI-PREPARE-WIRE-001"
            ExpectedVersion = 0L
            Kind = CommandKind.Open
            Values =
                [
                    "incidentDate", registration.IncidentDate
                    "incidentNotificationDate", registration.IncidentNotificationDate
                    "incidentCountry", registration.IncidentCountry
                    "claimantName", registration.ClaimantName
                    "insurerName", registration.InsurerName
                    "claimedAmount", registration.ClaimedAmount
                    "claimedCurrency", registration.ClaimedCurrency
                ]
        }

    let details =
        match core.Prepare(draft, CancellationToken.None).Result with
        | PrepareOutcome.Prepared(details, _) -> details
        | _ -> failtest "Expected a synthetic retained preparation."

    let receipt =
        match core.Execute(draft, CancellationToken.None).Result with
        | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted receipt, _) -> receipt
        | _ -> failtest "Expected a synthetic accepted receipt."

    details, receipt

let private lookupAbsence =
    testCase "recovery lookup absence exits two while an empty recovery page succeeds" (fun () ->
        let operationId = Guid.Parse("60000000-0000-4000-8000-000000000001")

        let inspection: RecoveryQueryOutcome<Lookup<RecoveryDetails, Guid>> =
            RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)

        let export: RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>> =
            RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)

        let page: RecoveryQueryOutcome<RecoveryPage> =
            RecoveryQueryOutcome.RecoverySucceeded { Items = []; NextCursor = None }

        Expect.equal
            (CliWireCodec.recoveryInspect "recovery.inspect" inspection).ExitCode
            2
            "Unobserved retained material is an explicit not-found result"

        Expect.equal
            (CliWireCodec.recoveryExport "recovery.export" operationId export).ExitCode
            2
            "Absent export identity has the same not-found exit"

        Expect.equal
            (CliWireCodec.recoveryList "recovery.list" page).ExitCode
            0
            "An empty successful recovery page is not absence")

let private prepareReplayWire =
    testCase
        "[CC-REC-001] exact prepare replay wires observed acceptance and retained recovery distinctly"
        (fun () ->
            let details, receipt = wireFixtures ()

            let accepted =
                CliWireCodec.prepare "command.prepare" (PrepareOutcome.ObservedAccepted receipt)

            Expect.equal accepted.ExitCode 0 "Accepted observation is a definite success"
            use acceptedJson = JsonDocument.Parse(accepted.Bytes)
            let acceptedValue = acceptedJson.RootElement.GetProperty("outcome")
            Expect.equal (acceptedValue.GetProperty("kind").GetString()) "observedAccepted" "Kind"

            Expect.equal
                (acceptedValue.GetProperty("receipt").GetProperty("operationId").GetString())
                (receipt.OperationId.ToString("D"))
                "Accepted receipt identity"

            let reason: Rejection =
                {
                    Code = RejectionCode.VersionConflict
                    Message = "Synthetic state is no longer reviewable."
                    Field = None
                    ActualVersion = Some 1L
                    Action = RecommendedAction.ReadCurrent
                }

            let retained =
                CliWireCodec.prepare
                    "command.prepare"
                    (PrepareOutcome.RetainedForRecovery(details, reason))

            Expect.equal retained.ExitCode 2 "Non-reviewable exact retention is a refusal"
            use retainedJson = JsonDocument.Parse(retained.Bytes)
            let retainedValue = retainedJson.RootElement.GetProperty("outcome")

            Expect.equal
                (retainedValue.GetProperty("kind").GetString())
                "retainedForRecovery"
                "Recovery kind"

            Expect.equal
                (retainedValue.GetProperty("rejection").GetProperty("code").GetString())
                "VERSION_CONFLICT"
                "Typed reason"

            Expect.equal
                (retainedValue
                    .GetProperty("details")
                    .GetProperty("summary")
                    .GetProperty("operationId")
                    .GetString())
                (receipt.OperationId.ToString("D"))
                "Original preparation identity")

let tests = testList "CLI-v3 exit contract" [ lookupAbsence; prepareReplayWire ]
