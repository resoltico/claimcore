module ClaimCore.IntegrationTests.WitnessKeyCustodyTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures

let private identity () =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT installation_id,lineage_id,witness_epoch FROM claimcore.installation_lineage",
            connection
        )

    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic primary identity is required."

    {
        InstallationId = reader.GetGuid(0)
        LineageId = reader.GetGuid(1)
        Epoch = reader.GetInt64(2)
    }

let private keyPath () =
    let parent =
        Path.GetDirectoryName(appConnectionFile ())
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic private directory is missing.")

    let canonicalParent =
        if
            OperatingSystem.IsMacOS()
            && parent.StartsWith("/var/", StringComparison.Ordinal)
        then
            "/private" + parent
        else
            parent

    Path.Combine(canonicalParent, "witness-keys-" + Guid.NewGuid().ToString("N"))

let private writeKeyRing (path: string) keyId =
    let encoded = Convert.ToBase64String(witnessKey ())

    let json =
        $"{{\"version\":1,\"activeKeyId\":\"{keyId:D}\",\"keys\":[{{\"id\":\"{keyId:D}\",\"materialBase64\":\"{encoded}\"}}]}}"

    let options =
        FileStreamOptions(
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        )

    options.UnixCreateMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    use stream = new FileStream(path, options)
    let bytes = Encoding.UTF8.GetBytes(json)
    stream.Write(bytes)
    stream.Flush(true)

let private expectOpen (path: string) =
    use _loaded = WitnessKeyCustody.load path

    match
        Runtime.OpenPostgres(
            appConnection (),
            witnessConnection (),
            path,
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await
    with
    | Error _ -> failtest "Owner-private key ring must admit the synthetic runtime."
    | Ok runtime -> (runtime :> IDisposable).Dispose()

let private expectBroadModeRefused (path: string) =
    File.SetUnixFileMode(
        path,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead
    )

    match
        Runtime.OpenPostgres(
            appConnection (),
            witnessConnection (),
            path,
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await
    with
    | Error _ -> ()
    | Ok runtime ->
        (runtime :> IDisposable).Dispose()
        failtest "Group-readable key ring must refuse runtime opening."

let private expectWrongSuppressionRefused () =
    let path = keyPath ()
    let material = RandomNumberGenerator.GetBytes(32)
    let encoded = Convert.ToBase64String(material)
    CryptographicOperations.ZeroMemory(material)

    let json =
        $"{{\"version\":1,\"keyId\":\"{Guid.NewGuid():D}\",\"materialBase64\":\"{encoded}\"}}"

    let options =
        FileStreamOptions(
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        )

    options.UnixCreateMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite

    try
        use stream = new FileStream(path, options)
        let bytes = Encoding.UTF8.GetBytes(json)
        stream.Write(bytes)
        stream.Flush(true)
        stream.Close()

        match
            Runtime.OpenPostgres(
                appConnection (),
                witnessConnection (),
                witnessKey (),
                path,
                artifactKeyRingFile (),
                CancellationToken.None
            )
            |> await
        with
        | Error _ -> ()
        | Ok runtime ->
            (runtime :> IDisposable).Dispose()
            failtest "Wrong suppression key must refuse runtime opening."
    finally
        if File.Exists(path) then
            File.Delete(path)

let tests =
    testList
        "owner-private witness key custody"
        [
            testCase
                "[CC-WIT-001] owner-private key-ring file admits and broad mode refuses"
                (fun _ ->
                    let store = witnessStore (witnessConnection ()) (identity ())
                    let keyId, _ = store.ReadKeyCheck()
                    let path = keyPath ()

                    try
                        writeKeyRing path keyId
                        expectOpen path
                        expectBroadModeRefused path
                        expectWrongSuppressionRefused ()
                    finally
                        if File.Exists(path) then
                            File.Delete(path))
        ]
