module internal ClaimCore.WitnessTests.WitnessFixtureDiagnosticsTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Expecto
open Npgsql
open ClaimCore.TestSupport
open ClaimCore.WitnessTests.WitnessTestSupport
open ClaimCore.WitnessTests.WitnessFixtureDiagnostics

let private privateRoot () =
    let temporary = DirectoryInfo(Path.GetTempPath()).FullName

    let physical =
        if
            OperatingSystem.IsMacOS()
            && temporary.StartsWith("/var/", StringComparison.Ordinal)
        then
            "/private" + temporary
        else
            temporary

    let directory =
        Path.Combine(
            physical,
            "claimcore-witness-diagnostic-control-" + Guid.NewGuid().ToString("N")
        )

    Directory.CreateDirectory(
        directory,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    directory

let private checkFacts facts (password: string) (rejectedPassword: string) directory =
    Expect.equal
        facts.CredentialAgreement
        (Nullable false)
        "Wrong client credential is distinguished from unavailable evidence"

    Expect.equal facts.PortAgreement (Nullable true) "Owned binding matches selected port"

    Expect.equal
        facts.Running
        (Nullable true)
        "Owned synthetic container remains running before disposal"

    Expect.equal facts.SqlState "28P01" "Closed original authentication category"

    write directory facts
    |> function
        | Ok() -> ()
        | Error _ -> failtest "Private diagnostic write refused."

    let text = File.ReadAllText(Path.Combine(directory, "witness-startup.json"))

    Expect.isFalse
        (text.Contains(password, StringComparison.Ordinal))
        "Supplied credential is omitted"

    Expect.isFalse
        (text.Contains(rejectedPassword, StringComparison.Ordinal))
        "Rejected credential is omitted"

    use parsed = JsonDocument.Parse(text)

    Expect.equal
        (parsed.RootElement.GetProperty("CredentialAgreement").GetBoolean())
        false
        "Retained closed fact"

let private failedRetention facts directory (original: exn) =
    let target = Path.Combine(directory, "empty")
    Directory.CreateDirectory(target) |> ignore
    let linked = directory + "-linked"
    Directory.CreateSymbolicLink(linked, target) |> ignore
    let mutable refused = false

    try
        let mutable observed: exn option = None

        try
            FixtureCleanup.run (fun () -> raise (TimeoutException("PRIVATE-CLEANUP"))) (fun () ->
                retain
                    (fun () ->
                        write linked facts
                        |> function
                            | Error _ ->
                                refused <- true
                                raise (IOException("PRIVATE-CAPTURE"))
                            | Ok() -> ())
                    original
                |> raise)
        with error ->
            observed <- Some error

        Expect.isTrue refused "A fresh linked ancestor must refuse the private write"

        Expect.isFalse
            (File.Exists(Path.Combine(target, "witness-startup.json")))
            "No file reaches linked target"

        Expect.isTrue
            (observed |> Option.exists (fun value -> obj.ReferenceEquals(value, original)))
            "Refused capture and failed cleanup preserve the actual primary failure"

        Expect.equal
            original.Data["FixtureCleanupSettlement"]
            (box "unknown")
            "Failed cleanup is separate uncertainty"
    finally
        Directory.Delete(linked)

let private withContainer password test =
    let container = createContainer password

    FixtureCleanup.run
        (fun () -> container.DisposeAsync().AsTask().GetAwaiter().GetResult())
        (fun () ->
            container.StartAsync().GetAwaiter().GetResult()
            test container)

let tests =
    testCase
        "startup diagnostic refusal preserves authentication failure and cleanup uncertainty"
        (fun () ->
            let password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))

            withContainer password (fun container ->
                let rejectedPassword =
                    Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))

                let selected = NpgsqlConnectionStringBuilder(container.GetConnectionString())
                selected.Password <- rejectedPassword
                selected.Timeout <- 5
                selected.Pooling <- false
                let mutable original: exn option = None

                try
                    use connection = new NpgsqlConnection(selected.ConnectionString)
                    connection.Open()
                with :? PostgresException as error ->
                    original <- Some error

                let original =
                    original
                    |> Option.defaultWith (fun () ->
                        failtest "Wrong credential must be rejected.")

                let facts =
                    inspect
                        container
                        (Some selected.ConnectionString)
                        password
                        "owner-provision"
                        original

                let directory = privateRoot ()

                try
                    checkFacts facts password rejectedPassword directory
                    failedRetention facts directory original
                finally
                    Directory.Delete(directory, true)))
