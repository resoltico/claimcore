namespace ClaimCore.Domain


open System
open ClaimCore.Domain.ResultFlow

[<NoEquality; NoComparison>]
type PreparedCaseInput = private PreparedCaseInput of RequestValidation.Prepared

[<NoComparison>]
type CaseCommand =
    private
        {
            Source: UnvalidatedCaseRequest
            ParsedOperationId: OperationId
            ParsedExpectedRevision: Revision
            ParsedReference: CaseReference
            ParsedAct: ValidCaseAct
        }

    member this.Authored = this.Source
    member this.Reference = this.ParsedReference
    member this.Act = this.ParsedAct
    member this.OperationId = this.ParsedOperationId
    member this.ExpectedRevision = this.ParsedExpectedRevision

[<NoComparison>]
type CaseRecord =
    private
        {
            CaseId: CaseId
            Reference: CaseReference
            Revision: Revision
            State: CaseState<Claim>
        }

module private BusinessState =
    let fields reference (claim: Claim) : CaseFields =
        {
            CaseReference = reference
            Status = claim.Status
            Registration = claim.Facts
            Finance = claim.Finance
        }

    let restore (accepted: CaseFields) =
        result {
            let progress = accepted.Finance

            do! StateValidation.financial accepted.Registration progress

            return
                {
                    Facts = accepted.Registration
                    Finance = progress
                    Status = accepted.Status
                }
        }

    let openCase today facts =
        let claim =
            {
                Facts = facts
                Finance = FinancialPosition.undecided
                Status = CaseStatus.Opened
            }

        StateValidation.admit today None claim |> Result.map (fun () -> claim)

