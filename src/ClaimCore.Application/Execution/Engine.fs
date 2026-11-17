namespace ClaimCore.Application.Execution

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type internal ExecutionStop =
    | Refused of Refusal
    | Failed of CoreFault
    | AlreadyAccepted of RetainedOperation

[<NoEquality; NoComparison>]
type internal DecisionPlan =
    {
        Snapshot: DecisionSnapshot
        Header: OperationHeader
        Binding: Binding
        Inputs: InputMap
        Changes: PartChange list
        Descriptions: DescribedPart list
        After: AggregateState
    }

/// One decision path for previews and commits, independent of receipt and document transport.
module internal Engine =
    let canObserve principal access aggregate =
        match aggregate with
        | AggregateReference.Family id ->
            Access.can principal access Capability.ReadHistory (Scope.Case id)
        | AggregateReference.Person _ ->
            Access.can principal access Capability.ReadAccess Scope.Installation

    /// A current terminal version cannot be advanced; an older reviewed version is still stale.
    let rejection binding rejected =
        let terminalReviewed =
            match binding with
            | Binding.Case(_, revision)
            | Binding.CasePayment(_, revision, _)
            | Binding.CaseErase(_, revision, _)
            | Binding.Work(_, revision)
            | Binding.Reminder(_, _, revision) -> Revision.value revision = Revision.Maximum
            | Binding.Note _ -> true // Removal reviews the selected note's current accepted version.
            | _ -> false

        match OperationSubmission.refusal rejected with
        | Some(Refusal.RevisionConflict conflict) when
            Revision.value conflict.ActualRevision = Revision.Maximum && terminalReviewed
            ->
            ExecutionStop.Failed CoreFault.InternalError
        | Some refusal -> ExecutionStop.Refused refusal
        | None -> ExecutionStop.Failed CoreFault.StoreIntegrityError

    let private selectors binding inputs =
        (Binding.caseTarget binding |> Option.toList)
        @ (InputSelection.grantTargets inputs
           |> List.choose (function
               | GrantTarget.CaseId id -> Some(CaseSelector.Id id)
               | GrantTarget.CaseReference reference -> Some(CaseSelector.Reference reference)
               | GrantTarget.Installation -> None))

    let private selectorKey =
        function
        | CaseSelector.Reference reference -> CaseSelector.Reference(CaseReference.key reference)
        | selector -> selector

    let private resolver tx supplied inputs accepted =
        task {
            let targets =
                selectors supplied (Some inputs)
                @ (accepted
                   |> Option.map (fun (record: RetainedOperation) ->
                       selectors record.Root.Structure.Binding record.Inputs)
                   |> Option.defaultValue [])
                |> List.distinct

            let mutable resolved = Map.empty

            for target in targets do
                let! id = AggregateStorage.resolveCase tx target
                resolved <- Map.add (selectorKey target) id resolved

            return fun target -> Map.tryFind (selectorKey target) resolved |> Option.flatten
        }

    let prepare
        (tx: ReadTx)
        (head: LedgerPosition)
        (context: DecisionContext)
        selected
        operation
        binding
        inputs
        =
        task {
            if tx.ObservedAt <> context.ObservedAt then
                return Error(ExecutionStop.Failed CoreFault.StoreIntegrityError)
            else
                let declaration = Task.declaration selected

                let! access = ExecutionEnvironment.requester tx context.Principal

                if
                    declaration.Exposure <> TaskExposure.Operator
                    && not (Access.validActor context.Principal access)
                then
                    return
                        Error(
                            ExecutionStop.Refused(Refusal.Access AccessRefusal.ResourceUnavailable)
                        )
                else
                    let! existing =
                        OperationStorage.find
                            tx.Connection
                            tx.InstallationId
                            operation
                            tx.CancellationToken

                    match existing with
                    | Some record ->
                        let! authorization =
                            ExecutionEnvironment.authorize
                                tx
                                context.Principal
                                access
                                selected
                                binding
                                inputs

                        match authorization with
                        | Error refusal -> return Error(ExecutionStop.Refused refusal)
                        | Ok() when
                            not (canObserve context.Principal access record.Changes.Head.Aggregate)
                            ->
                            return
                                Error(
                                    ExecutionStop.Refused(
                                        Refusal.Access AccessRefusal.ResourceUnavailable
                                    )
                                )
                        | Ok() ->
                            let! resolve = resolver tx binding inputs existing

                            match OperationIntent.accepted record resolve with
                            | None ->
                                return
                                    Error(
                                        ExecutionStop.Refused(
                                            Refusal.Access AccessRefusal.ResourceUnavailable
                                        )
                                    )
                            | Some accepted when
                                OperationIntent.same
                                    accepted
                                    (OperationIntent.hash declaration binding inputs resolve)
                                ->
                                return Error(ExecutionStop.AlreadyAccepted record)
                            | Some _ ->
                                return Error(ExecutionStop.Refused Refusal.IdempotencyConflict)
                    | None ->
                        let! snapshot =
                            ExecutionEnvironment.fromStorage
                                tx
                                context.Principal
                                access
                                context.Maintenance
                                selected
                                binding
                                (Some inputs)
                                (Some operation)

                        if Sequence.value head.Sequence >= Sequence.Maximum then
                            return Error(ExecutionStop.Failed CoreFault.InternalError)
                        else
                            let! resolve = resolver tx binding inputs None

                            let actor =
                                snapshot.Requester.Actor
                                |> Option.map _.ActorId
                                |> Option.orElseWith (fun () ->
                                    if declaration.Exposure = TaskExposure.Operator then
                                        Some(ActorId.ofOperationId operation)
                                    else
                                        None)

                            match actor with
                            | None ->
                                return
                                    Error(
                                        ExecutionStop.Refused(
                                            Refusal.Access AccessRefusal.ResourceUnavailable
                                        )
                                    )
                            | Some actor ->
                                let businessDate =
                                    if declaration.Aggregate = AggregateKind.Person then
                                        None
                                    else
                                        context.BusinessDate

                                let header =
                                    {
                                        OperationHeader.InstallationId = tx.InstallationId
                                        Sequence = Sequence.successor head.Sequence
                                        PreviousHash = head.EntryHash
                                        OperationId = operation
                                        ActorId = actor
                                        RecordedAt = tx.ObservedAt
                                        BusinessDate = businessDate
                                    }

                                let decision =
                                    {
                                        TaskContext.Decision =
                                            { context with
                                                BusinessDate = businessDate
                                                Access = snapshot.Requester
                                            }
                                        Stamp =
                                            {
                                                OperationId = operation
                                                ActorId = actor
                                                RecordedAt = tx.ObservedAt
                                                Position =
                                                    EffectPosition.create header.Sequence 0
                                                    |> Option.get
                                            }
                                    }

                                match snapshot.Decide decision binding inputs with
                                | Error rejected -> return Error(rejection binding rejected)
                                | Ok changes ->
                                    match (snapshot.Availability binding).Blocked with
                                    | Some refusal -> return Error(ExecutionStop.Refused refusal)
                                    | None ->
                                        match
                                            DecisionSnapshot.apply snapshot changes,
                                            snapshot.Describe changes
                                        with
                                        | Ok after, Ok descriptions ->
                                            return
                                                Ok
                                                    {
                                                        Snapshot = snapshot
                                                        Header = header
                                                        Binding =
                                                            OperationIntent.resolveBinding
                                                                resolve
                                                                binding
                                                        Inputs = inputs
                                                        Changes = changes
                                                        Descriptions = descriptions
                                                        After = after
                                                    }
                                        | _ ->
                                            return
                                                Error(
                                                    ExecutionStop.Failed
                                                        CoreFault.StoreIntegrityError
                                                )
        }

    let commit (tx: WriteTx) plan =
        task {
            if
                plan.Header.PreviousHash <> tx.Head.EntryHash
                || plan.Header.Sequence <> Sequence.successor tx.Head.Sequence
                || plan.Header.RecordedAt <> tx.ObservedAt
            then
                return Error CoreFault.StoreIntegrityError
            else
                match
                    RetainedOperation.create
                        plan.Header
                        plan.Snapshot.Declaration.Id
                        plan.Binding
                        plan.Inputs
                        plan.Changes
                        tx.NewPayloadNonce
                with
                | Error _ -> return Error CoreFault.StoreIntegrityError
                | Ok record ->
                    do! tx.Append record

                    match plan.After with
                    | AggregateState.Person(Some person) ->
                        match ProjectionRows.person person with
                        | Error _ -> return Error CoreFault.StoreIntegrityError
                        | Ok rows ->
                            do! tx.ProjectPerson rows
                            return Ok record
                    | AggregateState.Family(Some family) ->
                        let origins =
                            plan.Changes
                            |> List.fold
                                (fun origins change ->
                                    let key = change.Kind, change.PartId

                                    if Map.containsKey key origins then
                                        origins
                                    else
                                        Map.add key change.Stamp origins)
                                plan.Snapshot.Origins

                        let before =
                            match plan.Snapshot.State with
                            | AggregateState.Family(Some family) ->
                                Map.ofList [ family.Id, family.Work |> Option.bind _.Value ]
                            | _ -> Map.empty

                        let! prior =
                            PartProjections.summary tx.Connection family.Id tx.CancellationToken

                        let summaries =
                            ProjectionRows.summaries
                                before
                                (prior
                                 |> Option.map (fun value -> Map.ofList [ family.Id, value ])
                                 |> Option.defaultValue Map.empty)
                                plan.Changes

                        match
                            ProjectionRows.decision
                                family
                                plan.Changes
                                origins
                                (Map.tryFind family.Id summaries)
                        with
                        | Error _ -> return Error CoreFault.StoreIntegrityError
                        | Ok rows ->
                            do! tx.ProjectFamily rows
                            return Ok record
                    | _ -> return Error CoreFault.StoreIntegrityError
        }
