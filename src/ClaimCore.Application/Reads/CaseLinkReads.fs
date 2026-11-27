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
                    |> Option.filter (fun row -> row.Snapshot.State <> CaseState.Erased)
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
            let! visibilityActor = AccessReads.access tx principal None

            match Access.visibility principal visibilityActor, view.Actor.Actor with
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
                    CaseFamilyAcquisition.familyForDecision
                        tx
                        record.CaseId
                        (Binding.LinkNew target)
                        None

                let facts = family |> Option.map _.Facts
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
                        RelatedCase = fun _ -> None
                    }

                let available id binding =
                    match Registry.find id with
                    | Some(Task.Family task) ->
                        (task.Availability environment facts binding).Blocked.IsNone
                    | _ -> invalidOp "A related case operation requires a family task."

                return
                    {
                        RelatedCaseLinks.Outgoing =
                            outgoing
                            |> List.rev
                            |> List.map (fun item ->
                                { item with
                                    CanRemove =
                                        available
                                            ClientCompositions.linkRemoval
                                            (Binding.Link(target, item.Id))
                                })
                        Incoming = incoming
                        IncomingCount = count
                        CanAdd = available ClientCompositions.linkEntry (Binding.LinkNew target)
                    }
            | _ -> return RelatedCaseLinks.empty
        }
