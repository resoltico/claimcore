module ClaimCore.TestSupport.SessionResults

open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification

/// Storage tests can inspect their value independently of host-owned session observations.
let value (pending: Task<SessionChanges<'value>>) =
    task {
        let! changes = pending
        return changes.Value
    }

let principal =
    ClaimCore.Domain.PrincipalKey.human "https://issuer.example/" "synthetic"
    |> Result.toOption
    |> Option.get

let stored payload =
    {
        StoredSessionTicket.PrincipalKey = ClaimCore.Domain.PrincipalKey.naturalKey principal
        Payload = payload
    }

let payload (pending: Task<SessionChanges<StoredSessionTicket option>>) =
    task {
        let! changes = pending
        return changes.Value |> Option.map _.Payload
    }
