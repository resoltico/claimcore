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
type internal DecisionFrame =
    {
        Declaration: TaskDeclaration
        Requester: AccessView
        Header: OperationHeader
        Binding: Binding
        Inputs: InputMap
    }

[<NoEquality; NoComparison>]
type internal DecisionPlan =
    {
        Frame: DecisionFrame
        Before: AggregateState
        After: AggregateState
        Origins: Map<PartKind * System.Guid, SubjectStamp>
        Changes: PartChange list
        AttachmentPayload: VerifiedAttachment option
    }

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal PreparedDecision =
    | Ordinary of DecisionPlan * DescribedPart list
    | Opening of DecisionFrame * AdmittedOpening

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
            | Binding.Party(_, _, revision)
            | Binding.Reminder(_, _, revision) -> Revision.value revision = Revision.Maximum
            | Binding.Note _
            | Binding.Link _ -> true // Removal reviews the selected child's current accepted version.
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
                let! id = ClaimCore.Application.Reads.CaseIdentityReads.resolve tx target
                resolved <- Map.add (selectorKey target) id resolved

            return fun target -> Map.tryFind (selectorKey target) resolved |> Option.flatten
        }

    let private admitAttachment (tx: ReadTx) verified changes =
        task {
            match verified, changes with
            | None, _ -> return Ok(changes, None)
            | Some verified, [ change ] ->
                match change.Aggregate, change.Value with
                | AggregateReference.Family caseId,
                  PartValue.Attachment(AttachmentEffect.Added(true, Some descriptor)) ->
                    let! matched = AttachmentStorage.findMatch tx caseId verified

                    let! count, bytes =
                        match matched with
                        | Some _ -> System.Threading.Tasks.Task.FromResult(0L, 0L)
                        | None -> AttachmentStorage.usage tx caseId

                    let admit introduces blob payload =
                        let descriptor =
                            { descriptor with
                                Blob = blob
                                Preview =
                                    AttachmentPreviewProfile.forName
                                        (AttachmentIntent.name descriptor.Intent)
                                        (VerifiedAttachment.bytes verified)
                            }

                        let accepted =
                            { change with
                                Value =
                                    PartValue.Attachment(
                                        AttachmentEffect.Added(introduces, Some descriptor)
                                    )
                            }

                        Ok([ accepted ], payload)

                    return
                        match AttachmentAdmission.decide verified count bytes matched with
                        | AttachmentAdmission.Duplicate id ->
                            Error(ExecutionStop.Refused(Refusal.AttachmentDuplicate id))
                        | AttachmentAdmission.RetainedCapacityReached ->
                            Error(ExecutionStop.Refused Refusal.AttachmentCapacityReached)
                        | AttachmentAdmission.RetainNew ->
                            admit true descriptor.Blob (Some verified)
                        | AttachmentAdmission.Reattach blob -> admit false blob (Some verified)
                | _ -> return Error(ExecutionStop.Failed CoreFault.StoreIntegrityError)
            | _ -> return Error(ExecutionStop.Failed CoreFault.StoreIntegrityError)
        }

    let prepare
        (tx: ReadTx)
        (head: LedgerPosition)
        (context: DecisionContext)
        selected
        operation
        binding
        inputs
        verified
        =
        task {
            let declaration = Task.declaration selected

            if tx.ObservedAt <> context.ObservedAt then
                return Error(ExecutionStop.Failed CoreFault.StoreIntegrityError)
            elif
                declaration.Exposure = TaskExposure.Operator
                && declaration.Aggregate <> AggregateKind.Person
            then
                return Error(ExecutionStop.Failed CoreFault.InternalError)
            else
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
                        OperationStorage.find tx.Connection tx.Cipher operation tx.CancellationToken

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
                                match binding, record.Root.Header.Authority, access.Actor with
                                | Binding.CaseErase _, Authority.Actor original, Some requester when
                                    original <> requester.ActorId
                                    ->
                                    return
                                        Error(
                                            ExecutionStop.Refused(
                                                Refusal.Access AccessRefusal.ResourceUnavailable
                                            )
                                        )
                                | _ -> return Error(ExecutionStop.AlreadyAccepted record)
                            | Some _ ->
                                return Error(ExecutionStop.Refused Refusal.IdempotencyConflict)
                    | None when Sequence.value head.Sequence >= Sequence.Maximum ->
                        return Error(ExecutionStop.Failed CoreFault.InternalError)
                    | None ->
                        let! resolve = resolver tx binding inputs None
                        let resolvedBinding = OperationIntent.resolveBinding resolve binding

                        let authority =
                            match declaration.Exposure with
                            | TaskExposure.Operator -> Some Authority.Operator
                            | TaskExposure.Client ->
                                access.Actor |> Option.map (_.ActorId >> Authority.Actor)

                        match authority with
                        | None ->
                            return
                                Error(
                                    ExecutionStop.Refused(
                                        Refusal.Access AccessRefusal.ResourceUnavailable
                                    )
                                )
                        | Some authority ->
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
                                    Authority = authority
                                    RecordedAt = tx.ObservedAt
                                    BusinessDate = businessDate
                                }

                            let frame =
                                {
                                    Declaration = declaration
                                    Requester = access
                                    Header = header
                                    Binding = resolvedBinding
                                    Inputs = inputs
                                }

                            let decision =
                                {
                                    TaskContext.Decision =
                                        { context with
                                            BusinessDate = businessDate
                                            Access = access
                                        }
                                    Stamp =
                                        {
                                            OperationId = operation
                                            Authority = authority
                                            RecordedAt = tx.ObservedAt
                                            Position =
                                                EffectPosition.create header.Sequence 0
                                                |> Option.get
                                        }
                                }

                            match selected with
                            | Task.Opening opening ->
                                let! acquired =
                                    ExecutionEnvironment.opening
                                        tx
                                        context.Principal
                                        access
                                        context.Maintenance
                                        opening
                                        resolvedBinding
                                        inputs
                                        operation

                                return
                                    acquired
                                    |> Result.mapError ExecutionStop.Refused
                                    |> Result.bind (fun (environment, state) ->
                                        opening.Admit
                                            decision
                                            environment
                                            state
                                            resolvedBinding
                                            inputs
                                        |> Result.mapError (rejection binding))
                                    |> Result.map (fun facts ->
                                        PreparedDecision.Opening(frame, facts))
                            | _ ->
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

                                let! decisionBinding =
                                    task {
                                        match resolvedBinding, access.Actor, snapshot.State with
                                        | Binding.CaseErase(CaseSelector.Id caseId,
                                                            revision,
                                                            ErasureReview.Token token),
                                          Some requester,
                                          AggregateState.Family(Some family) when
                                            family.Id = caseId
                                            && (snapshot.Availability resolvedBinding)
                                                .Blocked.IsNone
                                            ->
                                            let! reviewed =
                                                CaseFamilyAcquisition.admitErasureReview
                                                    tx
                                                    caseId
                                                    requester.ActorId
                                                    token

                                            return
                                                match reviewed with
                                                | Some sequence ->
                                                    Binding.CaseErase(
                                                        CaseSelector.Id caseId,
                                                        revision,
                                                        ErasureReview.Accepted(
                                                            sequence,
                                                            OperationIntent.reviewCommitment token
                                                        )
                                                    )
                                                | None -> resolvedBinding
                                        | _ -> return resolvedBinding
                                    }

                                match snapshot.Decide decision decisionBinding inputs with
                                | Error rejected -> return Error(rejection binding rejected)
                                | Ok changes ->
                                    match (snapshot.Availability resolvedBinding).Blocked with
                                    | Some refusal -> return Error(ExecutionStop.Refused refusal)
                                    | None ->
                                        let! admitted = admitAttachment tx verified changes

                                        match admitted with
                                        | Error stop -> return Error stop
                                        | Ok(changes, payload) ->
                                            match
                                                DecisionSnapshot.apply snapshot changes,
                                                snapshot.Describe changes
                                            with
                                            | Ok after, Ok descriptions ->
                                                let plan =
                                                    {
                                                        Frame =
                                                            { frame with Binding = decisionBinding }
                                                        Before = snapshot.State
                                                        After = after
                                                        Origins = snapshot.Origins
                                                        Changes = changes
                                                        AttachmentPayload = payload
                                                    }

                                                return
                                                    Ok(
                                                        PreparedDecision.Ordinary(
                                                            plan,
                                                            descriptions
                                                        )
                                                    )
                                            | _ ->
                                                return
                                                    Error(
                                                        ExecutionStop.Failed
                                                            CoreFault.StoreIntegrityError
                                                    )
        }

    let acceptOpening (tx: WriteTx) frame (opening: AdmittedOpening) =
        let owner = ClaimCore.Application.Reads.CaseIdentityReads.referenceOwner tx

        let rec draw remaining =
            task {
                if remaining = 0 then
                    return Error(ExecutionStop.Failed CoreFault.InternalError)
                else
                    let candidate = tx.DrawCaseReference()
                    let! reserved = owner candidate

                    match reserved with
                    | Some _ -> return! draw (remaining - 1)
                    | None -> return Ok(candidate, reserved)
            }

        task {
            let! reserved = draw 8

            return
                reserved
                |> Result.bind (fun (candidate, owner) ->
                    Registry.openCase.Accept candidate owner opening
                    |> Result.mapError (rejection frame.Binding))
                |> Result.bind (fun changes ->
                    DecisionFacts.apply None changes
                    |> Result.mapError (fun _ ->
                        ExecutionStop.Failed CoreFault.StoreIntegrityError)
                    |> Result.map (fun after ->
                        {
                            Frame = frame
                            Before = AggregateState.Family None
                            After = AggregateState.Family(Some after)
                            Origins = Map.empty
                            Changes = changes
                            AttachmentPayload = None
                        }))
        }

    let commit (tx: WriteTx) plan =
        task {
            if
                plan.Frame.Header.PreviousHash <> tx.Head.EntryHash
                || plan.Frame.Header.Sequence <> Sequence.successor tx.Head.Sequence
                || plan.Frame.Header.RecordedAt <> tx.ObservedAt
            then
                return Error CoreFault.StoreIntegrityError
            else
                match
                    RetainedOperation.create
                        plan.Frame.Header
                        plan.Frame.Declaration.Id
                        plan.Frame.Binding
                        plan.Frame.Inputs
                        plan.Changes
                        tx.NewPayloadNonce
                with
                | Error _ -> return Error CoreFault.StoreIntegrityError
                | Ok record ->
                    match plan.AttachmentPayload with
                    | Some payload -> do! tx.Append(record, payload)
                    | None -> do! tx.Append record

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
                                plan.Origins

                        let before =
                            match plan.Before with
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

                        let! reportDates =
                            PartProjections.previousReportDates
                                tx.Connection
                                family.Id
                                tx.CancellationToken

                        match
                            ProjectionRows.decision
                                family
                                plan.Changes
                                origins
                                (Map.tryFind family.Id summaries)
                                (CalendarDate.ofDate plan.Frame.Header.BusinessDate.Value)
                                reportDates
                        with
                        | Error _ -> return Error CoreFault.StoreIntegrityError
                        | Ok rows ->
                            do! tx.ProjectFamily rows
                            return Ok record
                    | _ -> return Error CoreFault.StoreIntegrityError
        }
