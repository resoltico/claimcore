namespace ClaimCore.Application.Reads

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage

/// Callers admit the action/history scope before requesting its related current labels.
module internal ChangeReferenceReads =
    let read (tx: ReadTx) aggregate descriptions =
        task {
            let actors = ResizeArray<ActorRef>()
            let cases = ResizeArray<CaseReferenceItem>()

            for id in PartDescriptions.relatedActors descriptions do
                let! person = PersonAcquisition.person tx id

                match person with
                | None -> raise (RecordCorruptException "Related account is absent.")
                | Some person ->
                    let availability =
                        if not person.Actor.Enabled then
                            ActorAvailability.Disabled
                        else
                            match aggregate with
                            | AggregateReference.Family caseId when
                                not (
                                    Access.eligibleAssignee
                                        person.Actor.Principal
                                        (Person.access person |> Some |> AccessView.ofActor)
                                        caseId
                                )
                                ->
                                ActorAvailability.NotEligible
                            | _ -> ActorAvailability.Available

                    actors.Add(Actor.reference availability person.Actor)

            for id in PartDescriptions.relatedCases descriptions do
                let! rows =
                    DatabaseSession.rows
                        tx.Connection
                        "SELECT case_reference FROM claimcore.cases WHERE case_id=$1"
                        [| box (CaseId.value id) |]
                        tx.CancellationToken

                match rows with
                | [ row ] ->
                    let reference =
                        DatabaseSession.value<string> 0 row
                        |> CaseReference.parse
                        |> Result.defaultWith (fun _ ->
                            raise (RecordCorruptException "Related case reference is invalid."))

                    cases.Add { CaseId = id; Reference = reference }
                | _ ->
                    raise (RecordCorruptException "Related case identity is absent or duplicated.")

            return
                {
                    ChangeReferences.Actors = List.ofSeq actors
                    Cases = List.ofSeq cases
                }
        }
