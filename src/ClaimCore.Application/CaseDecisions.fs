namespace ClaimCore.Application

open ClaimCore.Contract

open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal CaseDecisions =
    let authorize principal actor capability (record: StoredCase option) =
        record
        |> Option.exists (fun value ->
            ActorAuthorization.can principal actor capability (ResourceScope.Case value.CaseId))

    let current installationId (record: StoredCase) =
        Case.ofAccepted record.Snapshot
        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        |> Result.bind (fun value ->
            if CaseReferenceId.encode installationId record.Snapshot.Reference = record.CaseId then
                Ok value
            else
                Error CoreFault.StoreIntegrityError)

    let permission principal actor (request: CaseRequest) (record: StoredCase option) =
        if CaseActs.kind request.Act = (CaseActKind.Business BusinessActKind.Open) then
            if ActorAuthorization.canAct principal actor ResourceScope.Installation request.Act then
                Ok()
            else
                Error Refusal.ResourceUnavailable
        else
            match record with
            | None -> Error Refusal.ResourceUnavailable
            | Some value when
                ActorAuthorization.canAct
                    principal
                    actor
                    (ResourceScope.Case value.CaseId)
                    request.Act
                ->
                Ok()
            | Some value when
                ActorAuthorization.can
                    principal
                    actor
                    Capability.ReadCase
                    (ResourceScope.Case value.CaseId)
                ->
                Error Refusal.ActNotPermitted
            | _ -> Error Refusal.ResourceUnavailable

    let private admission installationId principal request bytes (view: CaseReadView) =
        match permission principal view.Actor request view.Case with
        | Error refusal -> IdentityDecision.Refuse refusal
        | Ok() ->
            OperationIdentity.decide
                installationId
                principal
                view.Actor
                request.OperationId
                bytes
                view.ExistingOperation

    let private propose installationId (calendar: IBusinessCalendar) request (view: CaseReadView) =
        let before =
            match view.Case with
            | None -> Ok None
            | Some record -> current installationId record |> Result.map Some

        match before with
        | Error fault -> LedgerReviewDecision.Fail fault
        | Ok before ->
            match Case.decide (calendar.DateAt view.ObservedAt) request before with
            | Error refusal -> LedgerReviewDecision.Refuse(refusal)
            | Ok after -> LedgerReviewDecision.Proposed(before, after)

    let private project
        installationId
        (calendar: IBusinessCalendar)
        (request: CaseRequest)
        (view: CaseReadView)
        after
        =
        let snapshot = Case.snapshot after

        let id =
            view.Case
            |> Option.map _.CaseId
            |> Option.defaultWith (fun () ->
                CaseReferenceId.encode installationId snapshot.Reference)

        let projection =
            match snapshot.State, view.Case with
            | CaseState.Erased, _ -> CaseProjection.Erase(id, snapshot.Revision)
            | _, None -> CaseProjection.Insert(id, snapshot)
            | _ -> CaseProjection.Update(id, snapshot)

        {
            ObservedAt = view.ObservedAt
            ActorId = LedgerEnvelope.actorId view.Actor
            Subject =
                StoredSubject.Case(
                    id,
                    snapshot.Revision,
                    CaseActs.kind request.Act,
                    calendar.DateAt view.ObservedAt
                )
            Snapshot = ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot
            Current = Some { CaseId = id; Snapshot = snapshot }
            Projection = projection
        }

    let private subject installationId calendar principal request =
        {
            Encode = fun () -> RequestRecord.encode request
            Canonical = fun () -> Case.canonical request |> RequestRecord.encode
            Admit = admission installationId principal request
            Propose = propose installationId calendar request
            Project = fun view (_, after) -> project installationId calendar request view after
            Receipt =
                fun (view: CaseReadView) current entry ->
                    LedgerEntries.receipt
                        principal
                        view.Actor
                        (current |> Option.orElse view.Case)
                        entry
        }

    let review installationId calendar principal request (view: CaseReadView) =
        LedgerEnvelope.review (subject installationId calendar principal request) view

    let execute installationId calendar principal (request: CaseRequest) (view: CaseLockedView) =
        LedgerEnvelope.execute
            installationId
            request.OperationId
            (subject installationId calendar principal request)
            view.Head
            view.IsFenced
            view.PayloadNonce
            view.Read
