namespace ClaimCore.Application


open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal CaseExecution =
    let authorize principal actor capability (record: StoredCase option) =
        record
        |> Option.exists (fun value ->
            Authority.can principal actor capability (ResourceScope.Case value.CaseId))

    let current installationId (record: StoredCase) =
        Case.ofAccepted record.Snapshot
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun value ->
            if
                CaseReferenceId.encode
                    installationId
                    (CaseReference.format record.Snapshot.Reference)
                    =
                    record.CaseId
            then
                Ok value
            else
                Error CoreFault.StoreIntegrityError)

    let permission principal actor (request: CaseCommand) (record: StoredCase option) =
        if CaseActs.kind request.Act = CaseActKind.Open then
            if Authority.canAct principal actor ResourceScope.Installation request.Act then
                Ok()
            else
                Error Refusal.ResourceUnavailable
        else
            match record with
            | None -> Error Refusal.ResourceUnavailable
            | Some value when
                Authority.canAct principal actor (ResourceScope.Case value.CaseId) request.Act
                ->
                Ok()
            | Some value when
                Authority.can principal actor Capability.ReadCase (ResourceScope.Case value.CaseId)
                ->
                Error Refusal.ActNotPermitted
            | _ -> Error Refusal.ResourceUnavailable

    let private authorizeIdentity installationId principal request bytes (view: CaseFacts) =
        match permission principal view.Actor request view.Case with
        | Error refusal -> Verdict.Refuse refusal
        | Ok() ->
            OperationIdentity.decide
                installationId
                principal
                view.Actor
                request.OperationId
                bytes
                view.ExistingOperation
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

        let id =
            view.Case
            |> Option.map _.CaseId
            |> Option.defaultWith (fun () ->
                CaseReferenceId.encode installationId (CaseReference.format snapshot.Reference))

        let projection =
            match snapshot.State, view.Case with
            | CaseState.Erased, _ -> CaseProjection.Erase(id, snapshot.Revision)
            | _, None -> CaseProjection.Insert(id, snapshot)
            | _ -> CaseProjection.Update(id, snapshot)

        {
            ObservedAt = view.ObservedAt
            ActorId = LedgerEnvelope.actorId view.Actor
            Subject =
                EntryTarget.Case(
                    id,
                    snapshot.Revision,
                    CaseActs.kind request.Act,
                    calendar.DateAt view.ObservedAt
                )
            Snapshot = ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot
            Current = Some { CaseId = id; Snapshot = snapshot }
            Projection = projection
        }

    let review installationId calendar principal (command: CaseCommand) (view: CaseFacts) =
        let identity = Case.canonical command |> RequestRecord.encode

        authorizeIdentity installationId principal command identity view
        |> Verdict.bind (fun () -> propose installationId calendar command view)

    let execute
        installationId
        calendar
        principal
        (command: CaseCommand)
        head
        fenced
        nonce
        (view: CaseFacts)
        =
        let identity = Case.canonical command |> RequestRecord.encode

        authorizeIdentity installationId principal command identity view
        |> Verdict.bind (fun () -> LedgerEnvelope.guard fenced)
        |> Verdict.bind (fun () -> propose installationId calendar command view)
        |> Verdict.bind (fun (_, after) ->
            let plan = project installationId calendar command view after

            LedgerEnvelope.accept
                installationId
                principal
                view.Actor
                command.OperationId
                head
                nonce
                plan
                (RequestRecord.encode command))

    let run (store: IStore) installationId calendar principal (command: CaseCommand) token =
        store.Write(
            (fun tx ->
                task {
                    let! view =
                        ExecutionFacts.case
                            tx
                            installationId
                            principal
                            (Some command.OperationId)
                            (CaseReference.format command.Reference)

                    let! fenced = ExecutionFacts.fenced tx view.Actor command.OperationId

                    return!
                        execute
                            installationId
                            calendar
                            principal
                            command
                            tx.Head
                            fenced
                            tx.Nonce
                            view
                        |> ExecutionFacts.persist tx tx.ProjectCase
                }),
            token
        )
