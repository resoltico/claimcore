namespace ClaimCore.Domain


open System
open ClaimCore.Domain.ResultFlow

[<NoComparison>]
type CaseCommand =
    private
        {
            Source: UnvalidatedCaseRequest
            ParsedReference: CaseReference
            ParsedAct: ValidCaseAct
        }

    member this.Authored = this.Source
    member this.Reference = this.ParsedReference
    member this.Act = this.ParsedAct
    member this.OperationId = this.Source.OperationId
    member this.ExpectedRevision = this.Source.ExpectedRevision

[<NoComparison>]
type CaseRecord =
    private
        {
            Reference: CaseReference
            Revision: int64
            State: CaseState<Claim>
        }

module private BusinessState =
    let fields reference (claim: Claim) : CaseFields =
        {
            CaseReference = reference
            Status = claim.Status
            Registration = claim.Facts
            Progress = claim.Progress
        }

    let private retained value =
        value
        |> Result.mapError (function
            | Refusal.InvalidInput(field, violation) ->
                RecordViolation.InvalidScalar(field, violation)
            | _ -> RecordViolation.InvalidProgress)

    let restore (accepted: CaseFields) =
        result {
            let progress = accepted.Progress

            do! StateValidation.progress accepted.Registration progress |> retained

            return
                {
                    Facts = accepted.Registration
                    Progress = progress
                    Status = accepted.Status
                }
        }

    let openCase (today: DateOnly) (facts: Registration) =
        result {
            do!
                Validation.notFuture
                    (InputTarget.Field BusinessField.IncidentDate)
                    today
                    facts.IncidentDate

            do!
                Validation.notFuture
                    (InputTarget.Field BusinessField.IncidentNotificationDate)
                    today
                    facts.IncidentNotificationDate

            do! Validation.assertedText None facts

            return
                {
                    Facts = facts
                    Progress = PaymentProgress.Undecided
                    Status = CaseStatus.Opened
                }
        }

module Case =
    let parse request =
        RequestValidation.parse request
        |> Result.map (fun (reference, act) ->
            {
                Source = request
                ParsedReference = reference
                ParsedAct = act
            })

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
            Reference = record.Reference
            Revision = record.Revision
            State = CaseState.map (BusinessState.fields record.Reference) record.State
        }

    let ofAccepted (accepted: CaseSnapshot) =
        result {
            if accepted.Revision < 1L || accepted.Revision > Revision.Maximum then
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
                        Reference = accepted.Reference
                        Revision = accepted.Revision
                        State = state
                    }
        }

    let private transition today act state =
        Transitions.applicable state (CaseAct.kind act)
        |> Result.bind (fun evidence -> Transitions.apply today evidence act)

    let private create today (command: CaseCommand) =
        match command.Act with
        | CaseAct.Open _ when command.ExpectedRevision <> 0L -> Error(Refusal.RevisionConflict 0L)
        | CaseAct.Open facts ->
            BusinessState.openCase today facts
            |> Result.map (fun claim ->
                {
                    Reference = command.Reference
                    Revision = 1L
                    State = CaseState.Active claim
                })
        | _ -> Error Refusal.ResourceUnavailable

    let private existing today (request: CaseCommand) (record: CaseRecord) =
        if
            CaseReference.identity record.Reference
            <> CaseReference.identity request.Reference
        then
            invalidArg "record" "The authoritative case does not match the request target."
        else
            match record.State, request.Act with
            | CaseState.Erased, _ -> Error Refusal.CaseErased
            | _, CaseAct.Open _ -> Error Refusal.CaseAlreadyExists
            | _ when record.Revision <> request.ExpectedRevision ->
                Error(Refusal.RevisionConflict record.Revision)
            | (CaseState.Active _ | CaseState.Voided _) as state, act ->
                transition today act state
                |> Result.map (fun state ->
                    { record with
                        Revision = Revision.successor record.Revision
                        State = state
                    })

    let decide today command current =
        match current with
        | None -> create today command
        | Some record -> existing today command record

    let eligibleActs (record: CaseRecord) =
        CaseActKind.all
        |> List.filter (Transitions.applicable record.State >> Result.isOk)

    let correctionActions (record: CaseRecord) group =
        match record.State with
        | CaseState.Active claim -> CaseCorrections.availableActions claim.Progress group
        | _ -> [ CorrectionGroupAction.Keep ]
