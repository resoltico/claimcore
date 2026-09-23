namespace ClaimCore.Web

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open Microsoft.Win32.SafeHandles
open ClaimCore.HostSecurity
open ClaimCore.Contracts

[<NoEquality; NoComparison>]
type BootstrapCredential = { Path: string; Digest: byte array }

[<Sealed>]
type StateDirectoryLease internal (handle: SafeFileHandle) =
    interface IDisposable with
        member _.Dispose() = handle.Dispose()

[<Sealed>]
type BootstrapCredentialLease internal (credential: BootstrapCredential) =
    member _.Credential = credential

    interface IDisposable with
        member _.Dispose() =
            CryptographicOperations.ZeroMemory(credential.Digest)
            PrivateFileService.deletePrivate credential.Path |> ignore

[<NoEquality; NoComparison>]
type private Session =
    {
        AbsoluteExpiry: DateTimeOffset
        mutable IdleExpiry: DateTimeOffset
    }

[<Sealed>]
type SessionRegistry(idleLifetime: TimeSpan, absoluteLifetime: TimeSpan) =
    do
        if idleLifetime <= TimeSpan.Zero || absoluteLifetime < idleLifetime then
            invalidArg (nameof idleLifetime) "Session lifetimes must be positive and ordered."

    let gate = obj ()
    let sessions = Dictionary<string, Session>(StringComparer.Ordinal)

    let removeExpired now =
        sessions
        |> Seq.filter (fun entry ->
            entry.Value.AbsoluteExpiry <= now || entry.Value.IdleExpiry <= now)
        |> Seq.map (fun entry -> entry.Key)
        |> Seq.toArray
        |> Array.iter (fun id -> sessions.Remove(id) |> ignore)

    member _.Create(now: DateTimeOffset) =
        let id = Guid.NewGuid().ToString("N")

        let session =
            {
                AbsoluteExpiry = now.Add(absoluteLifetime)
                IdleExpiry = now.Add(idleLifetime)
            }

        lock gate (fun () ->
            removeExpired now

            if sessions.TryAdd(id, session) then
                id
            else
                invalidOp "A session identifier collision occurred.")

    member _.IsCurrent(id: string, now: DateTimeOffset) =
        lock gate (fun () ->
            match sessions.TryGetValue(id) with
            | true, session when session.AbsoluteExpiry > now && session.IdleExpiry > now ->
                session.IdleExpiry <- min session.AbsoluteExpiry (now.Add(idleLifetime))
                true
            | true, _ ->
                sessions.Remove(id) |> ignore
                false
            | false, _ -> false)

    member _.Revoke(id: string) =
        lock gate (fun () -> sessions.Remove(id) |> ignore)

    member _.RevokeAll() = lock gate sessions.Clear

    member internal _.StoredCount = lock gate (fun () -> sessions.Count)

module Security =
    let private credentialName = "bootstrap-credential"

    let private exactCredentialName (name: string) =
        let prefix = credentialName + "-"

        name.StartsWith(prefix, StringComparison.Ordinal)
        && name.Length = prefix.Length + 32
        && (name.Substring(prefix.Length)
            |> Seq.forall (fun value ->
                ('0' <= value && value <= '9') || ('a' <= value && value <= 'f')))

    let acquireStateDirectory (stateDirectory: string) =
        let path = Path.Combine(stateDirectory, ".claimcore-web.lock")

        match PrivateFileService.openExclusive path with
        | Ok stream -> new StateDirectoryLease(stream)
        | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.StateLockRefused

    let private removeStaleCredentials (stateDirectory: string) =
        for path in Directory.EnumerateFileSystemEntries(stateDirectory) do
            let name = Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue ""

            if exactCredentialName name then
                match PrivateFileService.deletePrivate path with
                | Ok() -> ()
                | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.BootstrapRemovalFailed

    let private createBootstrapCredential stateDirectory =
        let path =
            Path.Combine(stateDirectory, credentialName + "-" + Guid.NewGuid().ToString("N"))

        let random = RandomNumberGenerator.GetBytes(32)
        let secret = Convert.ToBase64String(random)
        CryptographicOperations.ZeroMemory(Span<byte>(random))
        let credentialBytes = Encoding.UTF8.GetBytes(secret)
        let fileBytes = Encoding.UTF8.GetBytes(secret + Environment.NewLine)

        try
            match PrivateFileService.writeNew 128 path fileBytes with
            | Error _ -> WebStartupDiagnostics.refuse WebStartupProblem.BootstrapWriteFailed
            | Ok() ->
                {
                    Path = path
                    Digest = SHA256.HashData(credentialBytes)
                }
        finally
            CryptographicOperations.ZeroMemory(Span<byte>(credentialBytes))
            CryptographicOperations.ZeroMemory(Span<byte>(fileBytes))

    let rotateBootstrapCredential stateDirectory =
        removeStaleCredentials stateDirectory

        createBootstrapCredential stateDirectory
        |> fun value -> new BootstrapCredentialLease(value)

    let isBootstrapCredential valid (submitted: string) =
        let candidate = SHA256.HashData(Encoding.UTF8.GetBytes(submitted))
        CryptographicOperations.FixedTimeEquals(valid.Digest, candidate)

    let sessionId (claims: seq<System.Security.Claims.Claim>) =
        claims
        |> Seq.tryFind (fun claim -> claim.Type = "claimcore-session")
        |> Option.map _.Value
