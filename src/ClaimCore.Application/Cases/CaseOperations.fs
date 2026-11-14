namespace ClaimCore.Application


open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal CaseOperations =
    [<NoComparison>]
    type TargetedRequest = private TargetedRequest of CaseOperationRequest

    let target (TargetedRequest request) = request.Target
    let act (TargetedRequest request) = request.Act

    let operationId (TargetedRequest request) = OperationId.create request.OperationId
    let expected (TargetedRequest request) = request.ExpectedRevision

    let validateSelector =
        function
        | CaseSelector.Id _ -> Ok()
        | CaseSelector.Reference value ->
            CaseReference.parse value
            |> Result.map ignore
            |> Result.mapError (fun violation ->
                Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation))

    let private fromInput today (request: CaseOperationRequest) (record: StoredCase option) =
        let reference =
            match request.Target with
            | CaseSelector.Reference value -> Ok value
            | CaseSelector.Id id ->
                match record, request.Act with
                | Some record, _ when
                    record.CaseId = id && CaseAct.kind request.Act <> CaseActKind.Open
                    ->
                    Ok(CaseReference.format record.Snapshot.Reference)
                | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

        reference
        |> Result.bind (fun reference ->
            let input =
                {
                    OperationId = request.OperationId
                    CaseReference = reference
                    ExpectedRevision = request.ExpectedRevision
                    Act = request.Act
                }

            let prepared = Case.prepare input

            match Case.parsed prepared with
            | Ok command -> Ok command
            | Error _ ->
                // Complete commands still reach exact replay and revision checks before admission.
                // Incomplete groups need per-field admission to reveal all sibling issues.
                Case.admitPrepared today (record |> Option.map _.Snapshot) prepared)

    // Current admission needs the same business-date/state snapshot as the action.
    let prepare (request: CaseOperationRequest) = Ok(TargetedRequest request)

    let authorize principal actor capability (record: StoredCase option) =
        record
        |> Option.exists (fun value ->
            Access.can principal actor capability (Scope.Case value.CaseId))

    let current _installationId (record: StoredCase) =
        Case.ofAccepted record.Snapshot
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun value ->
            let indexed = CaseIndex.create record.Index.ChangedAt record.Snapshot None []

            if
                { indexed with
                    Responsible = record.Index.Responsible
                    FollowUp = record.Index.FollowUp
                    WorkSearchText = record.Index.WorkSearchText
                }
                    =
                    record.Index
                && record.Snapshot.CaseId = record.CaseId
            then
                Ok value
            else
                Error CoreFault.StoreIntegrityError)

    let permissionFor principal actor act (record: StoredCase option) =
        CaseSubject.authorize principal actor (record |> Option.map _.CaseId) act

    let permission principal actor (request: CaseCommand) record =
        permissionFor principal actor request.Act record

    let complete (services: ServiceContext) (TargetedRequest request) (view: CaseFacts) =
        permissionFor services.Principal view.Actor request.Act view.Case
        |> Result.bind (fun () ->
            fromInput (services.Calendar.DateAt view.ObservedAt) request view.Case)

    let private authorizeIdentity (services: ServiceContext) request bytes (view: CaseFacts) =
        let principal = services.Principal

        match permission principal view.Actor request view.Case with
        | Error refusal -> Verdict.Refuse refusal
        | Ok() ->
            OperationIdentity.decide
                services
                view.Actor
                {
                    OperationId = request.OperationId
                    Bytes = bytes
                    Existing = view.ExistingOperation
                }
                (LedgerEntries.receiptOperation
                    (ServiceContext.basis services view.ObservedAt)
                    principal
                    view.Actor
                    (view.Actor.Actor |> Option.map Actor.currentReference)
                    (ReceiptCurrent.Case view.Case))

    let private propose installationId context request (view: CaseFacts) =
        try
            let before =
                match view.Case with
                | None -> Ok None
                | Some record ->
                    match view.State with
                    | Some state when
                        state.CaseId = record.CaseId
                        && state.Snapshot = Some record.Snapshot
                        && view.Work.IsSome
                        && CaseIndex.create
                            record.Index.ChangedAt
                            record.Snapshot
                            (view.Work |> Option.bind _.Snapshot)
                            view.PendingReminderDue
                            =
                            record.Index
                        ->
                        current installationId record |> Result.map Some
                    | _ -> Error CoreFault.StoreIntegrityError

            match before with
            | Error fault -> Verdict.Fail fault
            | Ok before ->
                match CaseSubject.decide context view.State request with
                | Error refusal -> Verdict.Refuse refusal
                | Ok after when after.After.State = CaseState.Erased ->
                    match view.Family with
                    | Some family when CaseFamily.validate after.After.CaseId family |> Result.isOk ->
                        Verdict.Proceed(before, after)
                    | _ -> Verdict.Fail CoreFault.StoreIntegrityError
                | Ok after -> Verdict.Proceed(before, after)
        with
        | SubjectStateInvalid _ -> Verdict.Fail CoreFault.StoreIntegrityError
        | :? System.OverflowException -> Verdict.Fail CoreFault.InternalError

    let private project
        _installationId
        (_calendar: IBusinessCalendar)
        (request: CaseCommand)
        (view: CaseFacts)
        (after: CaseDecision)
        =
        let snapshot = after.After

        let index =
            CaseIndex.create
                view.ObservedAt
                snapshot
                (view.Work |> Option.bind _.Snapshot)
                view.PendingReminderDue

        let id = snapshot.CaseId

        let projection operation =
            let stamp = LedgerEnvelope.stamp operation
            let state = CaseSubject.apply stamp view.State after |> Option.get
            let owned = CaseSubject.rows state |> Option.get

            let beforePayments =
                view.State |> Option.map _.Payments |> Option.defaultValue Map.empty

            let rows =
                { owned with
                    Payments =
                        owned.Payments
                        |> List.filter (fun row ->
                            Map.tryFind row.PaymentId beforePayments <> Some row)
                }

            let complete value =
                value
                |> Result.defaultWith (fun _ ->
                    invalidOp "A case-family consequence has complete owned rows.")
                |> Some

            let family =
                match snapshot.State, view.Case with
                | CaseState.Erased, _ ->
                    CaseFamily.onCaseErased state view.Family.Value
                    |> Result.bind CaseFamily.rows
                    |> complete
                | _, None -> CaseFamily.onCaseCreated id |> CaseFamily.rows |> complete
                | _ -> None

            {
                CaseProjection.Rows = rows
                Index = index
                Family = family
            }

        {
            OperationId = request.OperationId
            ObservedAt = view.ObservedAt
            ActorId = LedgerEnvelope.actorId view.Actor
            RecordedBy = view.Actor.Actor |> Option.map Actor.currentReference
            Command =
                CommandSkeleton.Case
                    {
                        CaseId = id
                        Reference = snapshot.Reference
                        ExpectedRevision = request.ExpectedRevision
                        Act = CaseAct.kind request.Act
                        SelectedPayment =
                            match request.Act with
                            | CaseAct.RemovePayment selected -> Some selected
                            | _ -> None
                    }
            Values = OperationValues.Case { Command = request; Decision = after }
            Current =
                ReceiptCurrent.Case(
                    Some
                        {
                            CaseId = id
                            Snapshot = snapshot
                            Index = index
                            SnapshotBytes =
                                ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot
                        }
                )
            Projection = projection
        }

    let decide context (services: ServiceContext) (command: CaseCommand) (view: CaseFacts) =
        let id =
            view.Case
            |> Option.map _.CaseId
            |> Option.defaultValue (CaseId.ofOperationId command.OperationId)

        let scope =
            {
                CaseCommandSkeleton.CaseId = id
                Reference = command.Reference
                ExpectedRevision = command.ExpectedRevision
                Act = CaseAct.kind command.Act
                SelectedPayment =
                    match command.Act with
                    | CaseAct.RemovePayment id -> Some id
                    | _ -> None
            }

        let identity = OperationIntent.case command.OperationId scope command

        authorizeIdentity services command identity view
        |> Verdict.bind (fun () -> propose services.InstallationId context command view)

    let append (services: ServiceContext) command view (_, after) =
        project services.InstallationId services.Calendar command view after

    let registration (services: ServiceContext) =
        {
            Load =
                fun tx request ->
                    ExecutionFacts.case
                        services
                        tx
                        (operationId request)
                        (target request)
                        (Some(act request))
            Context =
                fun mode view ->
                    SubjectExecution.context
                        services
                        mode
                        view.ObservedAt
                        view.Actor
                        view.Maintenance
                        view.ExpiredBefore
            Decide =
                fun _ context tx request view ->
                    task {
                        match complete services request view with
                        | Error refusal ->
                            let! result =
                                ConflictReads.refusal
                                    services
                                    tx
                                    view.Actor
                                    view.Case
                                    ChangeEntity.Case
                                    (expected request)
                                    refusal

                            return
                                (result
                                 |> Result.map Verdict.Refuse
                                 |> Result.defaultWith Verdict.Fail)
                        | Ok command ->
                            let! proposed =
                                ConflictReads.verdict
                                    services
                                    tx
                                    view.Actor
                                    view.Case
                                    ChangeEntity.Case
                                    (Revision.value command.ExpectedRevision)
                                    (decide context services command view)

                            return Verdict.map (fun decision -> command, decision) proposed
                    }
            Append = fun _ view (command, decision) -> append services command view decision
            Project = fun tx rows -> tx.ProjectCase rows
            Review =
                fun _ view (_, (before, after)) ->
                    let before = before |> Option.map Case.snapshot

                    OperationReview.Case
                        {
                            Before = before
                            Proposed = after.After
                            Changes = CaseChanges.review before after
                            Context = ServiceContext.runtime services view.ObservedAt
                        }
        }
