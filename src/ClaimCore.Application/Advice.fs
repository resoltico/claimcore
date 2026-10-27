namespace ClaimCore.Application


open ClaimCore.Domain

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

    let private actOffer principal actor resource accepted revision kind =
        match kind with
        | CaseActKind.Correct ->
            let choices = correctionChoices principal actor resource accepted

            if usable choices then
                Some(CaseActOffer.Correct(revision, choices))
            else
                None
        | _ -> Some(CaseActOffer.Act(kind, revision))

    let caseRecord installationId principal actor (record: StoredCase) =
        CaseExecution.current installationId record
        |> Result.map (fun accepted ->
            let resource = ResourceScope.Case record.CaseId

            let acts =
                Case.eligibleActs accepted
                |> List.filter (Authority.canAdvertise principal actor resource)
                |> List.choose (
                    actOffer principal actor resource accepted record.Snapshot.Revision
                )

            {
                Snapshot = record.Snapshot
                Advice =
                    {
                        Acts = acts
                        History = history principal actor resource
                    }
            })

    let installationOffers principal actor =
        [
            if can principal actor Capability.Register ResourceScope.Installation then
                yield InstallationOffer.Open
            if can principal actor Capability.ManageAuthority ResourceScope.Installation then
                for kind in AuthorityKind.all do
                    if kind <> AuthorityKind.ProvisionOwner then
                        yield InstallationOffer.AuthorityAct kind
        ]

    let views principal actor =
        let cases = Authority.visibility principal actor |> Option.isSome

        let authority =
            can principal actor Capability.ReadAuthority ResourceScope.Installation

        [
            if cases then
                yield View.Cases
            if authority then
                yield View.Authority
            if cases || authority then
                yield View.Operations
        ]
