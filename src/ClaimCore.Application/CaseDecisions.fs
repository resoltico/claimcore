namespace ClaimCore.Application

open ClaimCore.Contract

open ClaimCore.Domain
open ClaimCore.RecordFormat

[<RequireQualifiedAccess; NoComparison>]
type internal CaseReviewDecision =
    | Proposed of before: ClaimCore.Domain.CaseRecord option * after: ClaimCore.Domain.CaseRecord
    | Replay of OperationReceipt
    | Refuse of Refusal
    | Fail of CoreFault

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

    let private permission principal actor (request: CaseRequest) (record: StoredCase option) =
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
        | Error fault -> CaseReviewDecision.Fail fault
        | Ok before ->
            match Case.decide (calendar.DateAt view.ObservedAt) request before with
            | Error refusal -> CaseReviewDecision.Refuse(refusal)
            | Ok after -> CaseReviewDecision.Proposed(before, after)

    let review installationId calendar principal request (view: CaseReadView) =
        let bytes = RequestRecord.encode request

        match admission installationId principal request bytes view with
        | IdentityDecision.Refuse refusal -> CaseReviewDecision.Refuse refusal
        | IdentityDecision.Fail fault -> CaseReviewDecision.Fail fault
        | IdentityDecision.Replay entry ->
            LedgerEntries.receipt principal view.Actor view.Case entry
            |> Result.map CaseReviewDecision.Replay
            |> Result.defaultWith CaseReviewDecision.Fail
        | IdentityDecision.Fresh -> propose installationId calendar request view

    let private append
        installationId
        (calendar: IBusinessCalendar)
        principal
        (request: CaseRequest)
        bytes
        (view: CaseLockedView)
        after
        =
        let snapshot = Case.snapshot after

        let id =
            view.Case
            |> Option.map _.CaseId
            |> Option.defaultWith (fun () ->
                CaseReferenceId.encode installationId snapshot.Reference)

        let actor =
            view.Actor.Actor
            |> Option.defaultWith (fun () ->
                invalidArg (nameof view) "Authorized actor is missing.")

        let scope =
            StoredScope.Case(
                id,
                snapshot.Revision,
                CaseActs.kind request.Act,
                calendar.DateAt view.ObservedAt
            )

        let entry =
            LedgerEntries.create
                installationId
                view.Head
                view.ObservedAt
                view.PayloadNonce
                actor.ActorId
                request.OperationId
                scope
                bytes
                (ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot)

        let projection =
            match snapshot.View, view.Case with
            | CaseView.Erased, _ -> CaseProjection.Erase(id, snapshot.Revision)
            | _, None -> CaseProjection.Insert(id, snapshot)
            | _ -> CaseProjection.Update(id, snapshot)

        let current = Some { CaseId = id; Snapshot = snapshot }

        LedgerEntries.validate installationId (LedgerEntries.stored entry)
        |> Result.bind (LedgerEntries.receipt principal view.Actor current)
        |> Result.map (fun receipt -> LedgerDecision.Append(entry, projection, receipt))
        |> Result.defaultWith LedgerDecision.Fail

    let execute installationId calendar principal request (view: CaseLockedView) =
        let bytes = RequestRecord.encode request

        let read: CaseReadView =
            {
                ObservedAt = view.ObservedAt
                Actor = view.Actor
                ExistingOperation = view.ExistingOperation
                Case = view.Case
            }

        match admission installationId principal request bytes read with
        | IdentityDecision.Refuse refusal -> LedgerDecision.Refuse refusal
        | IdentityDecision.Fail fault -> LedgerDecision.Fail fault
        | IdentityDecision.Replay entry ->
            LedgerEntries.receipt principal view.Actor view.Case entry
            |> Result.map LedgerDecision.Replay
            |> Result.defaultWith LedgerDecision.Fail
        | IdentityDecision.Fresh when view.Head.Sequence = Revision.Maximum ->
            LedgerDecision.Refuse Refusal.LedgerExhausted
        | IdentityDecision.Fresh ->
            match propose installationId calendar request read with
            | CaseReviewDecision.Proposed(_, after) ->
                append installationId calendar principal request bytes view after
            | CaseReviewDecision.Refuse refusal -> LedgerDecision.Refuse refusal
            | CaseReviewDecision.Fail fault -> LedgerDecision.Fail fault
            | CaseReviewDecision.Replay receipt -> LedgerDecision.Replay receipt
