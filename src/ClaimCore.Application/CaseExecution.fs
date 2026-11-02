namespace ClaimCore.Application


open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal CaseExecution =
    [<RequireQualifiedAccess; NoComparison>]
    type TargetedRequest =
        | ByReference of CaseCommand
        | ById of CaseOperationRequest

    let target =
        function
        | TargetedRequest.ByReference command ->
            CaseSelector.Reference(CaseReference.format command.Reference)
        | TargetedRequest.ById request -> request.Target

    let operationId =
        function
        | TargetedRequest.ByReference command -> Some command.OperationId
        | TargetedRequest.ById request -> OperationId.create request.OperationId

    let validateSelector =
        function
        | CaseSelector.Id _ -> Ok()
        | CaseSelector.Reference value ->
            CaseReference.parse value
            |> Result.map ignore
            |> Result.mapError (fun violation ->
                Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation))

    let private fromInput (request: CaseOperationRequest) (record: StoredCase option) =
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
            Case.parse
                {
                    OperationId = request.OperationId
                    CaseReference = reference
                    ExpectedRevision = request.ExpectedRevision
                    Act = request.Act
                })

    let prepare (request: CaseOperationRequest) =
        match request.Target with
        | CaseSelector.Reference _ ->
            fromInput request None |> Result.map TargetedRequest.ByReference
        | CaseSelector.Id _ -> Ok(TargetedRequest.ById request)

    let complete request record =
        match request with
        | TargetedRequest.ByReference command -> Ok command
        | TargetedRequest.ById request -> fromInput request record

    let authorize principal actor capability (record: StoredCase option) =
        record
        |> Option.exists (fun value ->
            Authority.can principal actor capability (Scope.Case value.CaseId))

    let current _installationId (record: StoredCase) =
        Case.ofAccepted record.Snapshot
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun value ->
            let indexed = CaseIndex.create record.Index.ChangedAt record.Snapshot

            if indexed = record.Index && record.Snapshot.CaseId = record.CaseId then
                Ok value
            else
                Error CoreFault.StoreIntegrityError)

    let permission principal actor (request: CaseCommand) (record: StoredCase option) =
        if CaseAct.kind request.Act = CaseActKind.Open then
            if Authority.canAct principal actor Scope.Installation request.Act then
                Ok()
            else
                Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            match record with
            | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            | Some value when Authority.canAct principal actor (Scope.Case value.CaseId) request.Act ->
                Ok()
            | Some value when
                Authority.can principal actor Capability.ReadCase (Scope.Case value.CaseId)
                ->
                Error(Refusal.Access AccessRefusal.ActNotPermitted)
            | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

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
                (LedgerEntries.receipt principal view.Actor view.Case)

    /// The same read/action snapshot supplies target eligibility; assignment never creates authority.
    let assignment (tx: IReadTx) (request: CaseCommand) (view: CaseFacts) =
        task {
            match request.Act, view.Case with
            | CaseAct.UpdateWork { Responsible = WorkValue.Set id }, Some current ->
                let! target = tx.FindActor id

                match target with
                | None -> return view
                | Some target ->
                    let! authority =
                        tx.Authority
                            {
                                Principal = target.Principal
                                CaseId = Some current.CaseId
                            }

                    return { view with Assignee = Some authority }
            | _ -> return view
        }

    let private eligibleAssignee (request: CaseCommand) (view: CaseFacts) =
        match request.Act, view.Case with
        | CaseAct.UpdateWork { Responsible = WorkValue.Set id }, Some current ->
            view.Assignee
            |> Option.exists (fun authority ->
                authority.Actor
                |> Option.exists (fun actor ->
                    actor.ActorId = id
                    && PrincipalKey.isHuman actor.Principal
                    && Authority.can
                        actor.Principal
                        authority
                        Capability.ReadCase
                        (Scope.Case current.CaseId)
                    && Authority.can
                        actor.Principal
                        authority
                        Capability.ManageCaseWork
                        (Scope.Case current.CaseId)))
        | _ -> true

    let private propose installationId (calendar: IBusinessCalendar) request (view: CaseFacts) =
        try
            let before =
                match view.Case with
                | None -> Ok None
                | Some record -> current installationId record |> Result.map Some

            match before with
            | Error fault -> Verdict.Fail fault
            | Ok _ when not (eligibleAssignee request view) ->
                Verdict.Refuse(Refusal.Case CaseRefusal.AssigneeUnavailable)
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
                Some
                    {
                        CaseId = id
                        Snapshot = snapshot
                        Index = index
                        SnapshotBytes = ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot
                    }
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
        |> Verdict.bind (fun () -> LedgerEnvelope.guard write.Fenced)
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

                            match complete request view.Case with
                            | Error refusal -> return Verdict.Refuse refusal
                            | Ok command ->
                                let! view = assignment tx command view

                                let! fenced =
                                    ExecutionFacts.fenced
                                        services
                                        tx
                                        view.Actor
                                        command.OperationId
                                        view.ExistingOperation

                                let verdict =
                                    match fenced with
                                    | Error fault -> Verdict.Fail fault
                                    | Ok fenced ->
                                        execute
                                            services
                                            command
                                            {
                                                Head = tx.Head
                                                Fenced = fenced
                                                Nonce = tx.Nonce
                                            }
                                            view

                                return! ExecutionFacts.persist tx tx.ProjectCase verdict
                        })
                CancellationToken = token
            }
        )
