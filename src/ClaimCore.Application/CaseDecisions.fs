namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal CaseReviewDecision =
    | Proposed of Claim option * Claim
    | Replay of StoredEntry
    | Refuse of Rejection
    | Fail of CoreFault

module internal CaseDecisions =
    let authorize principal actor capability opening (case: StoredCase option) =
        match opening, case with
        | true, _ -> ActorAuthorization.can principal actor capability ResourceScope.Installation
        | false, Some value ->
            ActorAuthorization.can principal actor capability (ResourceScope.Case value.CaseId)
        | _ -> false

    let current (case: StoredCase) =
        match case.State with
        | StoredCaseState.Erased -> Error CoreFault.StoreIntegrityError
        | StoredCaseState.Live(view, _) when
            view.Version = case.Revision && view.Fields.CaseReference = case.Reference
            ->
            Claim.restore view |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
        | _ -> Error CoreFault.StoreIntegrityError

    let review
        (calendar: IBusinessCalendar)
        principal
        (request: CommandRequest)
        (view: CaseReadView)
        =
        if
            not (
                authorize
                    principal
                    view.Actor
                    Capability.EditCase
                    (Commands.kind request.Command = CommandKind.Open)
                    view.Case
            )
        then
            CaseReviewDecision.Refuse Rejection.ResourceUnavailable
        else
            match
                OperationIdentity.decide
                    principal
                    view.Actor
                    (RequestRecord.encode request)
                    view.ExistingOperation
            with
            | IdentityDecision.Replay entry -> CaseReviewDecision.Replay entry
            | IdentityDecision.Refuse reason -> CaseReviewDecision.Refuse reason
            | IdentityDecision.Fail fault -> CaseReviewDecision.Fail fault
            | IdentityDecision.Fresh ->
                match view.Case with
                | Some { State = StoredCaseState.Erased } ->
                    CaseReviewDecision.Refuse Rejection.CaseErased
                | Some {
                           State = StoredCaseState.Live(_, CaseDisposition.Voided)
                       } -> CaseReviewDecision.Refuse Rejection.CaseVoided
                | value ->
                    let before =
                        match value with
                        | None -> Ok None
                        | Some case -> current case |> Result.map Some

                    match before with
                    | Error fault -> CaseReviewDecision.Fail fault
                    | Ok claim ->
                        match Claim.decide (calendar.DateAt view.ObservedAt) request claim with
                        | Error reason -> CaseReviewDecision.Refuse(Rejection.Domain reason)
                        | Ok after -> CaseReviewDecision.Proposed(claim, after)

    let command installationId calendar principal request (view: CaseLockedView) =
        let read: CaseReadView =
            {
                ObservedAt = view.ObservedAt
                Actor = view.Actor
                ExistingOperation = view.ExistingOperation
                Case = view.Case
            }

        match review calendar principal request read with
        | CaseReviewDecision.Replay entry -> CaseDecision.Replay entry
        | CaseReviewDecision.Refuse reason -> CaseDecision.Refuse reason
        | CaseReviewDecision.Fail fault -> CaseDecision.Fail fault
        | CaseReviewDecision.Proposed(_, after) ->
            let snapshot = Claim.view after
            let id = view.Case |> Option.map _.CaseId |> Option.defaultValue request.OperationId

            let actor =
                view.Actor
                |> Option.defaultWith (fun () -> invalidOp "Authorized actor missing.")

            let scope =
                StoredScope.Case(
                    id,
                    snapshot.Version,
                    AcceptedAct.Command(Commands.kind request.Command),
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
                    (RequestRecord.encode request)
                    (ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot)

            let projection =
                if view.Case.IsNone then
                    CaseProjection.Insert(id, snapshot)
                else
                    CaseProjection.Update(id, snapshot, CaseDisposition.Active)

            CaseDecision.Append(entry, projection)

    let private stewardshipRefusal =
        function
        | StewardshipError.InvalidRequest error -> Rejection.Domain error
        | StewardshipError.InvalidReason -> Rejection.InvalidReason
        | StewardshipError.AlreadyVoided -> Rejection.AlreadyVoided
        | StewardshipError.NotVoided -> Rejection.NotVoided
        | StewardshipError.VersionConflict value ->
            Rejection.Domain(DomainError.VersionConflict value)
        | StewardshipError.RevisionExhausted ->
            Rejection.Domain(
                DomainError.InvalidInput(
                    InputTarget.ExpectedVersion,
                    InputViolation.Command CommandViolation.RevisionExhausted
                )
            )

    let private stewardshipProjection (case: StoredCase) before result =
        match result with
        | StewardshipResult.Erased revision ->
            revision, [||], CaseProjection.Erase(case.CaseId, revision)
        | StewardshipResult.Retained(revision, next) ->
            let snapshot =
                { Claim.view before with
                    Version = revision
                }

            revision,
            ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot,
            CaseProjection.Update(case.CaseId, snapshot, next)

    let private stewardFresh
        installationId
        (calendar: IBusinessCalendar)
        (request: StewardshipRequest)
        (view: CaseLockedView)
        =
        match view.Case with
        | None -> CaseDecision.Refuse Rejection.ResourceUnavailable
        | Some { State = StoredCaseState.Erased } -> CaseDecision.Refuse Rejection.CaseErased
        | Some case ->
            let disposition =
                match case.State with
                | StoredCaseState.Live(_, value) -> value
                | _ -> invalidOp "Live case required."

            match current case, Stewardship.decide request case.Revision disposition with
            | Error fault, _ -> CaseDecision.Fail fault
            | _, Error error ->
                let reason = stewardshipRefusal error
                CaseDecision.Refuse reason
            | Ok before, Ok result ->
                let revision, snapshot, projection = stewardshipProjection case before result

                let actor =
                    view.Actor
                    |> Option.defaultWith (fun () -> invalidOp "Authorized actor missing.")

                let scope =
                    StoredScope.Case(
                        case.CaseId,
                        revision,
                        AcceptedAct.Stewardship(Stewardship.kind request.Action),
                        calendar.DateAt view.ObservedAt
                    )

                CaseDecision.Append(
                    LedgerEntries.create
                        installationId
                        view.Head
                        view.ObservedAt
                        view.PayloadNonce
                        actor.ActorId
                        request.OperationId
                        scope
                        (StewardshipRecord.encode request)
                        snapshot,
                    projection
                )

    let steward installationId calendar principal request (view: CaseLockedView) =
        if not (authorize principal view.Actor Capability.StewardCase false view.Case) then
            CaseDecision.Refuse Rejection.ResourceUnavailable
        else
            match
                OperationIdentity.decide
                    principal
                    view.Actor
                    (StewardshipRecord.encode request)
                    view.ExistingOperation
            with
            | IdentityDecision.Replay entry -> CaseDecision.Replay entry
            | IdentityDecision.Refuse reason -> CaseDecision.Refuse reason
            | IdentityDecision.Fail fault -> CaseDecision.Fail fault
            | IdentityDecision.Fresh -> stewardFresh installationId calendar request view
