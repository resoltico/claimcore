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
            let! access = ExecutionEnvironment.requester tx services.Principal

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
                        DisclosureKind.OperationPreview
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
                    | Ok(Refusal.RevisionConflict conflict), Some record when
                        conflict.Changes.IsSome || conflict.ReadCaseId.IsSome
                        ->
                        let result, event =
                            DisclosureReads.capturedCaseRead
                                services.Principal
                                current.Actor
                                DisclosureKind.RevisionConflict
                                record.CaseId
                                None
                                explained

                        let event =
                            match conflict.Changes with
                            | Some(ConflictChanges.Page page) ->
                                event
                                |> Option.map (fun event ->
                                    { event with
                                        Page = Some(DisclosureReads.rows page.Entries)
                                    })
                            | _ -> event

                        return result, event
                    | _ -> return explained, None
            | _ -> return Ok refusal, None
        }

    let private decide services (tx: WriteTx) selected operation binding inputs verified =
        task {
            let! decisionContext = context services tx true

            match!
                Engine.prepare tx tx.Head decisionContext selected operation binding inputs verified
            with
            | Ok(PreparedDecision.Opening(frame, facts)) ->
                return! Engine.acceptOpening tx frame facts
            | Ok(PreparedDecision.Ordinary(plan, _)) -> return Ok plan
            | Error stop -> return Error stop
        }

    let private inTransaction
        (services: ServiceContext)
        (tx: WriteTx)
        selected
        operation
        binding
        inputs
        =
        task {
            let! proposed = decide services tx selected operation binding inputs None

            match proposed with
            | Error(ExecutionStop.Refused refusal) -> return Verdict.Refuse refusal
            | Error(ExecutionStop.Failed fault) -> return Verdict.Fail fault
            | Error(ExecutionStop.AlreadyAccepted record) ->
                let! receipt, _ = replayReceipt services tx record
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
                                Target = ReviewTarget.Opening
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
                    match plan.Changes.Head.Aggregate with
                    | AggregateReference.Family caseId ->
                        let _, evidence =
                            DisclosureReads.capturedCaseRead
                                services.Principal
                                plan.Frame.Requester
                                DisclosureKind.OperationPreview
                                caseId
                                (Some id)
                                (Ok())

                        return Outcome.Rejected(Refusal.AttachmentDuplicate id), evidence
                    | _ -> return Outcome.Failed CoreFault.StoreIntegrityError, None
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

    let private settle (services: ServiceContext) token selected operation binding inputs verified =
        services.Store.Write
            {
                RequiresEncryption = true
                CancellationToken = token
                Work =
                    fun tx ->
                        task {
                            match!
                                decide services tx selected operation binding inputs verified
                            with
                            | Error(ExecutionStop.Refused refusal) -> return Verdict.Refuse refusal
                            | Error(ExecutionStop.Failed fault) -> return Verdict.Fail fault
                            | Error(ExecutionStop.AlreadyAccepted _) -> return Verdict.Replay()
                            | Ok plan ->
                                match! Engine.commit tx plan with
                                | Ok _ -> return Verdict.Proceed()
                                | Error fault -> return Verdict.Fail fault
                        }
            }

    let private releaseReceipt (services: ServiceContext) operation replayed token =
        task {
            let! result =
                services.Store.Read
                    {
                        CancellationToken = token
                        Work =
                            fun tx ->
                                task {
                                    let! access =
                                        ExecutionEnvironment.requester tx services.Principal

                                    let! entry =
                                        OperationStorage.find
                                            tx.Connection
                                            tx.Cipher
                                            operation
                                            tx.CancellationToken

                                    match access.Actor, entry with
                                    | Some requester, Some record when
                                        Access.validActor services.Principal access
                                        && (record.Root.Header.Authority =
                                                Authority.Actor requester.ActorId
                                            || Engine.canObserve
                                                services.Principal
                                                access
                                                record.Changes.Head.Aggregate)
                                        ->
                                        let! receipt =
                                            ReceiptReads.read
                                                (ServiceContext.basis services tx.ObservedAt)
                                                services.Principal
                                                access
                                                tx
                                                record

                                        let caseId, attachment =
                                            DisclosureReads.receiptTarget record.Changes.Head
                                            |> Option.map (fun (id, attachment) ->
                                                Some id, attachment)
                                            |> Option.defaultValue (None, None)

                                        return
                                            DisclosureReads.observe
                                                services.Principal
                                                access
                                                DisclosureKind.OperationReceipt
                                                caseId
                                                attachment
                                                (Outcome.ofResult receipt)
                                    | _ ->
                                        return
                                            QueryResults.unavailable,
                                            DisclosureReads.refused
                                                services.Principal
                                                access
                                                DisclosureKind.OperationReceipt
                                }
                    }

            let! released = DisclosureReads.complete services token result
            return DisclosureReads.executeReceipt replayed released
        }

    let private releaseRefusal (services: ServiceContext) observe token =
        task {
            let! result =
                services.Store.Read
                    {
                        CancellationToken = token
                        Work = observe
                    }

            let! released = DisclosureReads.complete services token result
            return DisclosureReads.executeRefusal released
        }

    let private observeConflict services binding refusal tx =
        task {
            let! explained, disclosure = explainRefusal services tx binding refusal

            return
                explained |> Result.map Outcome.Rejected |> Result.defaultWith Outcome.Failed,
                disclosure
        }

    let private observeDuplicate services selected operation binding inputs verified id tx =
        task {
            let! decisionContext = context services tx true
            let! head = OperationStorage.position tx.Connection tx.CancellationToken

            let! decision =
                Engine.prepare tx head decisionContext selected operation binding inputs verified

            match binding, decision with
            | Binding.AttachmentNew target,
              Error(ExecutionStop.Refused(Refusal.AttachmentDuplicate current)) when current = id ->
                let! subject = CaseReadContext.load services tx target

                match subject.Case with
                | Some record ->
                    let _, evidence =
                        DisclosureReads.capturedCaseRead
                            services.Principal
                            subject.Actor
                            DisclosureKind.OperationReceipt
                            record.CaseId
                            (Some id)
                            (Ok())

                    return Outcome.Rejected(Refusal.AttachmentDuplicate id), evidence
                | None -> return Outcome.Failed CoreFault.StoreIntegrityError, None
            | _ ->
                let! access = ExecutionEnvironment.requester tx services.Principal

                return
                    QueryResults.unavailable,
                    DisclosureReads.refused
                        services.Principal
                        access
                        DisclosureKind.OperationReceipt
        }

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
                        return
                            DisclosureReads.safeExecute (
                                ExecuteResult.Failed CoreFault.StoreUnavailable
                            )
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

                        match prepared with
                        | Error refusal ->
                            match! RequestRefusals.write services ct refusal with
                            | Ok(Verdict.Refuse refusal) ->
                                return DisclosureReads.safeExecute (ExecuteResult.Rejected refusal)
                            | Ok(Verdict.Fail fault) ->
                                return DisclosureReads.safeExecute (ExecuteResult.Failed fault)
                            | Ok _ ->
                                return
                                    DisclosureReads.safeExecute (
                                        ExecuteResult.Failed CoreFault.StoreIntegrityError
                                    )
                            | Error failure ->
                                return DisclosureReads.safeExecute (mutationFailure failure)
                        | Ok(selected, operation, input, verified) ->
                            match!
                                settle
                                    services
                                    ct
                                    selected
                                    operation
                                    input.Binding
                                    input.Inputs
                                    verified
                            with
                            | Ok(Verdict.Proceed()) ->
                                return! releaseReceipt services operation false ct
                            | Ok(Verdict.Replay()) ->
                                return! releaseReceipt services operation true ct
                            | Ok(Verdict.Refuse(Refusal.RevisionConflict _ as refusal)) ->
                                return!
                                    releaseRefusal
                                        services
                                        (observeConflict services input.Binding refusal)
                                        ct
                            | Ok(Verdict.Refuse(Refusal.AttachmentDuplicate id)) ->
                                return!
                                    releaseRefusal
                                        services
                                        (observeDuplicate
                                            services
                                            selected
                                            operation
                                            input.Binding
                                            input.Inputs
                                            verified
                                            id)
                                        ct
                            | Ok(Verdict.Refuse refusal) ->
                                return DisclosureReads.safeExecute (ExecuteResult.Rejected refusal)
                            | Ok(Verdict.Fail fault) ->
                                return DisclosureReads.safeExecute (ExecuteResult.Failed fault)
                            | Error failure ->
                                return DisclosureReads.safeExecute (mutationFailure failure)
                }

            return DisclosureReads.execute submission.OperationId outcome
        }

    let executeSubmission services submission ct = execute services submission None ct

    let executeAttachmentUpload services submission bytes ct =
        execute services submission (Some bytes) ct

    let private previewObservation services tx selected operation binding inputs =
        task {
            let! outcome, earlier = inSnapshot services tx selected operation binding inputs

            match earlier with
            | Some _ -> return outcome, earlier
            | None ->
                let! access = ExecutionEnvironment.requester tx services.Principal

                let subject =
                    match outcome with
                    | Outcome.Succeeded(Previewed.Reviewed { Target = ReviewTarget.Case(id, _) }) ->
                        Some id
                    | _ -> None

                return
                    DisclosureReads.observe
                        services.Principal
                        access
                        DisclosureKind.OperationPreview
                        subject
                        None
                        outcome
        }

    let previewSubmission
        (services: ServiceContext)
        (submission: OperationSubmission)
        (ct: CancellationToken)
        =
        task {
            let! outcome =
                task {
                    if ct.IsCancellationRequested then
                        return
                            DisclosureReads.safeOutcome (Outcome.Failed CoreFault.StoreUnavailable)
                    else
                        let prepared =
                            admitted submission
                            |> Result.bind (fun (selected, operation, input) ->
                                canonical selected input
                                |> Result.map (fun (input, _) -> selected, operation, input))

                        match prepared with
                        | Error refusal -> return! RequestRefusals.protectedRead services ct refusal
                        | Ok(selected, operation, input) ->
                            let! result =
                                services.Store.Read
                                    {
                                        CancellationToken = ct
                                        Work =
                                            fun tx ->
                                                previewObservation
                                                    services
                                                    tx
                                                    selected
                                                    operation
                                                    input.Binding
                                                    input.Inputs
                                    }

                            return! DisclosureReads.complete services ct result
                }

            return DisclosureReads.preview submission.OperationId outcome
        }

    let private initialOwnerInput (services: ServiceContext) (profile: ActorProfileInput) roles =
        let kind, issuer, subject = PrincipalKey.parts services.Principal

        let text input value =
            TaskInputId.Target input, InputValue.Text value

        let values =
            [
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

    let internal provisionOwnerInTransaction services tx operationId profile roles =
        task {
            match OperationId.create operationId, initialOwnerInput services profile roles with
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
        }

    let internal provisionOwner services operationId profile roles token =
        services.Store.Write
            {
                WriteOperation.RequiresEncryption = true
                CancellationToken = token
                Work = fun tx -> provisionOwnerInTransaction services tx operationId profile roles
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
