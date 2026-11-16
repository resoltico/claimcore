namespace ClaimCore.Application.Reads

open System
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage

type internal CaseReadFacts =
    {
        ObservedAt: DateTimeOffset
        Maintenance: RegisterMaintenance
        Actor: AccessView
        Case: StoredCase option
        FamilySequence: Sequence
    }

module internal CaseReadContext =
    let find (tx: ReadTx) =
        function
        | CaseSelector.Id id -> FamilyReads.findCase tx id
        | CaseSelector.Reference reference -> FamilyReads.findCaseByReference tx reference

    let load (services: ServiceContext) (tx: ReadTx) selector =
        task {
            let! record = find tx selector
            let! maintenance = RegisterReads.maintenance tx ()
            let! actor = AccessReads.access tx services.Principal (record |> Option.map _.CaseId)

            let! sequence =
                task {
                    match record with
                    | None -> return Sequence.zero
                    | Some record -> return! FamilyReads.caseFamilySequence tx record.CaseId
                }

            return
                {
                    ObservedAt = tx.ObservedAt
                    Maintenance = maintenance
                    Actor = actor
                    Case = record
                    FamilySequence = sequence
                }
        }
