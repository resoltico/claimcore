namespace ClaimCore.Application


open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal CaseExecution =
    [<NoComparison>]
    type TargetedRequest = private TargetedRequest of CaseOperationRequest

    let target (TargetedRequest request) = request.Target

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
            Authority.can principal actor capability (Scope.Case value.CaseId))

    let current _installationId (record: StoredCase) =
        Case.ofAccepted record.Snapshot
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun value ->
            let indexed = CaseIndex.create record.Index.ChangedAt record.Snapshot

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
        if CaseAct.kind act = CaseActKind.Open then
            if Authority.canAct principal actor Scope.Installation act then
                Ok()
            else
                Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            match record with
            | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            | Some value when Authority.canAct principal actor (Scope.Case value.CaseId) act -> Ok()
            | Some value when
                Authority.can principal actor Capability.ReadCase (Scope.Case value.CaseId)
                ->
                let required =
                    Authority.missingCapabilities principal actor (Scope.Case value.CaseId) act
                    |> List.tryHead
                    |> Option.defaultWith (fun () ->
                        invalidOp "A readable denied act has a missing capability.")

                Error(Refusal.Access(AccessRefusal.NotAuthorized required))
            | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

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

    let private propose installationId (calendar: IBusinessCalendar) request (view: CaseFacts) =
        try
            let before =
                match view.Case with
                | None -> Ok None
                | Some record -> current installationId record |> Result.map Some

            match before with
            | Error fault -> Verdict.Fail fault
            | Ok before ->
                match Case.decide (calendar.DateAt view.ObservedAt) request before with
                | Error refusal -> Verdict.Refuse(refusal)
                | Ok after -> Verdict.Proceed(before, after)
        with :? System.OverflowException ->
            Verdict.Fail CoreFault.InternalError

    let private project
        _installationId
        (calendar: IBusinessCalendar)
        (request: CaseCommand)
        (view: CaseFacts)
        after
        =
        let snapshot = Case.snapshot after
        let index = CaseIndex.create view.ObservedAt snapshot

        let id = snapshot.CaseId

        let projection =
            match snapshot.State, view.Case with
            | CaseState.Erased, _ -> CaseProjection.Erase(id, snapshot.Revision, view.ObservedAt)
            | _, None -> CaseProjection.Insert(id, snapshot, index)
            | _ -> CaseProjection.Update(id, snapshot, index)

        {
            OperationId = request.OperationId
            ObservedAt = view.ObservedAt
            ActorId = LedgerEnvelope.actorId view.Actor
            Subject =
                EntryTarget.Case(
                    id,
                    snapshot.Revision,
                    CaseAct.kind request.Act,
                    calendar.DateAt view.ObservedAt
                )
            Snapshot = ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot
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

    let review (services: ServiceContext) (command: CaseCommand) (view: CaseFacts) =
        let id =
            view.Case
            |> Option.map _.CaseId
            |> Option.defaultValue (CaseId.ofOperationId command.OperationId)

        let identity = RequestRecord.identity id command

        authorizeIdentity services command identity view
        |> Verdict.bind (fun () ->
            if view.Maintenance.Active then
                Verdict.Refuse Refusal.RegisterMaintenance
            else
                propose services.InstallationId services.Calendar command view)

    let execute
        (services: ServiceContext)
        (command: CaseCommand)
        (write: LedgerWriteContext)
        (view: CaseFacts)
        =
        let installationId = services.InstallationId
        let calendar = services.Calendar

        let id =
            view.Case
            |> Option.map _.CaseId
            |> Option.defaultValue (CaseId.ofOperationId command.OperationId)

        let identity = RequestRecord.identity id command

        authorizeIdentity services command identity view
        |> Verdict.bind (fun () -> LedgerEnvelope.lifetime view.ObservedAt command.OperationId)
        |> Verdict.bind (fun () ->
            if view.Maintenance.Active then
                Verdict.Refuse Refusal.RegisterMaintenance
            else
                propose services.InstallationId services.Calendar command view)
        |> Verdict.bind (fun (_, after) ->
            let plan = project installationId calendar command view after

            LedgerEnvelope.accept services view.Actor write plan (RequestRecord.encode command))

    let run (services: ServiceContext) (request: TargetedRequest) token =
        let store = services.Store

        store.Write(
            {
                WriteOperation.Work =
                    (fun tx ->
                        task {
                            let! view =
                                ExecutionFacts.case
                                    services
                                    tx
                                    (operationId request)
                                    (target request)

                            let admitted =
                                match
                                    operationId request
                                    |> Option.bind (
                                        ExecutionFacts.lifetime
                                            services
                                            view.Actor
                                            view.ExistingOperation
                                            view.ObservedAt
                                    )
                                with
                                | Some refusal -> Error refusal
                                | None -> complete services request view

                            match admitted with
                            | Error refusal ->
                                return!
                                    ConflictReads.verdict
                                        services
                                        tx
                                        view.Actor
                                        view.Case
                                        ChangeEntity.Case
                                        (expected request)
                                        (Verdict.Refuse refusal)
                            | Ok command ->
                                let verdict =
                                    execute
                                        services
                                        command
                                        {
                                            Head = tx.Head
                                            Nonce = tx.NewPayloadNonce()
                                        }
                                        view

                                let! verdict =
                                    ConflictReads.verdict
                                        services
                                        tx
                                        view.Actor
                                        view.Case
                                        ChangeEntity.Case
                                        (Revision.value command.ExpectedRevision)
                                        verdict

                                return! ExecutionFacts.persist tx tx.ProjectCase verdict
                        })
                CancellationToken = token
            }
        )
