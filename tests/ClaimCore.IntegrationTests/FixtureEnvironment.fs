module ClaimCore.IntegrationTests.FixtureEnvironment

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open Testcontainers.PostgreSql
open ClaimCore.Postgres

let disposeContainer (container: PostgreSqlContainer) =
    container.DisposeAsync().AsTask().GetAwaiter().GetResult()

let tryDisposeContainer (container: PostgreSqlContainer) =
    try
        disposeContainer container
    with _ ->
        ()

let private testRunLabel () =
    match Environment.GetEnvironmentVariable("CLAIMCORE_TEST_RUN_LABEL") with
    | null
    | "" -> $"claimcore-integration-local-{Environment.ProcessId}-{Guid.NewGuid():N}"
    | value when Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$") -> value
    | _ -> invalidOp "CLAIMCORE_TEST_RUN_LABEL must be a bounded portable Docker label value."

let privateFile (directory: string) (path: string) (value: string) =
    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory(directory) |> ignore
    else
        Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

    let options =
        FileStreamOptions(
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        )

    if not (OperatingSystem.IsWindows()) then
        options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    use stream = new FileStream(path, options)
    use writer = new StreamWriter(stream, UTF8Encoding(false, true))
    writer.Write(value)
    writer.Flush()
    stream.Flush(true)

let privateBytes (directory: string) (path: string) (value: byte array) =
    if value.Length <> 32 || value |> Array.forall ((=) 0uy) then
        invalidArg (nameof value) "A synthetic writer capability must be nonzero and 32 bytes."

    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory(directory) |> ignore
    else
        Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

    let options =
        FileStreamOptions(
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        )

    if not (OperatingSystem.IsWindows()) then
        options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    use stream = new FileStream(path, options)
    stream.Write(value)
    stream.Flush(true)

let startContainer (databaseName: string) (ownerName: string) (ownerPassword: string) =
    let container =
        PostgreSqlBuilder(Baseline.containerImage)
            .WithDatabase(databaseName)
            .WithUsername(ownerName)
            .WithPassword(ownerPassword)
            .WithLabel("org.claimcore.test-run", testRunLabel ())
            // Testcontainers disables durability for speed by default; this product requires it.
            .WithCommand("-c", "fsync=on")
            .WithCommand("-c", "full_page_writes=on")
            .WithCommand("-c", "synchronous_commit=on")
            .Build()

    try
        container.StartAsync().GetAwaiter().GetResult()
        container
    with _ ->
        tryDisposeContainer container
        reraise ()
