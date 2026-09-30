namespace ClaimCore.Witness

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes

/// Proof that catalog-derived admission checks already succeeded for exactly this catalog state.
///
/// Admission has two kinds of check. Liveness checks (server settings, the session role, the
/// installed baseline marker, installation identity) can change without a catalog write and run on
/// every checkout. Structural checks (the pinned catalog projection, the constraint policy, every
/// privilege answer) are a function of catalog state alone, and are the expensive ones. This module
/// runs the structural checks when the catalog has changed and otherwise proves, with one cheap
/// statement, that it has not.
///
/// The fast path can only skip work. Every doubt (an empty cache, a different token, an elapsed
/// interval, a token statement that fails) runs the caller's full verification, which raises what it
/// always raised. A token is stored only after the full verification succeeded, and it is the token
/// read before that verification began, so an unverified state is never remembered.
module internal CatalogEpoch =
    /// A full verification is forced at least this often, which bounds how long any change the
    /// token could theoretically miss can go unnoticed.
    let verificationInterval = TimeSpan.FromSeconds 60.0

    /// Bounds memory when a process opens many distinct databases (isolated test clusters).
    let private capacity = 256

    [<NoEquality; NoComparison>]
    type Verified =
        {
            Token: string
            VerifiedAtMilliseconds: int64
        }

    /// How often the fast path was taken and how often a full verification ran, for tests.
    [<NoEquality; NoComparison>]
    type Counters = { Skipped: int64; Verified: int64 }

    let private tokenSql =
        lazy
            (use stream =
                typeof<Verified>.Assembly.GetManifestResourceStream("ClaimCore.CatalogEpoch.sql")
                |> Option.ofObj
                |> Option.defaultWith (fun () ->
                    invalidOp "The catalog change token SQL is missing.")

             use buffer = new MemoryStream()
             stream.CopyTo(buffer)
             UTF8Encoding(false, true).GetString(buffer.ToArray()))

    let private remembered =
        ConcurrentDictionary<string, Verified>(StringComparer.Ordinal)

    let mutable private skipped = 0L
    let mutable private verified = 0L

    let counters () =
        {
            Skipped = Volatile.Read(&skipped)
            Verified = Volatile.Read(&verified)
        }

    /// Forget everything; the next checkout verifies in full. For tests and owner tooling.
    let forget () = remembered.Clear()

    /// The scope, server endpoint, database and login role: what a verification is a fact about.
    /// The password is not part of the identity, and the token itself carries the server start time
    /// and database, so a restarted or restored server cannot reuse an old verification.
    let private keyOf (scope: string) (connection: NpgsqlConnection) =
        $"{scope}|{connection.Host}|{connection.Port}|{connection.Database}|{connection.UserName}"

    let private isCurrent (interval: TimeSpan) (key: string) (token: string) =
        match remembered.TryGetValue(key) with
        | true, entry ->
            String.Equals(entry.Token, token, StringComparison.Ordinal)
            && Environment.TickCount64 - entry.VerifiedAtMilliseconds <
                int64 interval.TotalMilliseconds
        | _ -> false

    let private remember (key: string) (token: string) =
        if remembered.Count >= capacity then
            remembered.Clear()

        remembered[key] <-
            {
                Token = token
                VerifiedAtMilliseconds = Environment.TickCount64
            }

    let private tokenCommand (connection: NpgsqlConnection) (schema: string) =
        let command = new NpgsqlCommand(tokenSql.Value, connection)
        command.Parameters.AddWithValue("schema", NpgsqlDbType.Text, schema) |> ignore
        command

    let private asToken (value: obj | null) =
        match value with
        | :? string as token -> Some token
        | _ -> None

    /// The token, or None when it cannot be read; a failed read means "unknown", never "unchanged".
    let readToken (connection: NpgsqlConnection) (schema: string) =
        try
            use command = PreparedCommand.prepared (tokenCommand connection schema)
            asToken (command.ExecuteScalar())
        with
        | :? NpgsqlException
        | :? InvalidCastException -> None

    let readTokenAsync
        (connection: NpgsqlConnection)
        (schema: string)
        (cancellationToken: CancellationToken)
        =
        task {
            try
                use! command =
                    PreparedCommand.preparedAsync (tokenCommand connection schema) cancellationToken

                let! value = command.ExecuteScalarAsync(cancellationToken)
                return asToken value
            with
            | :? NpgsqlException
            | :? InvalidCastException -> return None
        }

    /// Run `verify` unless the catalog is provably unchanged since it last succeeded within `interval`.
    let admitWithin
        (interval: TimeSpan)
        (scope: string)
        (schema: string)
        (connection: NpgsqlConnection)
        (verify: unit -> unit)
        =
        let key = keyOf scope connection
        let observed = readToken connection schema

        match observed with
        | Some token when isCurrent interval key token -> Interlocked.Increment(&skipped) |> ignore
        | _ ->
            verify ()
            Interlocked.Increment(&verified) |> ignore
            observed |> Option.iter (remember key)

    let admit = admitWithin verificationInterval

    /// The cancellable twin of `admitWithin`; cancellation propagates exactly as it does from `verify`.
    let admitWithinAsync
        (interval: TimeSpan)
        (scope: string)
        (schema: string)
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        (verify: unit -> Task)
        =
        task {
            let key = keyOf scope connection
            let! observed = readTokenAsync connection schema cancellationToken

            match observed with
            | Some token when isCurrent interval key token ->
                Interlocked.Increment(&skipped) |> ignore
            | _ ->
                do! verify ()
                Interlocked.Increment(&verified) |> ignore
                observed |> Option.iter (remember key)
        }
        :> Task

    let admitAsync = admitWithinAsync verificationInterval
