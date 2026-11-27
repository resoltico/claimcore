namespace ClaimCore.Application.Execution

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Inputs
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Reads
open ClaimCore.Application.Storage

/// Delivery wrappers bind the shared engine to one authorized store snapshot or write transaction.
module internal OperationService =
    let private mutationResult =
        function
        | Verdict.Refuse error -> ExecuteResult.Rejected error
        | Verdict.Fail fault -> ExecuteResult.Failed fault
        | Verdict.Proceed receipt -> ExecuteResult.Accepted receipt
        | Verdict.Replay receipt -> ExecuteResult.AlreadyAccepted receipt

    let private mutationFailure =
        function
        | StoreFailure.Cancelled -> ExecuteResult.Failed CoreFault.StoreUnavailable
        | StoreFailure.CommitOutcomeUnknown -> ExecuteResult.OutcomeUnknown
        | failure -> ExecuteResult.Failed(QueryResults.fault failure)

    let private context (services: ServiceContext) (tx: ReadTx) commit =
        task {
            let! maintenance = RegisterReads.maintenance tx ()
            let! frontier = RegisterReads.expiredBefore tx ()

            return
                {
                    DecisionContext.BusinessDate = Some(services.Calendar.DateAt tx.ObservedAt)
                    ObservedAt = tx.ObservedAt
                    Principal = services.Principal
                    Access = AccessView.empty
                    Maintenance = maintenance.Active
                    Lifetime =
                        if commit then
                            DecisionLifetime.Execution frontier
                        else
                            DecisionLifetime.Preview frontier
                }
        }

    let private replayReceipt (services: ServiceContext) (tx: ReadTx) record =
        task {
            let! person = PersonAcquisition.principal tx services.Principal

            let access =
                person
                |> Option.map ClaimCore.Domain.Aggregates.Person.access
                |> AccessView.ofActor

            let! receipt =
                ReceiptReads.read
                    (ServiceContext.basis services tx.ObservedAt)
                    services.Principal
                    access
                    tx
                    record

            return
                match DisclosureReads.receiptTarget record.Changes.Head with
                | Some(caseId, attachment) ->
                    DisclosureReads.capturedCaseRead
                        services.Principal
                        access
                        caseId
                        attachment
                        receipt
                | None -> receipt, None
        }

    let private explainRefusal services (tx: ReadTx) binding refusal =
        task {
            match refusal with
            | Refusal.RevisionConflict _ ->
                let selected =
                    match binding with
                    | Binding.Case(target, expected)
                    | Binding.CasePayment(target, expected, _)
                    | Binding.CaseErase(target, expected, _) ->
                        Some(target, Some ChangeEntity.Case, expected)
                    | Binding.Work(target, expected) ->
                        Some(target, Some ChangeEntity.Work, expected)
                    | Binding.Reminder(target, id, expected) ->
                        Some(target, Some(ChangeEntity.Reminder id), expected)
                    | Binding.Party(target, _, expected) -> Some(target, None, expected)
                    | _ -> None

                match selected with
                | None -> return Error CoreFault.StoreIntegrityError, None
                | Some(target, entity, expected) ->
                    let! current = CaseReadContext.load services tx target

                    let! explained =
                        ConflictReads.refusal
                            services
                            tx
                            current.Actor
                            current.Case
                            entity
                            (Revision.value expected)
                            refusal

                    match explained, current.Case with
                    | Ok(Refusal.RevisionConflict conflict), Some record ->
                        match conflict.Changes with
                        | Some(ConflictChanges.Page _) ->
                            return
                                DisclosureReads.capturedCaseRead
                                    services.Principal
                                    current.Actor
                                    record.CaseId
                                    None
                                    explained
                        | _ -> return explained, None
                    | _ -> return explained, None
            | _ -> return Ok refusal, None
        }

    let private inTransaction
        (services: ServiceContext)
        (tx: WriteTx)
        selected
        operation
        binding
        inputs
        verified
        captureDisclosure
        =
        task {
            let! decisionContext = context services tx true

            let! proposed =
                Engine.prepare tx tx.Head decisionContext selected operation binding inputs verified

            let! proposed =
                match proposed with
                | Ok(PreparedDecision.Opening(frame, facts)) -> Engine.acceptOpening tx frame facts
                | Ok(PreparedDecision.Ordinary(plan, _)) ->
                    System.Threading.Tasks.Task.FromResult(Ok plan)
                | Error stop -> System.Threading.Tasks.Task.FromResult(Error stop)

            match proposed with
            | Error(ExecutionStop.Refused refusal) ->
                let! explained, disclosure = explainRefusal services tx binding refusal
                captureDisclosure disclosure
                return explained |> Result.map Verdict.Refuse |> Result.defaultWith Verdict.Fail
            | Error(ExecutionStop.Failed fault) -> return Verdict.Fail fault
            | Error(ExecutionStop.AlreadyAccepted record) ->
                let! receipt, disclosure = replayReceipt services tx record
                captureDisclosure disclosure
                return receipt |> Result.map Verdict.Replay |> Result.defaultWith Verdict.Fail
            | Ok plan ->
                let! accepted = Engine.commit tx plan

                match accepted with
                | Error fault -> return Verdict.Fail fault
                | Ok record ->
                    let! receipt =
                        ReceiptReads.read
                            (ServiceContext.basis services tx.ObservedAt)
                            services.Principal
                            plan.Frame.Requester
                            tx
                            record

                    return receipt |> Result.map Verdict.Proceed |> Result.defaultWith Verdict.Fail
        }

    let private duplicateReview tx (plan: DecisionPlan) =
        match plan.Changes with
        | [ {
                Aggregate = AggregateReference.Family caseId
                Value = PartValue.Attachment(AttachmentEffect.Added(true, Some descriptor))
            } ] -> AttachmentStorage.findMetadataMatch tx caseId descriptor.Intent
        | _ -> System.Threading.Tasks.Task.FromResult None

    let private inSnapshot
        (services: ServiceContext)
        (tx: ReadTx)
        selected
        operation
        binding
        inputs
        =
        task {
            let! decisionContext = context services tx false
            let! head = OperationStorage.position tx.Connection tx.CancellationToken

            let! proposed =
                Engine.prepare tx head decisionContext selected operation binding inputs None

            match proposed with
            | Error(ExecutionStop.Refused refusal) ->
                let! explained, disclosure = explainRefusal services tx binding refusal

                return
                    (explained |> Result.map Outcome.Rejected |> Result.defaultWith Outcome.Failed),
                    disclosure
            | Error(ExecutionStop.Failed fault) -> return Outcome.Failed fault, None
            | Error(ExecutionStop.AlreadyAccepted record) ->
                let! receipt, disclosure = replayReceipt services tx record

                return
                    (receipt
                     |> Result.map (Previewed.AlreadyAccepted >> Outcome.Succeeded)
                     |> Result.defaultWith Outcome.Failed),
                    disclosure
            | Ok(PreparedDecision.Opening(frame, facts)) ->
                return
                    Outcome.Succeeded(
                        Previewed.Reviewed
                            {
                                OperationReview.Task = frame.Declaration.Id
                                Target = ReviewTarget.Opening(OpeningIntent.requested facts.Intent)
                                References = { Actors = []; Cases = [] }
                                Binding = frame.Binding
                                Changes = []
                                Opening = Some(Registry.openCase.Preview facts)
                                ChangesOwnSignInIdentity = false
                                Context = ServiceContext.runtime services tx.ObservedAt
                            }
                    ),
                    None
            | Ok(PreparedDecision.Ordinary(plan, descriptions)) ->
                let! duplicate = duplicateReview tx plan

                match duplicate with
                | Some(RetainedAttachmentMetadataMatch id) ->
                    return Outcome.Rejected(Refusal.AttachmentDuplicate id), None
                | None ->
                    let target =
                        match plan.Before, plan.After with
                        | _, AggregateState.Family(Some family) ->
                            ReviewTarget.Case(family.Id, family.Reference)
                        | AggregateState.Person before, AggregateState.Person _ ->
                            ReviewTarget.Person(
                                before
                                |> Option.map (fun person -> Actor.currentReference person.Actor)
                            )
                        | _ -> invalidOp "An admitted decision has no resulting aggregate."

                    let! references =
                        ChangeReferenceReads.read tx plan.Changes.Head.Aggregate descriptions

                    let changesOwnSignInIdentity =
                        plan.Frame.Requester.Actor
                        |> Option.exists (fun actor ->
                            descriptions
                            |> List.exists (fun part ->
                                match part.Description with
                                | ClaimCore.Domain.Changes.PartDescription.Person description ->
                                    part.PartId = ActorId.value actor.ActorId
                                    && ClaimCore.Domain.Changes
                                        .PersonDescription.changesSignInIdentity
                                        description
                                | _ -> false))

                    return
                        Outcome.Succeeded(
                            Previewed.Reviewed
                                {
                                    OperationReview.Task = plan.Frame.Declaration.Id
                                    Target = target
                                    References = references
                                    Binding = plan.Frame.Binding
                                    Changes = descriptions
                                    Opening = None
                                    ChangesOwnSignInIdentity = changesOwnSignInIdentity
                                    Context = ServiceContext.runtime services tx.ObservedAt
                                }
                        ),
                        None
        }

    let private admitted (submission: OperationSubmission) =
        // Check syntax before time- and state-dependent decisions; RequestRefusals still loads the current caller.
        match OperationSubmission.deliverySyntax submission with
        | Some issues -> Error(Refusal.InvalidInput issues)
        | None ->
            match
                OperationId.create submission.OperationId,
                ClaimCore.Domain.Tasks.Registry.find submission.Task,
                submission.Operation
            with
            | _, _, Error refusal -> Error refusal
            | Some operation, Some selected, Ok input when
                (ClaimCore.Domain.Tasks.Task.declaration selected).Exposure = TaskExposure.Client
                ->
                Ok(selected, operation, input)
            | None, _, _ ->
                Error(Refusal.invalidInput (InputTarget.OperationId, Violation.OperationIdRequired))
            | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

    let private uploadRequired =
        Refusal.invalidInput (InputTarget.AttachmentDigest, Violation.AttachmentPayloadRequired)

    let private canonical selected (input: SubmittedOperation) =
        match (Task.declaration selected).Execution with
        | TaskExecution.Structured -> Ok(input, None)
        | TaskExecution.AttachmentUpload ->
            AttachmentInputs.intent input.Inputs
            |> Result.map (fun intent ->
                { input with
                    Inputs = AttachmentInputs.canonicalInputs input.Inputs intent
                },
                Some intent)

    let attachmentUploadIntent submission =
        admitted submission
        |> Result.bind (fun (selected, _, input) ->
            canonical selected input
            |> Result.bind (function
                | _, Some intent -> Ok intent
                | _ -> Error uploadRequired))

    let private execute
        (services: ServiceContext)
        (submission: OperationSubmission)
        (payload: ReadOnlyMemory<byte> option)
        (ct: CancellationToken)
        =
        task {
            let! outcome =
                task {
                    if ct.IsCancellationRequested then
                        return ExecuteResult.Failed CoreFault.StoreUnavailable
                    else
                        let prepared =
                            admitted submission
                            |> Result.bind (fun (selected, operation, input) ->
                                canonical selected input
                                |> Result.bind (fun (input, intent) ->
                                    match intent, payload with
                                    | None, None -> Ok(selected, operation, input, None)
                                    | Some intent, Some payload ->
                                        VerifiedAttachment.verify intent payload
                                        |> Option.map (fun verified ->
                                            Ok(selected, operation, input, Some verified))
                                        |> Option.defaultValue (
                                            Error(
                                                Refusal.invalidInput (
                                                    InputTarget.AttachmentDigest,
                                                    Violation.AttachmentPayloadMismatch
                                                )
                                            )
                                        )
                                    | _ -> Error uploadRequired))

                        // Replay and conflict metadata accompany the verdict without changing its
                        // commit/rollback behavior. Record it only after the transaction returns.
                        let mutable disclosure = None

                        let! result =
                            match prepared with
                            | Error refusal -> RequestRefusals.write services ct refusal
                            | Ok(selected, operation, input, verified) ->
                                services.Store.Write
                                    {
                                        WriteOperation.RequiresEncryption = true
                                        CancellationToken = ct
                                        Work =
                                            fun tx ->
                                                disclosure <- None

                                                inTransaction
                                                    services
                                                    tx
                                                    selected
                                                    operation
                                                    input.Binding
                                                    input.Inputs
                                                    verified
                                                    (fun captured -> disclosure <- captured)
                                    }

                        match result, disclosure with
                        | Ok verdict, Some event ->
                            match! DisclosureReads.record services ct event with
                            | Ok() -> return mutationResult verdict
                            | Error fault ->
                                // A privacy failure after a known acceptance cannot prove non-execution.
                                match verdict with
                                | Verdict.Proceed _
                                | Verdict.Replay _ -> return ExecuteResult.OutcomeUnknown
                                | Verdict.Refuse _
                                | Verdict.Fail _ -> return ExecuteResult.Failed fault
                        | Ok verdict, None -> return mutationResult verdict
                        | Error failure, _ -> return mutationFailure failure
                }

            return
                {
                    ExecuteReply.OperationId = submission.OperationId
                    Outcome = outcome
                }
        }

    let executeSubmission services submission ct = execute services submission None ct

    let executeAttachmentUpload services submission bytes ct =
        execute services submission (Some bytes) ct

    let previewSubmission
        (services: ServiceContext)
        (submission: OperationSubmission)
        (ct: CancellationToken)
        =
        task {
            let! outcome =
                task {
                    if ct.IsCancellationRequested then
                        return Outcome.Failed CoreFault.StoreUnavailable
                    else
                        let prepared =
                            admitted submission
                            |> Result.bind (fun (selected, operation, input) ->
                                canonical selected input
                                |> Result.map (fun (input, _) -> selected, operation, input))

                        match prepared with
                        | Error refusal -> return! RequestRefusals.read services ct refusal
                        | Ok(selected, operation, input) ->
                            let! result =
                                services.Store.Read
                                    {
                                        CancellationToken = ct
                                        Work =
                                            fun tx ->
                                                inSnapshot
                                                    services
                                                    tx
                                                    selected
                                                    operation
                                                    input.Binding
                                                    input.Inputs
                                    }

                            return! DisclosureReads.complete services ct result
                }

            return
                {
                    PreviewReply.OperationId = submission.OperationId
                    Outcome = outcome
                }
        }

    let private initialOwnerInput
        (services: ServiceContext)
        (profile: ActorProfileInput)
        roles
        policy
        =
        let kind, issuer, subject = PrincipalKey.parts services.Principal

        let text input value =
            TaskInputId.Target input, InputValue.Text value

        let values =
            [
                text InputTarget.CaseReferencePolicy (CaseReferencePolicy.token policy)
                text InputTarget.PrincipalKind (Vocabulary.token PrincipalKind.vocabulary kind)
                text InputTarget.PrincipalIssuer issuer
                text InputTarget.PrincipalSubject subject
                text InputTarget.ProfileDisplayName profile.DisplayName
                match profile.Contact with
                | Some contact -> text InputTarget.ProfileContact contact
                | None -> ()
            ]
            |> Map.ofList

        let grants =
            roles
            |> List.map (fun role ->
                Map.ofList
                    [
                        text InputTarget.Role (Role.token role)
                        TaskInputId.Scope, InputValue.Text(ScopeKind.token ScopeKind.Installation)
                    ])

        InputMap.create values (Map.ofList [ TaskCollectionId.PeopleGrants, grants ])

    let internal provisionOwnerInTransaction services tx operationId profile roles policy =
        task {
            match
                OperationId.create operationId, initialOwnerInput services profile roles policy
            with
            | None, _ ->
                return
                    Verdict.Refuse(
                        Refusal.invalidInput (
                            InputTarget.OperationId,
                            Violation.OperationIdRequired
                        )
                    )
            | _, None ->
                return
                    Verdict.Refuse(
                        Refusal.invalidInput (
                            InputTarget.Grants,
                            Violation.GrantCountRange PeopleAdmission.MaximumGrants
                        )
                    )
            | Some operation, Some inputs ->
                let selected = ClaimCore.Domain.Tasks.Registry.provisionOwner

                return!
                    inTransaction
                        services
                        tx
                        selected
                        operation
                        (Binding.PeopleNew Revision.zero)
                        inputs
                        None
                        ignore
        }

    let internal provisionOwner services operationId profile roles policy token =
        services.Store.Write
            {
                WriteOperation.RequiresEncryption = true
                CancellationToken = token
                Work =
                    fun tx ->
                        provisionOwnerInTransaction services tx operationId profile roles policy
            }

    let internal recoverOwnerInTransaction (services: ServiceContext) tx operation binding name =
        let _, issuer, subject = PrincipalKey.parts services.Principal

        let values =
            [
                TaskInputId.Target InputTarget.PrincipalIssuer, InputValue.Text issuer
                TaskInputId.Target InputTarget.PrincipalSubject, InputValue.Text subject
                match name with
                | Some name ->
                    TaskInputId.Target InputTarget.ProfileDisplayName, InputValue.Text name
                | None -> ()
            ]
            |> Map.ofList

        let inputs = InputMap.create values Map.empty |> Option.get

        inTransaction
            services
            tx
            ClaimCore.Domain.Tasks.Registry.recoverOwner
            operation
            binding
            inputs
            None
            ignore
