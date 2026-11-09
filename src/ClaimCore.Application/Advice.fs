namespace ClaimCore.Application


open ClaimCore.Domain
open ClaimCore.Application.Contracts

module internal Advice =
    let private can principal actor capability resource =
        Authority.can principal actor capability resource

    let private correctionChoices principal actor resource accepted : CorrectionChoices =
        let actions group =
            if can principal actor (Authority.correctionCapability group) resource then
                Case.correctionActions accepted group
            else
                [ CorrectionGroupAction.Keep ]

        {
            Registration = actions RecordGroup.Registration
            Decision = actions RecordGroup.Decision
        }

    let private usable (choices: CorrectionChoices) =
        [ choices.Registration; choices.Decision ]
        |> List.exists (fun values -> values <> [ CorrectionGroupAction.Keep ])

    let private history principal actor resource =
        if can principal actor Capability.ReadHistoryFull resource then
            HistoryAccess.Full
        elif can principal actor Capability.ReadHistory resource then
            HistoryAccess.Summary
        else
            HistoryAccess.None

    let private blockedActs principal actor (record: StoredCase) accepted acts =
        let resource = Scope.Case record.CaseId

        CaseActKind.all
        |> List.filter (fun kind -> not (List.contains kind acts))
        |> List.map (fun kind ->
            let reason =
                match
                    CaseExecution.permissionFor principal actor (CaseAct.probe kind) (Some record)
                with
                | Error refusal -> refusal
                | Ok() ->
                    match Case.actRefusal kind accepted with
                    | Some refusal -> Refusal.Case refusal
                    | None when kind = CaseActKind.Correct ->
                        let required =
                            Authority.correctionGroups
                            |> List.tryPick (fun (group, capability) ->
                                if
                                    Case.correctionActions accepted group
                                    <> [ CorrectionGroupAction.Keep ]
                                    && not (can principal actor capability resource)
                                then
                                    Some capability
                                else
                                    None)
                            |> Option.defaultWith (fun () ->
                                invalidOp
                                    "An unavailable correction requires a missing editable-group capability.")

                        Refusal.Access(AccessRefusal.NotAuthorized required)
                    | None -> invalidOp "An unavailable act requires a core reason."

            {
                CaseActRefusal.Act = kind
                Reason = reason
            })

    let caseRecord
        (services: ServiceContext)
        actor
        (work: CaseWorkView)
        stages
        changedAt
        recordedBy
        (record: StoredCase)
        =
        let installationId = services.InstallationId
        let principal = services.Principal

        CaseExecution.current installationId record
        |> Result.map (fun accepted ->
            let resource = Scope.Case record.CaseId

            let eligible =
                Case.eligibleActs accepted
                |> List.filter (Authority.canAdvertise principal actor resource)

            let correction =
                if List.contains CaseActKind.Correct eligible then
                    let choices = correctionChoices principal actor resource accepted
                    if usable choices then Some choices else None
                else
                    None

            let acts =
                eligible
                |> List.filter (fun kind -> kind <> CaseActKind.Correct || correction.IsSome)

            let advice =
                CaseAdvice.create
                    acts
                    correction
                    (history principal actor resource)
                    (blockedActs principal actor record accepted acts)
                |> Result.bind (
                    CaseAdvice.withRecommendation (
                        ClaimCore.Domain.CaseAdvice.recommended
                            (CaseWorkReads.facts record work).State
                            acts
                    )
                )
                |> Result.defaultWith invalidOp

            {
                Work =
                    let active = CaseState.lifecycle record.Snapshot.State = Lifecycle.Active

                    let opened =
                        CaseState.fields record.Snapshot.State
                        |> Option.exists (fun fields -> fields.Status = CaseStatus.Opened)

                    let permitted =
                        can principal actor Capability.ReadCase resource
                        && can principal actor Capability.ManageCaseWork resource

                    { work with
                        CanUpdateSummary = active && permitted
                        CanAssign = CaseWorkReads.canAssign principal actor false record.Snapshot
                        CanCreateReminder = active && opened && permitted
                    }
                Snapshot = record.Snapshot
                Stages = stages
                Disposition = None
                NextActions = []
                Notes =
                    {
                        Items = []
                        Count = 0L
                        CanAdd = false
                    }
                ChangedAt = changedAt
                RecordedBy = recordedBy
                Headline = CaseHeadline.ofState (CaseWorkReads.facts record work).State
                Attention =
                    CaseAttention.at work.Basis.BusinessDate (CaseWorkReads.facts record work)
                Advice = advice
                Mine =
                    match actor.Actor, work.Responsible with
                    | Some requester, Some assigned -> assigned.ActorId = requester.ActorId
                    | _ -> false
            })

    let installationOffers principal actor =
        [
            if can principal actor Capability.Register Scope.Installation then
                yield InstallationOffer.Open
            if can principal actor Capability.ManageAuthority Scope.Installation then
                for kind in AuthorityKind.all do
                    if kind <> AuthorityKind.ProvisionOwner then
                        yield InstallationOffer.AuthorityAct kind
        ]

    let views principal actor =
        let cases = Authority.visibility principal actor |> Option.isSome

        let authority = can principal actor Capability.ReadAuthority Scope.Installation

        [
            if cases then
                yield View.Cases
            if authority then
                yield View.Authority
            if Authority.validActor principal actor then
                yield View.Operations
        ]
