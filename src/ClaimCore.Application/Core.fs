namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal Core =
    let private digest bytes =
        LedgerEntry.requestDigest bytes |> Convert.ToHexStringLower

    let private caseFailure operationId bytes =
        function
        | StoreFailure.Cancelled -> ExecuteOutcome.Cancelled
        | StoreFailure.CommitOutcomeUnknown ->
            ExecuteOutcome.OutcomeUnknown(operationId, digest bytes)
        | failure -> ExecuteOutcome.Failed(Queries.fault failure)

    let private caseResult =
        function
        | CaseWriteResult.Refused error -> ExecuteOutcome.Rejected error
        | CaseWriteResult.Failed fault -> ExecuteOutcome.Failed fault
        | CaseWriteResult.Appended entry ->
            LedgerEntries.receipt (LedgerEntries.stored entry)
            |> Result.map ExecuteOutcome.Accepted
            |> Result.defaultWith ExecuteOutcome.Failed
        | CaseWriteResult.Replayed entry ->
            LedgerEntries.receipt entry
            |> Result.map ExecuteOutcome.AlreadyAccepted
            |> Result.defaultWith ExecuteOutcome.Failed

    let private protectCase decide view =
        try
            decide view
        with :? ArgumentException ->
            CaseDecision.Fail CoreFault.StoreIntegrityError

    let private writeCase
        (store: ILedgerStore)
        principal
        operationId
        reference
        bytes
        decide
        (ct: CancellationToken)
        : Task<ExecuteOutcome> =
        task {
            if ct.IsCancellationRequested then
                return ExecuteOutcome.Cancelled
            else
                let! result =
                    store.WriteCase(principal, operationId, reference, protectCase decide, ct)

                return
                    result
                    |> Result.map caseResult
                    |> Result.defaultWith (caseFailure operationId bytes)
        }

    let private reviewContext (calendar: IBusinessCalendar) instant =
        {
            ProductVersion = BuildIdentity.current.Version
            EffectiveBusinessDate = calendar.DateAt instant
            TimeZoneId = calendar.TimeZoneId
        }

    let private advisory calendar instant before proposed =
        let previous =
            before
            |> Option.map (
                Claim.view >> fun view -> FieldDefinitions.values view.Fields |> Map.ofList
            )
            |> Option.defaultValue Map.empty

        let snapshot = Claim.view proposed
        let next = FieldDefinitions.values snapshot.Fields |> Map.ofList

        let changes =
            FieldDefinitions.all
            |> List.choose (fun field ->
                let a = previous |> Map.tryFind field.Name |> Option.flatten
                let b = next |> Map.tryFind field.Name |> Option.flatten

                if a = b then
                    None
                else
                    Some
                        {
                            FieldName = field.Name
                            Before = a
                            After = b
                        })

        {
            Before = before |> Option.map Claim.view
            Proposed = snapshot
            Changes = changes
            Context = reviewContext calendar instant
            IsAdvisory = true
        }

    let private preview
        (store: ILedgerStore)
        calendar
        principal
        (request: CommandRequest)
        (ct: CancellationToken)
        : Task<PreviewOutcome> =
        task {
            if ct.IsCancellationRequested then
                return PreviewOutcome.Cancelled
            else
                match Claim.validateRequest request with
                | Error error -> return PreviewOutcome.Rejected(Rejection.Domain error)
                | Ok() ->
                    let! result =
                        store.ReadCase(
                            principal,
                            Some request.OperationId,
                            request.CaseReference,
                            ct
                        )

                    match result with
                    | Error StoreFailure.Cancelled -> return PreviewOutcome.Cancelled
                    | Error error -> return PreviewOutcome.Failed(Queries.fault error)
                    | Ok view ->
                        match CaseDecisions.review calendar principal request view with
                        | CaseReviewDecision.Refuse error -> return PreviewOutcome.Rejected error
                        | CaseReviewDecision.Fail error -> return PreviewOutcome.Failed error
                        | CaseReviewDecision.Replay entry ->
                            return
                                LedgerEntries.receipt entry
                                |> Result.map PreviewOutcome.AlreadyAccepted
                                |> Result.defaultWith PreviewOutcome.Failed
                        | CaseReviewDecision.Proposed(before, after) ->
                            return
                                PreviewOutcome.Reviewed(
                                    advisory calendar view.ObservedAt before after,
                                    digest (RequestRecord.encode request)
                                )
        }

    let private authorityResult replay (entry: StoredEntry) =
        match entry.Scope with
        | StoredScope.Authority target ->
            let position = LedgerEntries.position entry.Sequence entry.EntryHash

            if replay then
                AuthorityOutcome.AlreadyApplied(entry.OperationId, position, target)
            else
                AuthorityOutcome.Applied(entry.OperationId, position, target)
        | _ -> AuthorityOutcome.Failed CoreFault.StoreIntegrityError

    let private authority
        (store: ILedgerStore)
        installationId
        principal
        eventId
        target
        action
        (ct: CancellationToken)
        : Task<AuthorityOutcome> =
        task {
            if ct.IsCancellationRequested then
                return AuthorityOutcome.Cancelled
            elif eventId = Guid.Empty then
                return
                    AuthorityOutcome.Rejected(
                        Rejection.Domain(
                            DomainError.InvalidInput(
                                InputTarget.OperationId,
                                InputViolation.Command CommandViolation.EmptyOperationId
                            )
                        )
                    )
            else
                let reference =
                    match action with
                    | AuthorityAction.Grant(_, GrantTarget.CaseReference value, _) -> Some value
                    | _ -> None

                let valid =
                    reference |> Option.map Claim.validateReference |> Option.defaultValue (Ok())

                match valid with
                | Error error -> return AuthorityOutcome.Rejected(Rejection.Domain error)
                | Ok() ->
                    let decide view =
                        try
                            AuthorityDecisions.decide
                                installationId
                                principal
                                eventId
                                target
                                action
                                view
                        with :? ArgumentException ->
                            AuthorityDecision.Fail CoreFault.StoreIntegrityError

                    let! result =
                        store.WriteAuthority(principal, eventId, target, reference, decide, ct)

                    return
                        match result with
                        | Error StoreFailure.Cancelled -> AuthorityOutcome.Cancelled
                        | Error StoreFailure.CommitOutcomeUnknown ->
                            AuthorityOutcome.OutcomeUnknown eventId
                        | Error error -> AuthorityOutcome.Failed(Queries.fault error)
                        | Ok(AuthorityWriteResult.Refused reason) ->
                            AuthorityOutcome.Rejected reason
                        | Ok(AuthorityWriteResult.Failed fault) -> AuthorityOutcome.Failed fault
                        | Ok(AuthorityWriteResult.Appended entry) ->
                            authorityResult false (LedgerEntries.stored entry)
                        | Ok(AuthorityWriteResult.Replayed entry) -> authorityResult true entry
        }

    let private observeAuthority
        (store: ILedgerStore)
        principal
        eventId
        (ct: CancellationToken)
        : Task<AuthorityOutcome> =
        task {
            let! read = store.ReadOperation(principal, eventId, ct)

            return
                match read with
                | Error StoreFailure.Cancelled -> AuthorityOutcome.Cancelled
                | Error error -> AuthorityOutcome.Failed(Queries.fault error)
                | Ok(actor, Some entry, _) when
                    (match entry.Scope with
                     | StoredScope.Authority _ -> true
                     | _ -> false)
                    && ActorAuthorization.can
                        principal
                        actor
                        Capability.ManageAuthority
                        ResourceScope.Installation
                    ->
                    authorityResult true entry
                | _ -> AuthorityOutcome.Rejected Rejection.ResourceUnavailable
        }

    let private management store installationId principal =
        { new IAuthorityManagement with
            member _.RegisterActor(id, target, ct) =
                authority store installationId principal id target AuthorityAction.Register ct

            member _.SetEnabled(id, target, enabled, ct) =
                authority
                    store
                    installationId
                    principal
                    id
                    target
                    (AuthorityAction.Enable enabled)
                    ct

            member _.SetGrant(id, target, role, scope, active, ct) =
                authority
                    store
                    installationId
                    principal
                    id
                    target
                    (AuthorityAction.Grant(role, scope, active))
                    ct

            member _.Observe(id, ct) = observeAuthority store principal id ct
        }

    /// Only the composition root and store tests can supply storage or a calendar.
    let create
        installationId
        (store: ILedgerStore)
        (calendar: IBusinessCalendar)
        principal
        : IActorClaimsCore =

        { new IActorClaimsCore with
            member _.Definition ct =
                Queries.definition store calendar principal ct

            member _.Preview(request, ct) =
                preview store calendar principal request ct

            member _.Execute(request, ct) =
                match Claim.validateRequest request with
                | Error error -> Task.FromResult(ExecuteOutcome.Rejected(Rejection.Domain error))
                | Ok() ->
                    writeCase
                        store
                        principal
                        request.OperationId
                        request.CaseReference
                        (RequestRecord.encode request)
                        (CaseDecisions.command installationId calendar principal request)
                        ct

            member _.Steward(request, ct) =
                match Stewardship.validateRequest request with
                | Error(StewardshipError.InvalidRequest error) ->
                    Task.FromResult(ExecuteOutcome.Rejected(Rejection.Domain error))
                | Error _ -> Task.FromResult(ExecuteOutcome.Rejected Rejection.InvalidReason)
                | Ok() ->
                    writeCase
                        store
                        principal
                        request.OperationId
                        request.CaseReference
                        (StewardshipRecord.encode request)
                        (CaseDecisions.steward installationId calendar principal request)
                        ct

            member _.Get(reference, ct) =
                Queries.get store principal reference ct

            member _.List(request, ct) = Queries.list store principal request ct

            member _.History(request, ct) =
                Queries.history store principal request ct

            member _.ObserveOperation(id, ct) = Queries.observe store principal id ct
            member _.Authority = management store installationId principal
        }
