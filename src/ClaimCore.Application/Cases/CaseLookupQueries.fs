namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Authorizes the read and resolves its result within one store snapshot.
module internal CaseLookupQueries =
    open QueryResults

    let assignees (services: ServiceContext) (request: CaseLookupRequest) token =
        task {
            if not (validLimit request.Limit) then
                return! RequestRefusals.invalidLimit services token
            else
                match CaseRecords.validateSelector request.Target with
                | Error refusal -> return! RequestRefusals.read services token refusal
                | Ok() ->
                    let! result =
                        services.Store.Read
                            {
                                Work =
                                    fun tx ->
                                        task {
                                            let! record = CaseReadContext.find tx request.Target

                                            let! actor =
                                                AccessReads.access
                                                    tx
                                                    (services.Principal)
                                                    (record |> Option.map _.CaseId)

                                            match record with
                                            | Some record when
                                                Access.can
                                                    services.Principal
                                                    actor
                                                    Capability.ReadCase
                                                    (Scope.Case record.CaseId)
                                                && Access.can
                                                    services.Principal
                                                    actor
                                                    Capability.ManageCaseWork
                                                    (Scope.Case record.CaseId)
                                                && (CaseState.fields record.Snapshot.State).IsSome
                                                ->
                                                match
                                                    cursor
                                                        (Cursors.decodeAssignees
                                                            services.InstallationId
                                                            record.CaseId)
                                                        request.AfterCursor
                                                with
                                                | Error refusal -> return Outcome.Rejected refusal
                                                | Ok after ->
                                                    let! work = WorkReads.findWork tx record.CaseId

                                                    match work with
                                                    | Some work when
                                                        work.CaseId = record.CaseId
                                                        && (work.Snapshot
                                                            |> Option.exists (fun snapshot ->
                                                                snapshot.Revision = work.Revision))
                                                        ->
                                                        let! page =
                                                            AccessReads.assigneePage
                                                                tx
                                                                {
                                                                    CaseId = record.CaseId
                                                                    AfterActorId = after
                                                                    Limit = request.Limit
                                                                }

                                                        if page.Items.Length > request.Limit then
                                                            return
                                                                Outcome.Failed
                                                                    CoreFault.StoreIntegrityError
                                                        else
                                                            return
                                                                Outcome.Succeeded
                                                                    {
                                                                        AssigneePage.CaseId =
                                                                            record.CaseId
                                                                        WorkRevision = work.Revision
                                                                        Basis =
                                                                            ServiceContext.basis
                                                                                services
                                                                                tx.ObservedAt
                                                                        Items = page.Items
                                                                        NextCursor =
                                                                            page.Next
                                                                            |> Option.map (
                                                                                Cursors
                                                                                    .encodeAssignees
                                                                                    services
                                                                                        .InstallationId
                                                                                    record.CaseId
                                                                            )
                                                                    }
                                                    | _ ->
                                                        return
                                                            Outcome.Failed
                                                                CoreFault.StoreIntegrityError
                                            | _ -> return unavailable
                                        }
                                CancellationToken = token
                            }

                    return readResult result
        }

    let parties (services: ServiceContext) (request: CaseLookupRequest) token =
        task {
            if not (validLimit request.Limit) then
                return! RequestRefusals.invalidLimit services token
            else
                match CaseRecords.validateSelector request.Target with
                | Error refusal -> return! RequestRefusals.read services token refusal
                | Ok() ->
                    let! result =
                        services.Store.Read
                            {
                                CancellationToken = token
                                Work =
                                    fun tx ->
                                        task {
                                            let! record = CaseReadContext.find tx request.Target

                                            let! actor =
                                                AccessReads.access
                                                    tx
                                                    services.Principal
                                                    (record |> Option.map _.CaseId)

                                            match record with
                                            | Some record when
                                                CaseRecords.authorize
                                                    services.Principal
                                                    actor
                                                    Capability.ReadCase
                                                    (Some record)
                                                && Access.can
                                                    services.Principal
                                                    actor
                                                    Capability.RecordPayment
                                                    (Scope.Case record.CaseId)
                                                ->
                                                let decode =
                                                    Cursors.decodeParties
                                                        services.InstallationId
                                                        record.CaseId

                                                match cursor decode request.AfterCursor with
                                                | Error refusal -> return Outcome.Rejected refusal
                                                | Ok after ->
                                                    let! page =
                                                        PartyStorage.choicePage
                                                            tx
                                                            record.CaseId
                                                            after
                                                            request.Limit

                                                    return
                                                        Outcome.Succeeded
                                                            {
                                                                PartyChoicePage.Basis =
                                                                    ServiceContext.basis
                                                                        services
                                                                        tx.ObservedAt
                                                                Items = page.Items
                                                                NextCursor =
                                                                    page.Next
                                                                    |> Option.map (
                                                                        Cursors.encodeParties
                                                                            services.InstallationId
                                                                            record.CaseId
                                                                    )
                                                            }
                                            | _ -> return unavailable
                                        }
                            }

                    return readResult result
        }
