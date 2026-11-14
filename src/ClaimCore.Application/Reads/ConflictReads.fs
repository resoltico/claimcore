namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Revision explanations are read before the action snapshot ends and never retain historical disclosure rights.
module internal ConflictReads =
    let private explain
        (services: ServiceContext)
        (tx: IReadTx)
        (actor: AccessView)
        (parent: StoredCase option)
        entity
        expected
        (conflict: RevisionConflictInfo)
        =
        task {
            match parent, actor.Actor with
            | Some parent, Some requester when
                Access.can
                    services.Principal
                    actor
                    Capability.ReadHistory
                    (Scope.Case parent.CaseId)
                ->
                let range =
                    {
                        Entity = entity
                        ExpectedRevision = expected
                        ActualRevision = conflict.ActualRevision
                    }

                let! page =
                    tx.TimelinePage
                        {
                            CaseId = parent.CaseId
                            Filter = None
                            Range = Some range
                            BeforePosition = None
                            Limit = Paging.MaximumTimelinePageSize
                        }

                if
                    not (
                        TimelineHistory.validPage
                            parent.CaseId
                            None
                            (Some range)
                            None
                            Paging.MaximumTimelinePageSize
                            page
                    )
                then
                    return Error CoreFault.StoreIntegrityError
                else
                    let full =
                        Access.can
                            services.Principal
                            actor
                            Capability.ReadHistoryFull
                            (Scope.Case parent.CaseId)

                    let erased = CaseState.lifecycle parent.Snapshot.State = Lifecycle.Erased
                    let! values = TimelineHistory.entries services tx erased full (Some range) page

                    return
                        values
                        |> Result.map (fun values ->
                            let scope =
                                TimelineHistory.binding parent.CaseId requester.ActorId None

                            let cursor =
                                page.Next
                                |> Option.map (
                                    Cursors.encodeTimeline
                                        services.InstallationId
                                        scope
                                        (Some range)
                                )

                            RevisionConflictInfo.explain
                                (ConflictChanges.Page
                                    {
                                        Basis = ServiceContext.basis services tx.ObservedAt
                                        Disclosure = HistoryDisclosure.ofAccess erased full
                                        Entries = values
                                        NextCursor = cursor
                                    })
                                conflict)
            | _ -> return Ok(RevisionConflictInfo.explain ConflictChanges.Withheld conflict)
        }

    let refusal services tx actor parent entity expected refusal =
        task {
            match refusal with
            | Refusal.RevisionConflict conflict when conflict.Changes.IsNone ->
                match Revision.create expected with
                | None -> return Error CoreFault.StoreIntegrityError
                | Some expected ->
                    let! result = explain services tx actor parent entity expected conflict
                    return result |> Result.map Refusal.RevisionConflict
            | _ -> return Ok refusal
        }

    let verdict services tx actor parent entity expected verdict =
        task {
            match verdict with
            | Verdict.Refuse rejected ->
                let! result = refusal services tx actor parent entity expected rejected
                return result |> Result.map Verdict.Refuse |> Result.defaultWith Verdict.Fail
            | _ -> return verdict
        }
