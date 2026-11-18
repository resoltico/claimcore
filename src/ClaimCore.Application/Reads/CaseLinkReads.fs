namespace ClaimCore.Application.Reads

open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Tasks

module internal CaseLinkReads =
    let private related (tx: ReadTx) principal id =
        task {
            let! access = AccessReads.access tx principal (Some id)

            if Access.can principal access Capability.ReadCase (Scope.Case id) then
                let! stored = FamilyReads.findCase tx id

                return
                    stored
                    |> Option.map (fun row ->
                        {
                            RelatedCaseTarget.CaseId = id
                            Reference = row.Snapshot.Reference
                            Lifecycle = CaseState.lifecycle row.Snapshot.State
                            Status = row.Index.Status
                        })
            else
                return None
        }

    let read (services: ServiceContext) (tx: ReadTx) (view: CaseReadFacts) (record: StoredCase) =
        task {
            let principal = services.Principal
            let target = CaseSelector.Id record.CaseId

            match Access.visibility principal view.Actor, view.Actor.Actor with
            | Some visibility, Some actor ->
                let! incoming, count =
                    CaseLinkStorage.incoming
                        tx
                        {
                            DateBasis = ServiceContext.basis services tx.ObservedAt
                            Visibility = visibility
                            ActorId = actor.ActorId
                            ReadRoles = Access.readRoles principal
                            ForbiddenRoles = Access.invalidRoles principal
                            Filter = CaseListFilter.empty
                        }
                        record.CaseId

                let! family =
                    AggregateStorage.familyForDecision
                        tx
                        record.CaseId
                        (Binding.LinkNew target)
                        None

                let facts = family |> Option.map _.Facts
                let mutable targets = Map.empty
                let mutable outgoing = []

                for id, row in
                    facts
                    |> Option.map (fun facts -> facts.Links |> Map.toList)
                    |> Option.defaultValue []
                    |> List.sortBy (fun (_, row) -> row.Stamp.Position) do
                    match row.Value.Content with
                    | None -> ()
                    | Some content ->
                        let! item = related tx principal content.Target
                        targets <- Map.add content.Target item targets

                        outgoing <-
                            ({
                                RelatedCaseRead.Id = id
                                Case = item
                                Note = if item.IsSome then content.Note else None
                                CanRemove = false
                            })
                            :: outgoing

                let environment =
                    {
                        FamilyEnvironment.Requester = view.Actor
                        RequesterPrincipal = principal
                        Maintenance = view.Maintenance.Active
                        AssigneeEligible = fun _ -> false
                        ReferenceOwner = fun _ -> None
                        RelatedCase =
                            function
                            | CaseSelector.Id id ->
                                targets
                                |> Map.tryFind id
                                |> Option.flatten
                                |> Option.map (fun value -> id, value.Lifecycle)
                            | CaseSelector.Reference _ -> None
                    }

                return
                    {
                        RelatedCaseLinks.Outgoing =
                            outgoing
                            |> List.rev
                            |> List.map (fun item ->
                                let offer =
                                    Link.Remove.availability
                                        environment
                                        facts
                                        (Binding.Link(target, item.Id))

                                { item with
                                    CanRemove = offer.Blocked.IsNone
                                })
                        Incoming = incoming
                        IncomingCount = count
                        CanAdd =
                            (Link.Add.availability environment facts (Binding.LinkNew target))
                                .Blocked.IsNone
                    }
            | _ -> return RelatedCaseLinks.empty
        }
