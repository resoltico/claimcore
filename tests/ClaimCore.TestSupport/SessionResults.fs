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