module Case =
    let private command request (reference, identity, revision, act) =
        {
            Source = request
            ParsedOperationId = identity
            ParsedExpectedRevision = revision
            ParsedReference = reference
            ParsedAct = act
        }

    let prepare request =
        RequestValidation.prepare request |> PreparedCaseInput

    let parsed (PreparedCaseInput prepared) =
        RequestValidation.parsed prepared
        |> Result.map (command (RequestValidation.source prepared))

    let parse request = prepare request |> parsed

    /// Current admission accumulates independently of complete group construction.
    let admitPrepared today (current: CaseSnapshot option) (PreparedCaseInput prepared) =
        let request = RequestValidation.source prepared

        let before =
            current
            |> Option.bind (fun snapshot -> CaseState.fields snapshot.State)
            |> Option.map (fun fields ->
                {
                    Facts = fields.Registration
                    Finance = fields.Finance
                    Status = fields.Status
                })

        // Retain the existing priority of revision conflicts over current admission.
        let revision =
            match current, RequestValidation.parsed prepared with
            | Some current, Ok(_, _, expected, _) when
                CaseAct.kind request.Act <> CaseActKind.Open
                && current.Revision <> expected
                && CaseState.lifecycle current.State <> Lifecycle.Erased
                ->
                Error(Refusal.revisionConflict current.Revision)
            | _ -> Ok()

        revision
        |> Result.bind (fun () ->
            let parse =
                RequestValidation.admitWith
                    (StateValidation.fieldAdmission today before)
                    (Reason.admit
                     >> Result.mapError (fun violation ->
                         Refusal.invalidInput (InputTarget.Reason, violation)))
                    prepared

            let opening, clearing, dates = RequestValidation.chronology prepared
            let contextRules = StateValidation.preparedChronology before opening clearing dates
            Admission.map2 (fun parsed () -> parsed) parse contextRules)
        |> InputValidation.ordered
        |> Result.map (command request)

    let admit today current request =
        prepare request |> admitPrepared today current

    let canonical (command: CaseCommand) =
        { command with
            Source =
                { command.Authored with
                    Act = CaseAct.authored command.Act
                    CaseReference = CaseReference.identity command.Reference
                }
            ParsedReference = CaseReference.canonical command.Reference
        }

    let snapshot (record: CaseRecord) : CaseSnapshot =
        {
            CaseId = record.CaseId
            Reference = record.Reference
            Revision = record.Revision
            State = CaseState.map (BusinessState.fields record.Reference) record.State
        }

    let ofAccepted (accepted: CaseSnapshot) =
        result {
            if accepted.Revision < Revision.first then
                return! Error RecordViolation.RevisionOutOfRange
            else
                let restore (fields: CaseFields) ctor =
                    if fields.CaseReference <> accepted.Reference then
                        Error RecordViolation.ReferenceMismatch
                    else
                        BusinessState.restore fields |> Result.map ctor

                let! state =
                    match accepted.State with
                    | CaseState.Erased -> Ok CaseState.Erased
                    | CaseState.Active fields -> restore fields CaseState.Active
                    | CaseState.Voided fields -> restore fields CaseState.Voided

                return
                    {
                        CaseId = accepted.CaseId
                        Reference = accepted.Reference
                        Revision = accepted.Revision
                        State = state
                    }
        }

    let private transition today paymentTransition act state =
        Transitions.applicable state act
        |> Result.mapError Refusal.Case
        |> Result.bind (Transitions.apply today paymentTransition)

    let private create today (command: CaseCommand) =
        match command.Act with
        | CaseAct.Open _ when command.ExpectedRevision <> Revision.zero ->
            Error(Refusal.revisionConflict Revision.zero)
        | CaseAct.Open facts ->
            BusinessState.openCase today facts
            |> Result.map (fun claim ->
                {
                    CaseId = CaseId.ofOperationId command.OperationId
                    Reference = command.Reference
                    Revision = Revision.first
                    State = CaseState.Active claim
                })
        | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

    let private existing today paymentTransition (request: CaseCommand) (record: CaseRecord) =
        if
            CaseReference.identity record.Reference
            <> CaseReference.identity request.Reference
        then
            invalidArg "record" "The authoritative case does not match the request target."
        else
            match record.State, request.Act with
            | CaseState.Erased, _ -> Error(Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Erased))
            | _, CaseAct.Open _ -> Error(Refusal.Case CaseRefusal.CaseAlreadyExists)
            | _ when record.Revision <> request.ExpectedRevision ->
                Error(Refusal.revisionConflict record.Revision)
            | (CaseState.Active _ | CaseState.Voided _) as state, act ->
                transition today paymentTransition act state
                |> Result.map (fun state ->
                    { record with
                        Revision = Revision.successor record.Revision
                        State = state
                    })

    let private paymentTransition
        (command: CaseCommand)
        current
        (payments: Map<OperationId, PaymentProjectionRow>)
        =
        let owned =
            payments
            |> Map.forall (fun id row ->
                id = row.PaymentId
                && (current
                    |> Option.exists (fun (record: CaseRecord) -> record.CaseId = row.CaseId))
                && (match row.Content with
                    | PaymentProjectionContent.Available(entry, _) -> PaymentEntry.id entry = id
                    | PaymentProjectionContent.Cleared _ -> true))

        if not owned then
            Error RecordViolation.InvalidPaymentState
        else
            match command.Act, current with
            | CaseAct.RecordPayment _, _ ->
                Ok(PaymentTransition.Recording(Map.containsKey command.OperationId payments))
            | CaseAct.RemovePayment id, Some record ->
                let active = CasePayments.active payments

                match Map.tryFind id active, CaseState.fields record.State with
                | None, _
                | _, None -> Ok(PaymentTransition.Removing None)
                | Some entry, Some claim ->
                    FinancialPosition.remove
                        entry
                        (CasePayments.dates (Map.remove id active))
                        claim.Finance
                    |> Result.map (fun after -> PaymentTransition.Removing(Some(entry, after)))
                    |> Result.mapError (fun _ -> RecordViolation.InvalidPaymentState)
            | CaseAct.RemovePayment _, None -> Ok(PaymentTransition.Removing None)
            | _ -> Ok PaymentTransition.NoneRequired

    let decide today payments (command: CaseCommand) current =
        paymentTransition command current payments
        |> Result.mapError CaseDecisionError.InvalidState
        |> Result.bind (fun context ->
            (match current with
             | None -> create today command
             | Some record -> existing today context command record)
            |> Result.mapError CaseDecisionError.Refused
            |> Result.map (fun after ->
                {
                    CaseDecision.After = snapshot after
                    Payment =
                        match command.Act, context with
                        | CaseAct.RecordPayment payment, PaymentTransition.Recording false ->
                            Some(
                                PaymentDifference.Recorded(
                                    PaymentEntry.recorded command.OperationId payment
                                )
                            )
                        | CaseAct.RemovePayment _, PaymentTransition.Removing(Some(entry, _)) ->
                            Some(PaymentDifference.Removed entry)
                        | _ -> None
                }))

    let decideSnapshot today payments (command: CaseCommand) (previous: CaseSnapshot option) =
        let before =
            match previous with
            | None -> Ok None
            | Some snapshot when
                CaseReference.identity snapshot.Reference
                <> CaseReference.identity command.Reference
                ->
                Error RecordViolation.ReferenceMismatch
            | Some snapshot -> ofAccepted snapshot |> Result.map Some

        before
        |> Result.mapError CaseDecisionError.InvalidState
        |> Result.bind (decide today payments command)

    let actRefusal kind (record: CaseRecord) =
        match Transitions.applicable record.State (CaseAct.probe kind) with
        | Ok _ -> None
        | Error refusal -> Some refusal

    let eligibleActs (record: CaseRecord) =
        CaseActKind.all
        |> List.filter (fun kind -> actRefusal kind record |> Option.isNone)

    let correctionActions (record: CaseRecord) group =
        match record.State with
        | CaseState.Active claim -> CaseCorrections.availableActions claim.Finance group
        | _ -> [ CorrectionGroupAction.Keep ]
