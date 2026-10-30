namespace ClaimCore.Cli

open System
open System.Threading
open System.Threading.Tasks

/// One serialized grant lifecycle, shared by all commands; the owner stops commands before disposal.
type internal MemoryTokenSession
    (
        acquire: CancellationToken -> Task<Result<AuthorizationGrant, AccessFailure>>,
        refresh:
            AuthorizationGrant
                -> CancellationToken
                -> Task<Result<AuthorizationGrant, AccessFailure>>
    ) =
    let gate = new SemaphoreSlim(1, 1)
    let mutable current: AuthorizationGrant option = None

    let obtain token =
        task {
            let! result = acquire token
            current <- result |> Result.toOption
            return result |> Result.map _.Access
        }

    let expired (grant: AuthorizationGrant) (token: CancellationToken) =
        task {
            current <- None

            match grant.Refresh with
            | None -> return! obtain token
            | Some _ ->
                match! refresh grant token with
                | Ok renewed ->
                    current <- Some renewed
                    return Ok renewed.Access
                | Error _ when token.IsCancellationRequested ->
                    return Error AccessFailure.OidcTokenUnavailable
                | Error _ -> return! obtain token
        }

    member _.Token(token: CancellationToken) =
        task {
            do! gate.WaitAsync token

            try
                match current with
                | Some grant when grant.Access.CanReuse -> return Ok grant.Access
                | Some grant -> return! expired grant token
                | None -> return! obtain token
            finally
                gate.Release() |> ignore
        }

    member _.Discard(token: CancellationToken) =
        task {
            do! gate.WaitAsync token

            try
                current <- None
            finally
                gate.Release() |> ignore
        }

    interface IDisposable with
        member _.Dispose() =
            current <- None
            gate.Dispose()
