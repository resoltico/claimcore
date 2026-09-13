module ClaimCore.Tests.ExactReplayTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private clock =
    { new IBusinessDate with
        member _.Today() = today
    }

let private draft operationId reference =
    {
        OperationId = operationId
        CaseReference = reference
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

let private await (task: System.Threading.Tasks.Task<'value>) = task.GetAwaiter().GetResult()

let private prepared value =
    match value with
    | PrepareOutcome.Prepared(details, _) -> details
    | _ -> failtest "Expected an advisory preparation."

let private exactAcceptedReplay =
    testCase
        "[CC-REC-001] exact Prepare retry after acceptance returns an observed receipt"
        (fun () ->
            let claims = new CoreStore.Store()
            let recovery = new CoreRecoveryStore.Store()
            let core = CoreApi.create claims recovery clock
            let operationId = Guid.NewGuid()
            let input = draft operationId "REPLAY-ACCEPTED"
            let first = core.Prepare(input, CancellationToken.None) |> await |> prepared

            let digest =
                first.Summary.RequestSha256 |> Option.defaultWith (fun () -> failtest "Digest")

            match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> await with
            | ResolveOutcome.ResolveCompleted(_, _, DefiniteExecution.Accepted _, _) -> ()
            | _ -> failtest "The original exact operation must accept."

            match core.Prepare(input, CancellationToken.None) |> await with
            | PrepareOutcome.ObservedAccepted(details, receipt) ->
                Expect.equal
                    details.Summary.PreparedAt
                    first.Summary.PreparedAt
                    "First writer retained"

                Expect.equal
                    details.Summary.AvailableActions
                    [ RecoveryAction.Export ]
                    "Accepted replay advertises export only"

                Expect.equal receipt.OperationId operationId "Same accepted operation"
                Expect.isTrue receipt.Replayed "Accepted observation is content-bound replay"
                Expect.equal receipt.Snapshot.Version 1L "No second case revision"
            | _ -> failtest "Exact accepted retry must not become a stale-version rejection.")

let private retainedAfterOtherChange =
    testCase
        "[CC-REC-001] exact retained Prepare retry after another commit directs Recovery"
        (fun () ->
            let core =
                CoreApi.create
                    (new CoreStore.Store() :> IClaimStore)
                    (new CoreRecoveryStore.Store() :> IRecoveryStore)
                    clock

            let original = draft (Guid.NewGuid()) "REPLAY-STALE"
            let first = core.Prepare(original, CancellationToken.None) |> await |> prepared
            let competing = draft (Guid.NewGuid()) "REPLAY-STALE"

            match core.Execute(competing, CancellationToken.None) |> await with
            | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted _, _) -> ()
            | _ -> failtest "A distinct operation must advance the case."

            match core.Prepare(original, CancellationToken.None) |> await with
            | PrepareOutcome.RetainedForRecovery(details, reason) ->
                Expect.equal
                    details.Summary.OperationId
                    original.OperationId
                    "Exact retained identity"

                Expect.equal
                    details.Summary.PreparedAt
                    first.Summary.PreparedAt
                    "First writer retained"

                Expect.equal
                    reason.Code
                    RejectionCode.VersionConflict
                    "Fresh state made review stale"
            | _ -> failtest "Retained exact retry must not invent an advisory review.")

let private spoofedDigestCannotBypassBytes =
    testCase
        "[CC-REC-001] Execute rejects a same-ID same-digest record with different canonical bytes"
        (fun () ->
            let mutable spoof = false
            let operationId = Guid.NewGuid()
            let original = draft operationId "REPLAY-EXACT-BYTES"
            let alternate = draft operationId "REPLAY-DIFFERENT-BYTES"

            let otherBytes =
                alternate
                |> Drafts.bind
                |> Result.defaultWith (fun _ -> failtest "Synthetic draft")
                |> RequestRecord.encode

            let recovery =
                new CoreRecoveryStore.Store(
                    transformGet =
                        (fun value ->
                            if spoof then
                                { value with
                                    CanonicalRequest = otherBytes
                                }
                            else
                                value)
                )

            let claims = new CoreStore.Store()
            let core = CoreApi.create claims recovery clock
            core.Prepare(original, CancellationToken.None) |> await |> prepared |> ignore
            spoof <- true

            match core.Execute(original, CancellationToken.None) |> await with
            | SubmissionOutcome.RejectedBeforeAttempt(_, reason) ->
                Expect.equal
                    reason.Code
                    RejectionCode.IdempotencyConflict
                    "Exact bytes are required"
            | _ -> failtest "A spoofed digest must not bypass exact canonical identity."

            Expect.equal claims.TransactionCalls 0 "No claim transaction may begin"
            Expect.equal recovery.StartCalls 0 "No recovery attempt may start")

let private atomicImportOutcome =
    testCase
        "[CC-REC-001] concurrent canonical imports classify creator and existing replay"
        (fun () ->
            let core =
                CoreApi.create
                    (new CoreStore.Store() :> IClaimStore)
                    (new CoreRecoveryStore.Store() :> IRecoveryStore)
                    clock

            let canonical =
                draft (Guid.NewGuid()) "IMPORT-ATOMIC"
                |> Drafts.bind
                |> Result.defaultWith (fun _ -> failtest "Synthetic draft")
                |> RequestRecord.encode

            let digest = canonical |> SHA256.HashData |> Convert.ToHexStringLower

            let outcomes =
                [|
                    core.Recovery.RetainCanonicalRecordImport(
                        canonical,
                        digest,
                        CancellationToken.None
                    )
                    core.Recovery.RetainCanonicalRecordImport(
                        canonical,
                        digest,
                        CancellationToken.None
                    )
                |]
                |> System.Threading.Tasks.Task.WhenAll
                |> await

            let created =
                outcomes
                |> Array.filter (function
                    | RecoveryImportRetainOutcome.RetainedPreparation _ -> true
                    | _ -> false)

            let existing =
                outcomes
                |> Array.filter (function
                    | RecoveryImportRetainOutcome.ExistingPreparation _ -> true
                    | _ -> false)

            Expect.equal created.Length 1 "One import retained"
            Expect.equal existing.Length 1 "The other import observed the exact retained bytes")

let tests =
    testList
        "exact retained identity"
        [
            exactAcceptedReplay
            retainedAfterOtherChange
            spoofedDigestCannotBypassBytes
            atomicImportOutcome
        ]
