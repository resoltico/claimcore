namespace ClaimCore.Application


open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal CaseExecution =
    let authorize principal actor capability (record: StoredCase option) =
        record
        |> Option.exists (fun value ->
            Authority.can principal actor capability (Scope.Case value.CaseId))

    let current installationId (record: StoredCase) =
        Case.ofAccepted record.Snapshot
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun value ->
            let indexed = CaseIndex.create record.Index.ChangedAt record.Snapshot

            if
                indexed = record.Index
                && CaseReferenceId.encode
                    installationId
                    (CaseReference.format record.Snapshot.Reference)
                    =
                    record.CaseId
            then
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
        installationId
        (calendar: IBusinessCalendar)
        (request: CaseCommand)
        (view: CaseFacts)
        after
        =
        let snapshot = Case.snapshot after
        let index = CaseIndex.create view.ObservedAt snapshot

        let id =
            view.Case
            |> Option.map _.CaseId
            |> Option.defaultWith (fun () ->
                CaseReferenceId.encode installationId (CaseReference.format snapshot.Reference))

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
        let identity = Case.canonical command |> RequestRecord.encode

        authorizeIdentity services command identity view
        |> Verdict.bind (fun () -> propose services.InstallationId services.Calendar command view)

    let execute
        (services: ServiceContext)
        (command: CaseCommand)
        (write: LedgerWriteContext)
        (view: CaseFacts)
        =
        let installationId = services.InstallationId
        let calendar = services.Calendar
        let identity = Case.canonical command |> RequestRecord.encode

        authorizeIdentity services command identity view
        |> Verdict.bind (fun () -> LedgerEnvelope.guard write.Fenced)
        |> Verdict.bind (fun () -> propose services.InstallationId services.Calendar command view)
        |> Verdict.bind (fun (_, after) ->
            let plan = project installationId calendar command view after

            LedgerEnvelope.accept services view.Actor write plan (RequestRecord.encode command))

    let run (services: ServiceContext) (command: CaseCommand) token =
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
                                    (Some command.OperationId)
                                    (CaseReference.format command.Reference)

                            let! fenced = ExecutionFacts.fenced tx view.Actor command.OperationId

                            return!
                                execute
                                    services
                                    command
                                    {
                                        Head = tx.Head
                                        Fenced = fenced
                                        Nonce = tx.Nonce
                                    }
                                    view
                                |> ExecutionFacts.persist tx tx.ProjectCase
                        })
                CancellationToken = token
            }
        )
