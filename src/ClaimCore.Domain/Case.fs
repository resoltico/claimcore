namespace ClaimCore.Domain


open System
open ClaimCore.Domain.ResultFlow

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
            Work = claim.Work
            CaseReference = reference
            Status = claim.Status
            Registration = claim.Facts
            Progress = claim.Progress
        }

    let private retained value =
        value
        |> Result.mapError (function
            | Refusal.InvalidInput issues ->
                let issue = InputIssues.head issues
                RecordViolation.InvalidScalar(issue.Target, issue.Violation)
            | _ -> RecordViolation.InvalidProgress)

    let restore (accepted: CaseFields) =
        result {
            let progress = accepted.Progress

            do! StateValidation.progress accepted.Registration progress |> retained

            return
                {
                    Work = accepted.Work
                    Facts = accepted.Registration
                    Progress = progress
                    Status = accepted.Status
                }
        }

    let openCase today facts =
        let claim =
            {
                Work = CaseWork.empty
                Facts = facts
                Progress = PaymentProgress.Undecided
                Status = CaseStatus.Opened
            }

        StateValidation.admit today None claim |> Result.map (fun () -> claim)

module Case =
    let parse request =
        RequestValidation.parse request
        |> Result.map (fun (reference, identity, revision, act) ->
            {
                Source = request
                ParsedOperationId = identity
                ParsedExpectedRevision = revision
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

    let private transition today act state =
        Transitions.applicable state act
        |> Result.mapError Refusal.Case
        |> Result.bind (Transitions.apply today)

    let private create today (command: CaseCommand) =
        match command.Act with
        | CaseAct.Open _ when command.ExpectedRevision <> Revision.zero ->
            Error(Refusal.RevisionConflict Revision.zero)
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

    let private existing today (request: CaseCommand) (record: CaseRecord) =
        if
            CaseReference.identity record.Reference
            <> CaseReference.identity request.Reference
        then
            invalidArg "record" "The authoritative case does not match the request target."
        else
            match record.State, request.Act with
            | CaseState.Erased, _ -> Error(Refusal.Case CaseRefusal.CaseErased)
            | _, CaseAct.Open _ -> Error(Refusal.Case CaseRefusal.CaseAlreadyExists)
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
        |> List.filter (CaseAct.probe >> Transitions.applicable record.State >> Result.isOk)

    let correctionActions (record: CaseRecord) group =
        match record.State with
        | CaseState.Active claim -> CaseCorrections.availableActions claim.Progress group
        | _ -> [ CorrectionGroupAction.Keep ]
