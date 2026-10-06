#r "/app/database/Microsoft.Extensions.Logging.Abstractions.dll"
#r "/app/database/Npgsql.dll"
#r "/app/database/ClaimCore.Witness.dll"

open System
open System.IO
open System.Security.Authentication
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Npgsql
open ClaimCore.Witness

// Engineering-only connection evidence: never print provider exceptions or private inputs.
let rec tlsFailure depth (error: exn) =
    if depth = 0 || isNull error then false
    elif error :? AuthenticationException then true
    else tlsFailure (depth - 1) error.InnerException

let snapshot (connection: NpgsqlConnection) schema =
    use tables =
        new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname=@schema ORDER BY tablename",
            connection
        )

    tables.Parameters.AddWithValue("schema", schema) |> ignore
    use reader = tables.ExecuteReader()

    let names =
        [
            while reader.Read() do
                yield reader.GetString(0)
        ]

    reader.Close()

    if List.isEmpty names then
        failwith "Missing initialized schema."

    use digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)

    for name in names do
        // Table identifiers originate from this initialized synthetic catalog, never client input.
        let table = schema + ".\"" + name.Replace("\"", "\"\"") + "\""

        use rows =
            new NpgsqlCommand(
                "SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text)::text,'[]') FROM "
                + table
                + " t",
                connection
            )

        let content = rows.ExecuteScalar() :?> string
        digest.AppendData(Encoding.UTF8.GetBytes(name + "\u0000" + content + "\u0000"))

    Convert.ToHexStringLower(digest.GetHashAndReset())

let fileDigest path =
    File.ReadAllBytes(path) |> SHA256.HashData |> Convert.ToHexStringLower

let publication () =
    use document = JsonDocument.Parse(File.ReadAllText("/app/database-manifest.json"))
    let root = document.RootElement

    if root.GetProperty("product").GetString() <> "ClaimCore.Database" then
        failwith "Wrong publication."

    let files = root.GetProperty("files").EnumerateArray() |> Seq.toList

    if List.isEmpty files then
        failwith "Empty publication."

    for file in files do
        let path = file.GetProperty("path").GetString()
        let expected = file.GetProperty("sha256").GetString()

        if fileDigest ("/app/database/" + path) <> expected then
            failwith "Changed publication."

    root.GetProperty("treeSha256").GetString()

let publicationSha256 = publication ()
let transportSha256 = fileDigest "/app/database/ClaimCore.Witness.dll"
let runtimeDirectory = Path.GetDirectoryName(typeof<obj>.Assembly.Location)

let systemDirectory =
    match System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture with
    | System.Runtime.InteropServices.Architecture.Arm64 -> "/usr/lib/aarch64-linux-gnu"
    | System.Runtime.InteropServices.Architecture.X64 -> "/usr/lib/x86_64-linux-gnu"
    | _ -> failwith "Unsupported qualification architecture."

let runtimeFiles =
    [
        "System.Net.Security.dll", typeof<System.Net.Security.SslStream>.Assembly.Location
        "System.Security.Cryptography.dll", typeof<SHA256>.Assembly.Location
        "libSystem.Security.Cryptography.Native.OpenSsl.so",
        Path.Combine(runtimeDirectory, "libSystem.Security.Cryptography.Native.OpenSsl.so")
        "libssl.so.3", Path.Combine(systemDirectory, "libssl.so.3")
        "libcrypto.so.3", Path.Combine(systemDirectory, "libcrypto.so.3")
    ]
    |> List.map (fun (name, path) -> name, fileDigest path)
    |> Map.ofList

let runtimeVersion = Environment.Version.ToString()
let target = fsi.CommandLineArgs |> Array.skip 1 |> Array.exactlyOne

let file, schema =
    match target with
    | "primary" -> "primary-owner.connection", "claimcore"
    | "witness" -> "witness-owner.connection", "claimcore_witness"
    | _ -> failwith "Unknown qualification target."

let mutable phase = "OPEN"

let result =
    try
        use connection =
            PostgresTransport.connection (
                File.ReadAllText("/etc/claimcore/administration/" + file).Trim()
            )

        connection.OpenAsync().GetAwaiter().GetResult()
        phase <- "READ"

        use transaction =
            connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead)

        use readOnly =
            new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)

        readOnly.ExecuteNonQuery() |> ignore

        let seedQuery =
            if target = "primary" then
                "SELECT EXISTS(SELECT 1 FROM claimcore.case_changes) AND (SELECT count(*) FROM claimcore.request_preparations)>=2"
            else
                "SELECT EXISTS(SELECT 1 FROM claimcore_witness.journal)"

        use seed = new NpgsqlCommand(seedQuery, connection, transaction)
        let seededWork = seed.ExecuteScalar() :?> bool

        if not seededWork then
            failwith "Synthetic work was not retained."

        let state = snapshot connection schema
        transaction.Rollback()

        {|
            outcome = "CONNECTED_READ_VERIFIED"
            phase = phase
            stateSha256 = state
            seededWork = seededWork
        |}
    with error ->
        let outcome =
            if phase = "OPEN" && tlsFailure 16 error then
                "TLS_AUTHENTICATION_REFUSED"
            else
                "OTHER_FAILURE"

        {|
            outcome = outcome
            phase = phase
            stateSha256 = ""
            seededWork = false
        |}

Console.WriteLine(
    JsonSerializer.Serialize(
        {|
            outcome = result.outcome
            phase = result.phase
            stateSha256 = result.stateSha256
            seededWork = result.seededWork
            publicationSha256 = publicationSha256
            transportSha256 = transportSha256
            runtimeVersion = runtimeVersion
            runtimeFiles = runtimeFiles
        |}
    )
)
