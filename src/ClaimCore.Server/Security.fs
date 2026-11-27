namespace ClaimCore.Server

open System
open System.IO
open System.Globalization
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Domain
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http

type internal BrowserSignInRejectedException() =
    inherit Exception("Browser sign-in proof was refused.")

module internal BrowserTicket =
    let flowProperty = "ClaimCore.BrowserFlow"
    let idTokenProperty = "ClaimCore.IdToken"
    let private issueProperty = "ClaimCore.BrowserIssued"
    let private authenticationProperty = "ClaimCore.BrowserAuthenticated"

    let recordProof (properties: AuthenticationProperties) (proof: HumanAuthentication) =
        let seconds (instant: DateTimeOffset) =
            instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)

        properties.Items[issueProperty] <- seconds proof.IssuedAt
        properties.Items[authenticationProperty] <- seconds proof.AuthenticatedAt

    let takeProof (properties: AuthenticationProperties) =
        let take key =
            match properties.Items.TryGetValue key with
            | true, NonNull value ->
                properties.Items.Remove key |> ignore

                match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, seconds when seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ->
                    Some(DateTimeOffset.FromUnixTimeSeconds seconds)
                | _ -> None
            | _ -> None

        match take issueProperty, take authenticationProperty with
        | Some issued, Some authenticated ->
            Some
                {
                    IssuedAt = issued
                    AuthenticatedAt = authenticated
                }
        | _ -> None

    let private credentialProperty = "ClaimCore.BrowserCredential"
    let private requestScope = obj ()

    let markRequest key (context: HttpContext) =
        let digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key: string))
        context.Items[requestScope] <- Convert.ToHexStringLower digest

    let scope (context: HttpContext) =
        match context.Items.TryGetValue requestScope with
        | true, (:? string as value) -> value
        | _ -> ""

    let bind (properties: AuthenticationProperties) credential =
        properties.Items[credentialProperty] <-
            match credential with
            | BrowserCredential.StatusOnly -> "status"
            | BrowserCredential.Protected(installation, recovery, actor, generation) ->
                String.Join(
                    "/",
                    installation.ToString("D"),
                    recovery.ToString("D"),
                    ActorId.format actor,
                    (Sequence.value generation).ToString(CultureInfo.InvariantCulture)
                )

    let credential (properties: AuthenticationProperties) =
        match properties.Items.TryGetValue credentialProperty with
        | true, "status" -> Some BrowserCredential.StatusOnly
        | true, NonNull value ->
            match value.Split '/' with
            | [| installation; recovery; actor; generation |] ->
                match
                    Guid.TryParseExact(installation, "D"),
                    Guid.TryParseExact(recovery, "D"),
                    Guid.TryParseExact(actor, "D"),
                    Int64.TryParse(generation, NumberStyles.None, CultureInfo.InvariantCulture)
                with
                | (true, installation), (true, recovery), (true, actor), (true, generation) ->
                    match ActorId.create actor, Sequence.create generation with
                    | Some actor, Some generation ->
                        Some(BrowserCredential.Protected(installation, recovery, actor, generation))
                    | _ -> None
                | _ -> None
            | _ -> None
        | _ -> None

type internal BrowserAntiforgeryScope() =
    interface IAntiforgeryAdditionalDataProvider with
        member _.GetAdditionalData context = BrowserTicket.scope context

        member _.ValidateAdditionalData(context, issued) =
            String.Equals(issued, BrowserTicket.scope context, StringComparison.Ordinal)

/// Cookies contain only a protected random lookup key; PostgreSQL owns ticket deadlines.
[<Sealed>]
type internal OidcTicketStore
    (
        issuer: Uri,
        idle: TimeSpan,
        absolute: TimeSpan,
        storage: ISessionStore,
        protection: IDataProtectionProvider,
        diagnose: string -> unit
    ) =
    let protector =
        protection.CreateProtector("ClaimCore.Server.DatabaseSessionTicket.2")

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

    let principal (ticket: AuthenticationTicket) =
        PrincipalIdentity.fromBrowserSession issuer ticket.Principal
        |> Result.map AuthenticatedPrincipals.key
        |> Result.defaultWith (fun _ -> raise (InvalidDataException "Invalid session principal."))

    let associated expected credential bytes =
        try
            decode bytes
            |> Option.exists (fun ticket ->
                PrincipalKey.naturalKey (principal ticket) = expected
                && (BrowserTicket.credential ticket.Properties) = credential)
        with
        | :? CryptographicException
        | :? InvalidDataException
        | :? EndOfStreamException
        | :? FormatException
        | :? OverflowException
        | :? ArgumentException -> false

    let store (ticket: AuthenticationTicket) token =
        task {
            let login =
                match ticket.Properties.Items.TryGetValue BrowserTicket.flowProperty with
                | true, NonNull raw ->
                    match
                        Guid.TryParseExact(raw, "D"), BrowserTicket.takeProof ticket.Properties
                    with
                    | (true, flow), Some proof -> Some(flow, proof)
                    | _ -> raise (BrowserSignInRejectedException())
                | _ when ticket.Properties.Items.ContainsKey BrowserTicket.idTokenProperty ->
                    raise (BrowserSignInRejectedException())
                | _ -> None

            let seal credential =
                ticket.Properties.Items.Remove BrowserTicket.flowProperty |> ignore
                BrowserTicket.bind ticket.Properties credential
                protect ticket

            let expiry = ticket.Properties.ExpiresUtc |> Option.ofNullable

            try
                let! changes =
                    storage.Create(principal ticket, login, seal, expiry, idle, absolute, token)

                let key = WebMetrics.sessionChanges changes
                WebMetrics.sessionStarted ()
                return key
            with
            | :? System.Security.Authentication.AuthenticationException ->
                return raise (BrowserSignInRejectedException())
            | :? AdmissionRejectedException as error -> return raise error
            | _ ->
                unavailable ()
                return raise (InvalidOperationException "OIDC session storage unavailable.")
        }

    let renew key (ticket: AuthenticationTicket) token =
        task {
            try
                if ticket.Properties.Items.ContainsKey BrowserTicket.flowProperty then
                    raise (InvalidDataException "A new sign-in requires a new browser ticket.")

                let! changes =
                    storage.Renew(
                        key,
                        principal ticket,
                        protect ticket,
                        associated
                            (PrincipalKey.naturalKey (principal ticket))
                            (BrowserTicket.credential ticket.Properties),
                        token
                    )

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
                | Some stored ->
                    try
                        match decode stored.Payload with
                        | Some ticket when
                            PrincipalKey.naturalKey (principal ticket) = stored.PrincipalKey
                            && (BrowserTicket.credential ticket.Properties).IsSome
                            ->
                            return (ticket: AuthenticationTicket | null)
                        | _ ->
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

    member _.StartFlow token = storage.StartFlow token

    interface ITicketStore with
        member _.StoreAsync(ticket) = store ticket CancellationToken.None
        member _.StoreAsync(ticket, token) = store ticket token

        member _.StoreAsync(ticket, context, token) =
            task {
                let! key = withContext context token (store ticket)
                BrowserTicket.markRequest key context
                return key
            }

        member _.RetrieveAsync(key) = retrieve key CancellationToken.None
        member _.RetrieveAsync(key, token) = retrieve key token

        member _.RetrieveAsync(key, context, token) =
            task {
                let! ticket = withContext context token (retrieve key)

                if not (isNull ticket) then
                    BrowserTicket.markRequest key context

                return ticket
            }

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
