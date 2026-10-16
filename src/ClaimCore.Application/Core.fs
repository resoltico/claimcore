namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal Core =
    let private digest bytes =
        LedgerEntry.requestDigest bytes |> Convert.ToHexStringLower

    let private mutationResult =
        function
        | LedgerWriteResult.Refused error -> ExecuteOutcome.Rejected error
        | LedgerWriteResult.Failed fault -> ExecuteOutcome.Failed fault
        | LedgerWriteResult.Appended receipt -> ExecuteOutcome.Accepted receipt
        | LedgerWriteResult.Replayed receipt -> ExecuteOutcome.AlreadyAccepted receipt

    let private mutationFailure operationId bytes =
        function
        | StoreFailure.Cancelled -> ExecuteOutcome.Cancelled
        | StoreFailure.CommitOutcomeUnknown ->
            ExecuteOutcome.OutcomeUnknown(operationId, digest bytes)
        | failure -> ExecuteOutcome.Failed(Queries.fault failure)

    let private protect decide view =
        try
            decide view
        with :? ArgumentException ->
            LedgerDecision.Fail CoreFault.StoreIntegrityError

    let private caseWrite
        (store: ILedgerStore)
        installationId
        calendar
        principal
        (request: CaseRequest)
        (ct: CancellationToken)
        =
        task {
            if ct.IsCancellationRequested then
                return ExecuteOutcome.Cancelled
            else
                match Case.validateRequest request with
                | Error error -> return ExecuteOutcome.Rejected(Rejection.Case error)
                | Ok() ->
                    let decide =
                        CaseDecisions.execute installationId calendar principal request |> protect

                    let! result =
                        store.WriteCase(
                            principal,
                            request.OperationId,
                            request.CaseReference,
                            decide,
                            ct
                        )

                    return
                        result
                        |> Result.map mutationResult
                        |> Result.defaultWith (
                            mutationFailure request.OperationId (RequestRecord.encode request)
                        )
        }

    let private authorityWrite
        (store: ILedgerStore)
        installationId
        principal
        (request: AuthorityRequest)
        (ct: CancellationToken)
        =
        task {
            if ct.IsCancellationRequested then
                return ExecuteOutcome.Cancelled
            else
                match AuthorityRequests.validate request with
                | Error error -> return ExecuteOutcome.Rejected error
                | Ok() ->
                    let decide =
                        AuthorityDecisions.execute installationId principal request |> protect

                    let! result =
                        store.WriteAuthority(
                            principal,
                            request.OperationId,
                            request.Target,
                            AuthorityRequests.targetCase request,
                            decide,
                            ct
                        )

                    let bytes = AuthorityRequests.toRecord request |> AuthorityRecord.encode

                    return
                        result
                        |> Result.map mutationResult
                        |> Result.defaultWith (mutationFailure request.OperationId bytes)
        }

    let private businessFields =
        function
        | None -> Map.empty
        | Some { View = CaseView.Live(fields, _) } -> FieldDefinitions.values fields |> Map.ofList
        | Some { View = CaseView.Erased } -> Map.empty

    let private changes act before after =
        match act with
        | CaseAct.Stewardship _ -> []
        | CaseAct.Business _ ->
            let previous = businessFields before
            let next = businessFields (Some after)

            FieldDefinitions.all
            |> List.choose (fun field ->
                let before = Map.tryFind field.Name previous |> Option.flatten
                let after = Map.tryFind field.Name next |> Option.flatten

                if before = after then
                    None
                else
                    Some
                        {
                            FieldName = field.Name
                            Before = before
                            After = after
                        })

    let private advisory (calendar: IBusinessCalendar) instant act before after =
        let before = before |> Option.map Case.snapshot
        let after = Case.snapshot after

        {
            Before = before
            Proposed = after
            Changes = changes act before after
            Context =
                {
                    ProductVersion = BuildIdentity.current.Version
                    EffectiveBusinessDate = calendar.DateAt instant
                    TimeZoneId = calendar.TimeZoneId
                }
        }

    let private preview
        (store: ILedgerStore)
        installationId
        calendar
        principal
        (request: CaseRequest)
        (ct: CancellationToken)
        =
        task {
            if ct.IsCancellationRequested then
                return PreviewOutcome.Cancelled
            else
                match Case.validateRequest request with
                | Error error -> return PreviewOutcome.Rejected(Rejection.Case error)
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
                        return
                            match
                                CaseDecisions.review installationId calendar principal request view
                            with
                            | CaseReviewDecision.Refuse error -> PreviewOutcome.Rejected error
                            | CaseReviewDecision.Fail error -> PreviewOutcome.Failed error
                            | CaseReviewDecision.Replay receipt ->
                                PreviewOutcome.AlreadyAccepted receipt
                            | CaseReviewDecision.Proposed(before, after) ->
                                PreviewOutcome.Reviewed(
                                    advisory calendar view.ObservedAt request.Act before after,
                                    digest (RequestRecord.encode request)
                                )
        }

    /// Hosting owns the single ephemeral key for the runtime; every facade shares its bytes.
    let create
        installationId
        (store: ILedgerStore)
        (calendar: IBusinessCalendar)
        (cursorKey: byte array)
        principal
        : IActorClaimsCore =
        if
            installationId = Guid.Empty
            || obj.ReferenceEquals(cursorKey, null)
            || cursorKey.Length <> 32
        then
            invalidArg (nameof cursorKey) "Use an admitted installation and a 32-byte cursor key."

        { new IActorClaimsCore with
            member _.Context ct =
                Queries.context store calendar principal ct

            member _.Preview(request, ct) =
                preview store installationId calendar principal request ct

            member _.Execute(request, ct) =
                caseWrite store installationId calendar principal request ct

            member _.ExecuteAuthority(request, ct) =
                authorityWrite store installationId principal request ct

            member _.Get(reference, ct) =
                Queries.get store installationId principal reference ct

            member _.List(request, ct) =
                Queries.list store installationId cursorKey principal request ct

            member _.History(request, ct) =
                Queries.history store installationId cursorKey principal request ct

            member _.Observe(id, ct) =
                Queries.observe store installationId principal id ct

            member _.ListGrants(request, ct) =
                Queries.grants store installationId cursorKey principal request ct

            member _.ListActors(request, ct) =
                Queries.actors store installationId cursorKey principal request ct
        }
