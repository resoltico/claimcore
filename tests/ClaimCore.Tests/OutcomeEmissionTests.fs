module ClaimCore.Tests.OutcomeEmissionTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Tests.Fixtures

let private operationId = Guid.Parse("40000000-0000-4000-8000-000000000101")
let private otherId = Guid.Parse("40000000-0000-4000-8000-000000000102")
let private digest = String.replicate 64 "a"
let private none = CancellationToken.None
let private wait (value: Task<'value>) = value.GetAwaiter().GetResult()

let private create () =
    let store = new CoreStore.Store()
    let recovery = new CoreRecoveryStore.Store()
    recovery.AttachClaimStore(store :> IClaimStore)

    CoreApi.create (store :> IClaimStore) (recovery :> IRecoveryStore) (businessTime today),
    store,
    recovery

let private refusal expected actual =
    match actual with
    | RecoveryQueryOutcome.RecoveryRejected reason ->
        Expect.equal reason expected "Owning admission selects the cause"
    | _ -> failtest "Expected a definite lifecycle refusal."

let private queryCauses () =
    let core, store, recovery = create ()
    let api = core.Recovery

    api.List(RecoveryListView.Pending, None, 0, none)
    |> wait
    |> refusal RecoveryRejection.PageLimitOutOfRange

    api.List(RecoveryListView.Pending, Some "DO-NOT-ECHO", 50, none)
    |> wait
    |> refusal RecoveryRejection.ListCursorInvalid

    api.Inspect(Guid.Empty, None, 50, none)
    |> wait
    |> refusal RecoveryRejection.OperationIdRequired

    api.Inspect(operationId, None, 51, none)
    |> wait
    |> refusal RecoveryRejection.PageLimitOutOfRange

    api.Inspect(operationId, Some "DO-NOT-ECHO", 50, none)
    |> wait
    |> refusal RecoveryRejection.AttemptCursorInvalid

    Expect.equal store.TransactionCalls 0 "No claim mutation on rejected input"
    Expect.equal recovery.StartCalls 0 "No recovery attempt on rejected input"

let private cursorCauses () =
    let core, _, _ = create ()
    let timestamp = DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)

    let listCursor =
        RecoveryCursorCodec.encode
            {
                View = RecoveryListView.Terminal
                OccurredAt = timestamp
                OperationId = operationId
            }

    let attemptCursor =
        RecoveryAttemptCursorCodec.encode
            {
                OperationId = otherId
                StartedAt = timestamp
                AttemptId = operationId
            }

    core.Recovery.List(RecoveryListView.Pending, Some listCursor, 50, none)
    |> wait
    |> refusal RecoveryRejection.ListCursorViewMismatch

    core.Recovery.Inspect(operationId, Some attemptCursor, 50, none)
    |> wait
    |> refusal RecoveryRejection.AttemptCursorOperationMismatch

let private mutationCauses () =
    let core, store, recovery = create ()

    for id, hash, expected in
        [
            Guid.Empty, digest, RecoveryRejection.OperationIdRequired
            operationId, "secret-hash", RecoveryRejection.RequestDigestInvalid
        ] do
        match core.Recovery.Resolve(id, hash, none) |> wait with
        | ResolveOutcome.RefusedBeforeAttempt(None, reason) ->
            Expect.equal reason expected "Exact resolve input cause"
        | _ -> failtest "Invalid resolution must be refused before attempt."

        core.Recovery.ExportEnvelope(id, hash, none) |> wait |> refusal expected

    for id, hash, confirmed, expected in
        [
            Guid.Empty, digest, true, RecoveryRejection.OperationIdRequired
            operationId, "secret-hash", true, RecoveryRejection.RequestDigestInvalid
            operationId, digest, false, RecoveryRejection.DismissalConfirmationRequired
        ] do
        match core.Recovery.Dismiss(id, hash, confirmed, none) |> wait with
        | RecoveryDismissOutcome.DismissRefused(None, reason) ->
            Expect.equal reason expected "Exact dismissal input cause"
        | _ -> failtest "Invalid dismissal must be refused."

    Expect.equal store.TransactionCalls 0 "No business effect"
    Expect.equal recovery.StartCalls 0 "No attempt admission"

