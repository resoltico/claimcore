namespace ClaimCore.Application.Execution

open System
open System.Threading
open ClaimCore.Domain
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
            let! person = AggregateStorage.principal tx services.Principal

            let access =
                person
                |> Option.map ClaimCore.Domain.Aggregates.Person.access
                |> AccessView.ofActor

            return!
                ReceiptReads.read
                    (ServiceContext.basis services tx.ObservedAt)
                    services.Principal
                    access
                    tx
                    record
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
                        Some(target, ChangeEntity.Case, expected)
                    | Binding.Work(target, expected) -> Some(target, ChangeEntity.Work, expected)
                    | Binding.Reminder(target, id, expected) ->
                        Some(target, ChangeEntity.Reminder id, expected)
                    | _ -> None

                match selected with
                | None -> return Error CoreFault.StoreIntegrityError
                | Some(target, entity, expected) ->
                    let! current = CaseReadContext.load services tx target

                    return!
                        ConflictReads.refusal
                            services
                            tx
                            current.Actor
                            current.Case
                            entity
                            (Revision.value expected)
                            refusal
            | _ -> return Ok refusal
        }

    let private inTransaction
        (services: ServiceContext)
        (tx: WriteTx)
        selected
        operation
        binding
        inputs
        verified
        =
        task {
            let! decisionContext = context services tx true

            let! proposed =
                Engine.prepare tx tx.Head decisionContext selected operation binding inputs verified

            match proposed with
            | Error(ExecutionStop.Refused refusal) ->
                let! explained = explainRefusal services tx binding refusal
                return explained |> Result.map Verdict.Refuse |> Result.defaultWith Verdict.Fail
            | Error(ExecutionStop.Failed fault) -> return Verdict.Fail fault
            | Error(ExecutionStop.AlreadyAccepted record) ->
                let! receipt = replayReceipt services tx record
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
                            plan.Snapshot.Requester
                            tx
                            record

                    return receipt |> Result.map Verdict.Proceed |> Result.defaultWith Verdict.Fail
        }

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
                let! explained = explainRefusal services tx binding refusal
                return explained |> Result.map Outcome.Rejected |> Result.defaultWith Outcome.Failed
            | Error(ExecutionStop.Failed fault) -> return Outcome.Failed fault
            | Error(ExecutionStop.AlreadyAccepted record) ->
                let! receipt = replayReceipt services tx record

                return
                    receipt
                    |> Result.map (Previewed.AlreadyAccepted >> Outcome.Succeeded)
                    |> Result.defaultWith Outcome.Failed
            | Ok plan ->
                let target =
                    match plan.Snapshot.State, plan.After with
                    | _, AggregateState.Family(Some family) ->
                        ReviewTarget.Case(family.Id, family.Reference)
                    | AggregateState.Person before, AggregateState.Person _ ->
                        ReviewTarget.Person(
                            before |> Option.map (fun person -> Actor.currentReference person.Actor)
                        )
                    | _ -> invalidOp "An admitted decision has no resulting aggregate."

                let! references =
                    ChangeReferenceReads.read tx plan.Changes.Head.Aggregate plan.Descriptions

                let changesOwnSignInIdentity =
                    plan.Snapshot.Requester.Actor
                    |> Option.exists (fun actor ->
                        plan.Descriptions
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
                                OperationReview.Task = plan.Snapshot.Declaration.Id
                                Target = target
                                References = references
                                Binding = plan.Binding
                                Changes = plan.Descriptions
                                ChangesOwnSignInIdentity = changesOwnSignInIdentity
                                Context = ServiceContext.runtime services tx.ObservedAt
                            }
                    )

        }

    let private admitted (submission: OperationSubmission) =
        // Check syntax before time- and state-dependent decisions; RequestRefusals still loads the current caller.
        match OperationSubmission.syntax submission with
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

                        let! result =
                            match prepared with
                            | Error refusal -> RequestRefusals.write services ct refusal
                            | Ok(selected, operation, input, verified) ->
                                services.Store.Write
                                    {
                                        CancellationToken = ct
                                        Work =
                                            fun tx ->
                                                inTransaction
                                                    services
                                                    tx
                                                    selected
                                                    operation
                                                    input.Binding
                                                    input.Inputs
                                                    verified
                                    }

                        return
                            result
                            |> Result.map mutationResult
                            |> Result.defaultWith mutationFailure
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

                            return QueryResults.readResult result
                }

            return
                {
                    PreviewReply.OperationId = submission.OperationId
                    Outcome = outcome
                }
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
                        None
        }

    let internal provisionOwner services operationId profile roles token =
        services.Store.Write
            {
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
            None
