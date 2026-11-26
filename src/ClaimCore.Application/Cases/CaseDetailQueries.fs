namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Authorizes the read and resolves its result within one store snapshot.
module internal CaseDetailQueries =
    open QueryResults

    let caseResult
        (services: ServiceContext)
        (tx: ReadTx)
        (view: CaseReadFacts)
        work
        stages
        changedAt
        recordedBy
        payments
        =
        task {
            if
                not (
                    CaseRecords.authorize
                        services.Principal
                        view.Actor
                        Capability.ReadCase
                        view.Case
                )
            then
                return unavailable
            else
                match view.Case with
                | None -> return Outcome.Failed CoreFault.StoreIntegrityError
                | Some record ->
                    let! model =
                        Advice.caseRecord
                            services
                            tx
                            view.Actor
                            view.Maintenance
                            view.FamilySequence
                            work
                            stages
                            changedAt
                            recordedBy
                            payments
                            record

                    return model |> succeeded
        }

    let private stageDisplay _installation (tx: ReadTx) (record: StoredCase) =
        task {
            let! facts = FamilyReads.caseStageFacts tx record.CaseId

            match facts with
            | Some facts when
                facts.Revision = record.Snapshot.Revision && facts.Snapshot = record.Snapshot
                ->
                let! authors = PeopleReads.actorRefs tx [ facts.RecordedBy ]

                let! paidDates =
                    PaymentStorage.latestPaidDates
                        tx.Connection
                        [ record.CaseId ]
                        tx.CancellationToken

                return
                    match
                        CaseStages.describe
                            record.Snapshot.State
                            (Some facts.OpenedOn)
                            facts.ClosedOn
                            (Map.tryFind record.CaseId paidDates),
                        Map.tryFind facts.RecordedBy authors,
                        Ok facts.Disposition
                    with
                    | Ok stages, Some author, Ok disposition when author.ActorId = facts.RecordedBy ->
                        Ok(stages, disposition, facts.RecordedAt, author)
                    | _ -> Error CoreFault.StoreIntegrityError
            | _ -> return Error CoreFault.StoreIntegrityError
        }

    let private getInSnapshot
        (services: ServiceContext)
        selector
        (tx: ReadTx)
        (view: CaseReadFacts)
        =
        task {
            match view.Case with
            | Some record when
                CaseRecords.authorize services.Principal view.Actor Capability.ReadCase view.Case
                ->
                let! work =
                    CaseWorkReads.view tx (ServiceContext.basis services tx.ObservedAt) record

                let! stages = stageDisplay services.InstallationId tx record

                match work, stages with
                | Ok work, Ok(stages, disposition, changedAt, recordedBy) ->
                    let! nextActions =
                        CaseWorkReads.nextActions
                            services
                            tx
                            work
                            view.Actor
                            view.Maintenance.Active
                            record

                    let! notes = NoteQueries.current services tx view record None 3

                    let! payments =
                        match CaseState.fields record.Snapshot.State with
                        | None -> System.Threading.Tasks.Task.FromResult []
                        | Some _ ->
                            PaymentReads.latestPayments
                                tx
                                (record.CaseId, PaymentPaging.PreviewLimit)

                    let count =
                        CaseState.fields record.Snapshot.State
                        |> Option.map (fun fields -> fields.Finance.PaymentCount)
                        |> Option.defaultValue 0L

                    let validPayments =
                        payments.Length = int (min (int64 PaymentPaging.PreviewLimit) count)

                    match nextActions, notes with
                    | Error fault, _
                    | _, Error fault -> return Outcome.Failed fault
                    | Ok _, Ok _ when not validPayments ->
                        return Outcome.Failed CoreFault.StoreIntegrityError
                    | Ok items, Ok notes ->
                        let! relatedCases = CaseLinkReads.read services tx view record

                        let! matchedReference =
                            CaseIdentityReads.matchedEarlier
                                tx
                                record.CaseId
                                record.Snapshot.Reference
                                selector

                        let! currentResult =
                            caseResult services tx view work stages changedAt recordedBy payments

                        return
                            currentResult
                            |> Outcome.fold
                                (fun current ->
                                    Outcome.Succeeded
                                        { current with
                                            MatchedReference = matchedReference
                                            RelatedCases = relatedCases
                                            Disposition = disposition
                                            NextActions = items
                                            Notes = notes.Notes
                                        })
                                Outcome.Rejected
                                Outcome.Failed
                | Error fault, _
                | _, Error fault -> return Outcome.Failed fault
            | _ -> return unavailable
        }

    let get (services: ServiceContext) selector token =
        task {
            match CaseRecords.validateSelector selector with
            | Error refusal -> return! RequestRefusals.read services token refusal
            | Ok _ ->
                let! result =
                    services.Store.Read
                        {
                            Work =
                                fun tx ->
                                    task {
                                        let! view = CaseReadContext.load services tx selector
                                        let! outcome = getInSnapshot services selector tx view

                                        let caseId =
                                            DisclosureReads.target
                                                selector
                                                (view.Case |> Option.map _.CaseId)

                                        return
                                            DisclosureReads.observe
                                                services.Principal
                                                view.Actor
                                                DisclosureKind.CaseRead
                                                caseId
                                                None
                                                outcome
                                    }
                            CancellationToken = token
                        }

                return! DisclosureReads.complete services token result
        }
