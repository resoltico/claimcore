namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Stored work retains identities; this read projection accepts only snapshot-resolved current refs.
module internal CaseWorkReadModels =
    let snapshot (accounts: Map<CaseId * ActorId, ActorRef>) (snapshot: CaseWorkSnapshot) =
        let responsible =
            match snapshot.Work.Responsible with
            | None -> Ok None
            | Some identity ->
                match Map.tryFind (snapshot.CaseId, identity) accounts with
                | Some actor when actor.ActorId = identity -> Ok(Some actor)
                | _ -> Error CoreFault.StoreIntegrityError

        responsible
        |> Result.map (fun actor ->
            {
                CaseWorkReadModel.CaseId = snapshot.CaseId
                Revision = snapshot.Revision
                Responsible = actor
                Summary = snapshot.Work.Summary
            })
