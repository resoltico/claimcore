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
            Payment = actions RecordGroup.Payment
        }

    let private usable (choices: CorrectionChoices) =
        [ choices.Registration; choices.Decision; choices.Payment ]
        |> List.exists (fun values -> values <> [ CorrectionGroupAction.Keep ])

    let private history principal actor resource =
        [
            HistoryDetail.Summary, Capability.ReadHistory
            HistoryDetail.Full, Capability.ReadHistoryFull
        ]
        |> List.choose (fun (detail, capability) ->
            if can principal actor capability resource then
                Some detail
            else
                None)

    let caseRecord (services: ServiceContext) actor (record: StoredCase) =
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
                CaseAdvice.create acts correction (history principal actor resource)
                |> Result.defaultWith invalidOp

            {
                Snapshot = record.Snapshot
                Advice = advice
                AssignedToYou = Authority.assignedTo principal actor record.CaseId
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
