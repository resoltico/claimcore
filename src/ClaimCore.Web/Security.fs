namespace ClaimCore.Web

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.Postgres
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http

/// Cookies contain only a protected random lookup key; PostgreSQL owns ticket deadlines.
[<Sealed>]
type internal OidcTicketStore
    (
        idle: TimeSpan,
        absolute: TimeSpan,
        storage: ISessionStore,
        protection: IDataProtectionProvider,
        diagnose: string -> unit
    ) =
    let protector = protection.CreateProtector("ClaimCore.Web.DatabaseSessionTicket.1")

    let protect ticket =
        let clear = TicketSerializer.Default.Serialize ticket

        try
            if clear.Length > 524288 then
                invalidOp "The OIDC session ticket is too large."

            protector.Protect clear
        finally
            CryptographicOperations.ZeroMemory clear

    // The persisted envelope has its own purpose. Random row keys never contain identity data.
    let decode bytes =
        let clear = protector.Unprotect bytes

        try
            if clear.Length > 524288 then
                raise (InvalidDataException "Invalid session ticket.")

            TicketSerializer.Default.Deserialize clear |> Option.ofObj
        finally
            CryptographicOperations.ZeroMemory clear

    let report code =
        try
            diagnose code
        with
        | :? IOException
        | :? ObjectDisposedException -> ()

    let unavailable () = report "OIDC_SESSION_UNAVAILABLE"
    let unreadable () = report "OIDC_SESSION_UNREADABLE"

    let store ticket token =
        task {
            // Protect before creation so no cookie can escape without a committed ticket.
            let payload = protect ticket
            let expiry = ticket.Properties.ExpiresUtc |> Option.ofNullable

            try
                let! changes = storage.Create(payload, expiry, idle, absolute, token)
                let key = WebMetrics.sessionChanges changes
                WebMetrics.sessionStarted ()
                return key
            with _ ->
                unavailable ()
                return raise (InvalidOperationException "OIDC session storage unavailable.")
        }

    let renew key ticket token =
        task {
            try
                let! changes = storage.Renew(key, protect ticket, token)
                WebMetrics.sessionChanges changes
            with _ ->
                unavailable ()
                return raise (InvalidOperationException "OIDC session storage unavailable.")
        }

    let retrieve key token : Task<AuthenticationTicket | null> =
        task {
            try
                let! changes = storage.Retrieve(key, token)
                let stored = WebMetrics.sessionChanges changes

                match stored with
                | None -> return null
                | Some bytes ->
                    try
                        match decode bytes with
                        | Some ticket -> return (ticket: AuthenticationTicket | null)
                        | None ->
                            let! changes = storage.Remove(key, SessionEndReason.Invalid, token)
                            WebMetrics.sessionChanges changes
                            unreadable ()
                            return null
                    with
                    | :? CryptographicException
                    | :? InvalidDataException
                    | :? EndOfStreamException
                    | :? FormatException
                    | :? OverflowException
                    | :? ArgumentException ->
                        let! changes = storage.Remove(key, SessionEndReason.Invalid, token)
                        WebMetrics.sessionChanges changes
                        unreadable ()
                        return null
            with
            | :? AdmissionRejectedException as error -> return raise error
            | _ ->
                unavailable ()
                return null
        }

    let remove key token =
        task {
            try
                let! changes = storage.Remove(key, SessionEndReason.Logout, token)
                WebMetrics.sessionChanges changes
            with _ ->
                unavailable ()
                return raise (InvalidOperationException "OIDC session storage unavailable.")
        }

    let withContext (context: HttpContext) token action =
        task {
            use linked =
                CancellationTokenSource.CreateLinkedTokenSource(token, context.RequestAborted)

            return! action linked.Token
        }

    interface ITicketStore with
        member _.StoreAsync(ticket) = store ticket CancellationToken.None
        member _.StoreAsync(ticket, token) = store ticket token

        member _.StoreAsync(ticket, context, token) =
            withContext context token (store ticket)

        member _.RetrieveAsync(key) = retrieve key CancellationToken.None
        member _.RetrieveAsync(key, token) = retrieve key token

        member _.RetrieveAsync(key, context, token) =
            withContext context token (retrieve key)

        member _.RenewAsync(key, ticket) =
            renew key ticket CancellationToken.None :> Task

        member _.RenewAsync(key, ticket, token) = renew key ticket token :> Task

        member _.RenewAsync(key, ticket, context, token) =
            withContext context token (renew key ticket) :> Task

        member _.RemoveAsync(key) =
            remove key CancellationToken.None :> Task

        member _.RemoveAsync(key, token) = remove key token :> Task

        member _.RemoveAsync(key, context, token) =
            withContext context token (remove key) :> Task
