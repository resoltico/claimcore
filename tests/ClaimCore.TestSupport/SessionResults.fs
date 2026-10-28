module ClaimCore.TestSupport.SessionResults

open System.Threading.Tasks
open ClaimCore.Postgres

/// Storage tests can inspect their value independently of host-owned session observations.
let value (pending: Task<SessionChanges<'value>>) =
    task {
        let! changes = pending
        return changes.Value
    }
