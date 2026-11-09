namespace ClaimCore.Application

open ClaimCore.RecordFormat
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Current names are bounded, authorized and read from the same snapshot as receipt disclosure.
module internal ReceiptReads =
    let enrich (services: ServiceContext) (tx: IReadTx) actor current entries =
        task {
            match current with
            | ReceiptCurrent.Work(parent, work, _) ->
                let relations =
                    LedgerEntries.workRelations services.Principal actor parent work entries

                let! accounts = tx.AssignedAccounts relations
                return Ok(ReceiptCurrent.Work(parent, work, accounts))
            | ReceiptCurrent.Authority _ when
                Authority.can services.Principal actor Capability.ReadAuthority Scope.Installation
                ->
                let identities =
                    entries
                    |> List.choose (fun (entry: ValidatedEntry) ->
                        match entry.Entry.Subject with
                        | EntryTarget.Authority(target, _) -> Some target
                        | _ -> None)
                    |> List.distinct

                match identities with
                | [ identity ] ->
                    let! actors = tx.ActorRefs [ identity ]

                    match Map.tryFind identity actors with
                    | Some actor when actor.ActorId = identity ->
                        return Ok(ReceiptCurrent.Authority(Some actor))
                    | _ -> return Error CoreFault.StoreIntegrityError
                | _ -> return Error CoreFault.StoreIntegrityError
            | ReceiptCurrent.Authority _ -> return Ok(ReceiptCurrent.Authority None)
            | _ -> return Ok current
        }

    let existing (services: ServiceContext) tx actor current (operation: RetainedOperation option) =
        task {
            match operation with
            | Some operation when
                LedgerEntries.canObserve
                    services.Principal
                    actor
                    (LedgerEntries.primary operation).Subject
                || (Authority.validActor services.Principal actor
                    && (actor.Actor
                        |> Option.exists (fun owner -> owner.ActorId = operation.ActorId)))
                ->
                match LedgerEntries.validateOperation services.InstallationId operation with
                | Error fault -> return Error fault
                | Ok validated ->
                    return! enrich services tx actor current validated.ValidatedEntries
            | _ -> return Ok current
        }
