module ClaimCore.Tests.DatabasePrivateFileTests

open System
open System.Diagnostics
open System.IO
open System.Text
open Expecto

let private physicalTemp () =
    let temporary = Path.GetTempPath()

    if
        OperatingSystem.IsMacOS()
        && (temporary.StartsWith("/var/", StringComparison.Ordinal)
            || temporary.StartsWith("/tmp/", StringComparison.Ordinal))
    then
        "/private" + temporary
    else
        temporary

let private withSandbox action =
    let directory =
        Path.Combine(physicalTemp (), "claimcore-admin-test-" + Guid.NewGuid().ToString("N"))

    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory(directory) |> ignore
    else
        Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

    try
        action directory
    finally
        Directory.Delete(directory, true)

let private privateSource directory name bytes =
    let path = Path.Combine(directory, name)
    let options = FileStreamOptions()
    options.Mode <- FileMode.CreateNew
    options.Access <- FileAccess.Write
    options.Share <- FileShare.None

    if not (OperatingSystem.IsWindows()) then
        options.UnixCreateMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite

    use stream = new FileStream(path, options)
    stream.Write(bytes, 0, bytes.Length)
    stream.Flush(true)
    path

let private invoke path =
    let dll = Path.Combine(AppContext.BaseDirectory, "ClaimCore.Database.dll")
    Expect.isTrue (File.Exists(dll)) "Built Database application is available"
    let start = ProcessStartInfo("dotnet")
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add(dll)
    start.ArgumentList.Add("verify")

    start.Environment.Keys
    |> Seq.filter (fun key ->
        key.StartsWith("CLAIMCORE_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("COVERLET_", StringComparison.OrdinalIgnoreCase))
    |> Seq.toArray
    |> Array.iter (fun key -> start.Environment.Remove(key) |> ignore)

    start.Environment["CLAIMCORE_ADMIN_CONNECTION_FILE"] <- path
    use child = new Process(StartInfo = start)
    Expect.isTrue (child.Start()) "Database process starts"
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit(30000)) then
        child.Kill(true)
        failtest "Database private-file rejection timed out"

    child.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult()

let private expectRefused path =
    let exitCode, stdout, stderr = invoke path
    Expect.equal exitCode 3 "Unsafe admin file refuses initialization before DB access"
    Expect.equal stdout "" "Unsafe admin file emits no success payload"

    use diagnostic = System.Text.Json.JsonDocument.Parse(stderr)

    Expect.equal
        (diagnostic.RootElement.GetProperty("diagnostic").GetProperty("id").GetString())
        "DB_CONNECTION_FILE_REFUSED"
        "Specific private-file admission cause"

    Expect.equal
        (diagnostic.RootElement.GetProperty("operationOutcome").GetString())
        "NOT_STARTED"
        "No maintenance was attempted"

    Expect.isFalse (stderr.Contains(path, StringComparison.Ordinal)) "Diagnostics omit private path"

    Expect.isFalse
        (stderr.Contains("synthetic-admin-marker", StringComparison.Ordinal))
        "Diagnostics omit file bytes"

let private boundary () =
    withSandbox (fun directory ->
        let value =
            Encoding.UTF8.GetBytes(
                "Host=127.0.0.1;Port=1;Database=synthetic;Username=synthetic;Password=synthetic-admin-marker"
            )

        let privatePath = privateSource directory "owner.connection" value

        if OperatingSystem.IsWindows() then
            expectRefused privatePath
        else
            File.SetUnixFileMode(privatePath, UnixFileMode.UserRead ||| UnixFileMode.GroupRead)
            expectRefused privatePath
            File.SetUnixFileMode(privatePath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            let link = Path.Combine(directory, "owner-link.connection")
            File.CreateSymbolicLink(link, privatePath) |> ignore
            expectRefused link
            let ancestor = Path.Combine(directory, "owner-ancestor-link")
            Directory.CreateSymbolicLink(ancestor, directory) |> ignore
            expectRefused (Path.Combine(ancestor, "owner.connection"))
            let invalid = privateSource directory "invalid.connection" [| 0xFFuy |]
            expectRefused invalid

            let oversized =
                privateSource directory "oversized.connection" (Array.create 8193 0x61uy)

            expectRefused oversized)

let tests =
    testList
        "Database admin private-file boundary"
        [
            testCase
                "[CC-DB-002] schema-owner credential rejects unsafe mode, links, UTF-8, and size before database access"
                boundary
        ]