let private cancellationPrecedence () =
    let core, _, recovery = create ()
    use cancelled = new CancellationTokenSource()
    cancelled.Cancel()
    let token = cancelled.Token

    match core.Recovery.Resolve(Guid.Empty, "bad", token) |> wait with
    | ResolveOutcome.RefusedBeforeAttempt(None, RecoveryRejection.OperationIdRequired) -> ()
    | _ -> failtest "Resolve validates the operation before cancellation, as before."

    match core.Recovery.Resolve(operationId, "bad", token) |> wait with
    | ResolveOutcome.RefusedBeforeAttempt(None, RecoveryRejection.RequestDigestInvalid) -> ()
    | _ -> failtest "Resolve validates the digest before cancellation, as before."

    match core.Recovery.Resolve(operationId, digest, token) |> wait with
    | ResolveOutcome.ResolveCancelledBeforeAdmission actual ->
        Expect.equal actual operationId "No remint"
    | _ -> failtest "Valid cancelled resolution is not relabelled as a refusal."

    Expect.equal
        (core.Recovery.List(RecoveryListView.Pending, Some "bad", 0, token) |> wait)
        RecoveryQueryOutcome.RecoveryCancelled
        "List cancels before input validation"

    Expect.equal
        (core.Recovery.Inspect(Guid.Empty, Some "bad", 0, token) |> wait)
        RecoveryQueryOutcome.RecoveryCancelled
        "Inspect cancels before input validation"

    Expect.equal
        (core.Recovery.ExportEnvelope(Guid.Empty, "bad", token) |> wait)
        RecoveryQueryOutcome.RecoveryCancelled
        "Export cancels before input validation"

    match core.Recovery.Dismiss(Guid.Empty, "bad", false, token) |> wait with
    | RecoveryDismissOutcome.DismissCancelledBeforeAdmission actual ->
        Expect.equal actual Guid.Empty "Dismiss cancellation keeps original identity"
    | _ -> failtest "Dismiss cancels before input validation."

    Expect.equal recovery.StartCalls 0 "No cancelled call starts recovery"

let private importCauses () =
    let core, store, recovery = create ()
    let source = Encoding.UTF8.GetBytes("private-invalid-source")

    core.Recovery.PreviewEnvelopeImport(source, none)
    |> wait
    |> refusal RecoveryRejection.EnvelopeInvalidOrUnsupported

    core.Recovery.PreviewCanonicalRecordImport(source, none)
    |> wait
    |> refusal RecoveryRejection.CanonicalRecordInvalidOrUnsupported

    match core.Recovery.RetainCanonicalRecordImport(source, digest, none) |> wait with
    | RecoveryImportRetainOutcome.ImportRejected reason ->
        Expect.equal
            reason
            RecoveryRejection.SourceDigestMismatch
            "Source bytes and request digest remain distinct"
    | _ -> failtest "Mismatched source must not be retained."

    Expect.equal store.TransactionCalls 0 "Import does not execute a case"
    Expect.equal recovery.StartCalls 0 "Import does not start recovery"

let private providerPrivacy () =
    let canary = "PRIVATE-provider-path-and-content"

    let reason =
        TypedProjection.recoveryFault (RecoveryStoreFailure.InvalidInput canary)

    Expect.equal
        reason
        CoreFault.RecoveryResponseInvalid
        "Malformed store response remains an integrity fault"

    let bytes = CliWireCodec.caseGet "case.get" (QueryOutcome.Failed reason)

    Expect.isFalse
        (Encoding.UTF8.GetString(bytes.Bytes).Contains(canary))
        "Provider payload is not diagnostic content"

    Expect.equal
        (CoreConversions.fault (CoreFailure.CommitOutcomeUnknown operationId))
        CoreFault.CommitOutcomeUnknown
        "Operation remains on outer outcome, not in arguments"

let private unknownOutcome () =
    let source = new CoreStore.Store()

    let recovery =
        new CoreRecoveryStore.Store(retainFailure = RecoveryStoreFailure.TechnicalMutationUnknown)

    recovery.AttachClaimStore(source :> IClaimStore)

    let core =
        CoreApi.create (source :> IClaimStore) (recovery :> IRecoveryStore) (businessTime today)

    let command = request 0L (ClaimCore.Domain.Command.Open registration)

    match core.Execute(command, none) |> wait with
    | SubmissionOutcome.PreparationStateUnknown(actual, actualDigest, fault) as result ->
        Expect.equal actual command.OperationId "No replacement operation"
        Expect.equal actualDigest.Length 64 "Original request remains content-bound"

        Expect.equal
            fault
            CoreFault.RecoveryMutationUnknown
            "Lost confirmation is not a definite failure"

        Expect.equal fault.Action RecommendedAction.RecoverExact "Exact recovery remains required"

        Expect.equal
            (CliWireCodec.submission "command.execute" result).ExitCode
            4
            "Unknown exit retained"
    | _ -> failtest "Unknown technical commit must remain explicitly uncertain."

    Expect.equal source.TransactionCalls 0 "No case was submitted during uncertain preparation"

let tests =
    testList
        "core outcome diagnostic emission"
        [
            testCase "recovery reads emit precise ID page and cursor causes" queryCauses
            testCase "recovery cursors retain view and operation binding causes" cursorCauses
            testCase
                "resolve dismissal and export preserve specific admission causes"
                mutationCauses
            testCase
                "diagnostic specificity preserves existing cancellation precedence"
                cancellationPrecedence
            testCase "source mismatch and unsupported artifact causes remain distinct" importCauses
            testCase "provider error payloads never enter diagnostic presentation" providerPrivacy
            testCase
                "technical commit uncertainty preserves operation identity and recovery guidance"
                unknownOutcome
        ]
