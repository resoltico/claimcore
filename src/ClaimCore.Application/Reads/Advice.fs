namespace ClaimCore.Application.Reads

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Tasks
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage

module internal Advice =
    let private history principal actor resource =
        if Access.can principal actor Capability.ReadHistoryFull resource then
            HistoryAccess.Full
        elif Access.can principal actor Capability.ReadHistory resource then
            HistoryAccess.Summary
        else
            HistoryAccess.None

    let private correction (offers: (TaskDeclaration * Availability) list) =
        offers
        |> List.tryPick (fun (_, availability) ->
            if availability.Blocked.IsSome then
                None
            else
                match
                    Map.tryFind
                        (TaskInputId.Target(InputTarget.Group RecordGroup.Registration))
                        availability.Choices,
                    Map.tryFind
                        (TaskInputId.Target(InputTarget.Group RecordGroup.Decision))
                        availability.Choices
                with
                | Some registration, Some decision ->
                    let parse values =
                        values
                        |> List.map (fun value ->
                            Vocabulary.tryParse CorrectionGroupAction.vocabulary value
                            |> Option.defaultWith (fun () ->
                                invalidOp
                                    "A declared correction choice is not in its domain vocabulary."))

                    Some
                        {
                            CorrectionChoices.Registration = parse registration
                            Decision = parse decision
                        }
                | _ -> None)

    let caseRecord
        (services: ServiceContext)
        (tx: ReadTx)
        actor
        (maintenance: RegisterMaintenance)
        familySequence
        (work: CaseWorkView)
        stages
        changedAt
        recordedBy
        latestPayments
        (record: StoredCase)
        =
        task {
            let! current =
                CaseFamilyAcquisition.familyForPayments
                    tx
                    record.CaseId
                    (latestPayments |> List.map PaymentEntry.id)

            let parties =
                current
                |> Option.map (fun value -> DecisionFacts.currentParties value.Facts)
                |> Option.defaultValue []

            match current, CaseRecords.current services.InstallationId parties record with
            | Some { Facts = state; Origins = origins }, Ok snapshot when
                DecisionFacts.snapshot state = Some snapshot && state.Sequence = familySequence
                ->
                let! payees =
                    PaymentStorage.payees
                        tx.Connection
                        record.CaseId
                        latestPayments
                        tx.CancellationToken

                let environment =
                    {
                        FamilyEnvironment.Requester = actor
                        RequesterPrincipal = services.Principal
                        Maintenance = maintenance.Active
                        RelatedCase = fun _ -> None
                        ReferenceOwner = fun _ -> None
                        AssigneeEligible = fun _ -> false
                    }

                let partyTasks creates binding =
                    Registry.all
                    |> List.choose (function
                        | Task.Family task when
                            task.Declaration.Target = ActSubject.Party
                            && task.Declaration.Creates = creates
                            ->
                            let offered = task.Availability environment (Some state) binding

                            if offered.Blocked.IsNone then
                                Some(TaskOffer.Available(task.Declaration.Id, binding, offered))
                            else
                                None
                        | _ -> None)

                let partyOffers = partyTasks true (Binding.PartyNew(CaseSelector.Id state.Id))

                let parties =
                    state.Parties
                    |> Map.toList
                    |> List.choose (fun (id, party) ->
                        if party.Value.Removed then
                            None
                        else
                            party.Value.Content
                            |> Option.map (fun content ->
                                (Map.find
                                    (ClaimCore.Domain.Changes.PartKind.Party, PartyId.value id)
                                    origins)
                                    .Position,
                                {
                                    PartyId = id
                                    Revision = party.Revision
                                    Content = content
                                    Offers =
                                        partyTasks
                                            false
                                            (Binding.Party(
                                                CaseSelector.Id state.Id,
                                                id,
                                                party.Revision
                                            ))
                                }))
                    |> List.sortBy fst
                    |> List.map snd

                let offers =
                    Registry.all
                    |> List.choose (function
                        | Task.Family task when
                            task.Declaration.Exposure = TaskExposure.Client
                            && task.Declaration.Target = ActSubject.Case
                            ->
                            let binding =
                                if task.Declaration.Creates then
                                    Binding.CaseNew Revision.zero
                                elif task.Declaration.ReviewsFamilySequence then
                                    Binding.CaseErase(
                                        CaseSelector.Id state.Id,
                                        state.Case.Revision,
                                        state.Sequence
                                    )
                                else
                                    Binding.Case(CaseSelector.Id state.Id, state.Case.Revision)
                            // Parent advice observes case eligibility. Payment-row affordances carry
                            // the additional selected payment identity before an action is emitted.
                            Some(
                                (task.Declaration, binding),
                                task.Availability environment (Some state) binding
                            )
                        | _ -> None)

                let controls =
                    offers
                    |> List.choose (fun ((declaration, binding), available) ->
                        if TaskDeclaration.bindingShape declaration = BindingShape.CasePayment then
                            None
                        elif declaration.Creates then
                            Some(TaskOffer.Declared declaration.Id)
                        else
                            Some(TaskOffer.forBinding declaration.Id binding available))

                let offers =
                    offers
                    |> List.map (fun ((declaration, _), availability) -> declaration, availability)

                let acts =
                    offers
                    |> List.choose (fun (task, offered) ->
                        if offered.Blocked.IsNone then Some task.Id else None)

                let blocked =
                    offers
                    |> List.choose (fun (task, offered) ->
                        offered.Blocked
                        |> Option.map (fun reason ->
                            {
                                CaseTaskRefusal.Act = task.Id
                                Reason = reason
                            }))

                let recommendation = Registry.recommendation offers

                match
                    CaseAdvice.create
                        acts
                        (correction offers)
                        (history services.Principal actor (Scope.Case state.Id))
                        blocked
                    |> Result.bind (CaseAdvice.withRecommendation recommendation)
                with
                | Error _ -> return Error CoreFault.StoreIntegrityError
                | Ok advice ->
                    return
                        Ok
                            {
                                Work =
                                    { work with
                                        CanUpdateSummary =
                                            CaseWorkPolicy.canEdit
                                                services.Principal
                                                actor
                                                maintenance.Active
                                                snapshot
                                        CanAssign =
                                            CaseWorkPolicy.canAssign
                                                services.Principal
                                                actor
                                                maintenance.Active
                                                snapshot
                                        CanCreateReminder =
                                            CaseWorkPolicy.canAssign
                                                services.Principal
                                                actor
                                                maintenance.Active
                                                snapshot
                                    }
                                MatchedReference = None
                                RelatedCases = RelatedCaseLinks.empty
                                FamilySequence = familySequence
                                Snapshot = snapshot
                                LatestPayments = latestPayments
                                PaymentPayees = payees
                                Stages = stages
                                Disposition = None
                                NextActions = []
                                Notes =
                                    {
                                        Items = []
                                        Count = 0L
                                        CanAdd = false
                                    }
                                Parties = parties
                                PartyOffers = partyOffers
                                ChangedAt = changedAt
                                RecordedBy = recordedBy
                                Headline =
                                    CaseHeadline.ofState (CaseWorkReads.facts record work).State
                                Attention =
                                    CaseAttention.at
                                        work.Basis.BusinessDate
                                        (CaseWorkReads.facts record work)
                                Advice =
                                    advice
                                    |> CaseAdvice.withOffers controls
                                    |> CaseAdvice.duringMaintenance maintenance
                                Mine =
                                    match actor.Actor, work.Responsible with
                                    | Some requester, Some assigned ->
                                        requester.ActorId = assigned.ActorId
                                    | _ -> false
                            }
            | _ -> return Error CoreFault.StoreIntegrityError
        }

    let installationOffers principal actor =
        Registry.clientDeclarations
        |> List.choose (fun declaration ->
            if
                declaration.Creates
                && (declaration.Target = ActSubject.Case || declaration.Target = ActSubject.People)
                && declaration.Capabilities
                   |> List.forall (fun capability ->
                       Access.can principal actor capability Scope.Installation)
            then
                match declaration.Target with
                | ActSubject.Case -> Some InstallationOffer.Open
                | ActSubject.People -> Some(InstallationOffer.PeopleAct declaration.Id)
                | _ -> None
            else
                None)

    let actorOffers principal actor (maintenance: RegisterMaintenance) =
        if
            maintenance.Active
            || not (Access.can principal actor Capability.ManageAccess Scope.Installation)
        then
            []
        else
            Registry.clientDeclarations
            |> List.filter (fun declaration ->
                declaration.Target = ActSubject.People && not declaration.Creates)
            |> List.map _.Id

    let views principal actor =
        [
            if Access.visibility principal actor |> Option.isSome then
                View.Cases
            if Access.can principal actor Capability.ReadAccess Scope.Installation then
                View.People
            if Access.validActor principal actor then
                View.Operations
        ]
