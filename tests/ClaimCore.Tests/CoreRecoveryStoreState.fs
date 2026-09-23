namespace ClaimCore.Tests

open System
open ClaimCore.Application
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type internal RecoveryStoreSettings =
    {
        RetainFailure: RecoveryStoreFailure option
        StartFailure: RecoveryStoreFailure option
        DismissFailure: RecoveryStoreFailure option
        SettleFailure: RecoveryStoreFailure option
        SettleThrows: bool
        OnGet: (unit -> unit) option
        GetFailure: RecoveryStoreFailure option
        TransformGet: (RetainedPreparation -> RetainedPreparation) option
        OnStart: (unit -> unit) option
    }

[<NoEquality; NoComparison>]
type internal RecoveryStoreStateData =
    {
        Gate: obj
        Lineage: Guid
        PreparedAt: DateTimeOffset
        mutable Values: Map<Guid, RetainedPreparation>
        mutable Attempts: Map<Guid, PreparationAttempt list>
        mutable Revocations: Map<Guid, OperationRevocation>
        mutable Accepted: Map<Guid, Receipt>
        mutable ClaimStore: IClaimStore option
        mutable StartCalls: int
        mutable SettlementCalls: int
        mutable GetCalls: int
    }

module internal RecoveryStoreState =
    let create () =
        {
            Gate = obj ()
            Lineage = Guid.Parse("30000000-0000-4000-8000-000000000001")
            PreparedAt = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
            Values = Map.empty
            Attempts = Map.empty
            Revocations = Map.empty
            Accepted = Map.empty
            ClaimStore = None
            StartCalls = 0
            SettlementCalls = 0
            GetCalls = 0
        }

    let same (draft: RecoveryPreparationDraft) (retained: RetainedPreparation) =
        draft.CanonicalRequestFormat = retained.CanonicalRequestFormat
        && draft.RequestSha256 = retained.RequestSha256
        && draft.CanonicalRequest = retained.CanonicalRequest

    let materialize state (draft: RecoveryPreparationDraft) lifecycle : RetainedPreparation =
        {
            OperationId = draft.OperationId
            CanonicalRequestFormat = draft.CanonicalRequestFormat
            RequestSha256 = draft.RequestSha256
            CanonicalRequest = Array.copy draft.CanonicalRequest
            PreparedAt = state.PreparedAt
            PreparingApplicationVersion = draft.PreparingApplicationVersion
            PreparingContractFingerprint = draft.PreparingContractFingerprint
            PreparingContractKind = draft.PreparingContractKind
            Lifecycle = lifecycle
        }

    let update state (value: RetainedPreparation) =
        state.Values <- Map.add value.OperationId value state.Values
        value

    let authority =
        function
        | PreparationLifecycle.Unsubmitted
        | PreparationLifecycle.SubmissionStarted _ -> RecoveryAuthority.PendingAuthority
        | PreparationLifecycle.Dismissed _ -> RecoveryAuthority.RevokedAuthority

    let attemptsFor state operationId =
        state.Attempts |> Map.tryFind operationId |> Option.defaultValue []

    let private attemptAfter (after: RecoveryAttemptCursor option) (attempt: PreparationAttempt) =
        after
        |> Option.forall (fun cursor ->
            attempt.StartedAt < cursor.StartedAt
            || (attempt.StartedAt = cursor.StartedAt
                && attempt.AttemptId.CompareTo(cursor.AttemptId) < 0))

    let attemptPage state operationId after limit : RecoveryAttemptPage =
        let ordered =
            attemptsFor state operationId
            |> List.sortByDescending (fun attempt -> attempt.StartedAt, attempt.AttemptId)
            |> List.filter (attemptAfter after)

        let items = ordered |> List.truncate limit

        {
            Items = items
            NextAfter =
                if ordered.Length > items.Length then
                    items
                    |> List.tryLast
                    |> Option.map (fun attempt ->
                        {
                            OperationId = operationId
                            StartedAt = attempt.StartedAt
                            AttemptId = attempt.AttemptId
                        })
                else
                    None

        }

    let recordSettlement state operationId attemptId settlement =
        let updateAttempt attempt =
            if attempt.AttemptId = attemptId then
                { attempt with
                    Settlement = Some settlement
                    SettledAt = Some DateTimeOffset.UtcNow
                }
            else
                attempt

        state.Attempts <-
            state.Attempts |> Map.change operationId (Option.map (List.map updateAttempt))

    let receipt state operationId command claim =
        {
            OperationId = operationId
            Case = claim
            RecordedAt = state.PreparedAt
            RecordedBy = "test-operator"
            Replayed = false
            CommandName = Commands.name command
        }

    let pruneRetained state operationId =
        lock state.Gate (fun () -> state.Values <- Map.remove operationId state.Values)
