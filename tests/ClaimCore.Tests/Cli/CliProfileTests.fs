module ClaimCore.Tests.CliProfileTests

open System
open System.IO
open System.Text
open System.Text.Json
open ClaimCore.Cli
open ClaimCore.Domain
open Expecto

let private accepted =
    function
    | Ok value -> value
    | Error _ -> failtest "Expected admitted public profile."

let private refused value =
    Expect.isError value "Unsafe profile must be refused"

let private privateMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

let private values =
    Map.ofList
        [
            CliSetting.ServiceUrl, "https://claims.example.test/"
            CliSetting.Issuer, "https://issuer.example.test/realm"
            CliSetting.ClientId, "profile-client"
            CliSetting.AuthMode, "interactive"
        ]

let private withDirectory action =
    let directory = Directory.CreateTempSubdirectory("claimcore-profile-")

    try
        action directory.FullName
    finally
        directory.Delete true

let private write (path: string) (source: string) (mode: UnixFileMode) =
    File.WriteAllText(path, source)
    File.SetUnixFileMode(path, mode)

let private environment directory overrides =
    let configured = Map.ofList (("XDG_CONFIG_HOME", directory) :: overrides)
    fun key -> Map.tryFind key configured

let private locations () =
    let lookup values name = Map.tryFind name (Map.ofList values)

    for supplied, expected in
        [
            [ "HOME", "/synthetic/home" ], "/synthetic/home/.config/claimcore/profile.json"
            [ "HOME", "/synthetic/home"; "XDG_CONFIG_HOME", "/synthetic/config" ],
            "/synthetic/config/claimcore/profile.json"
            [ "HOME", "/synthetic/home"; "XDG_CONFIG_HOME", "" ],
            "/synthetic/home/.config/claimcore/profile.json"
        ] do
        Expect.equal
            (CliProfile.location (lookup supplied) |> accepted)
            expected
            "Fixed Unix location"

    for supplied in [ []; [ "HOME", "relative" ]; [ "XDG_CONFIG_HOME", "relative" ] ] do
        CliProfile.location (lookup supplied) |> refused

let private atomicPermissions () =
    withDirectory (fun root ->
        let path = CliProfile.location (environment root []) |> accepted
        CliProfile.write path values |> accepted
        Expect.equal (File.GetUnixFileMode path) privateMode "Created profile is 0600"

        Expect.equal
            (File.GetUnixFileMode((Path.GetDirectoryName path |> nonNull)))
            (privateMode ||| UnixFileMode.UserExecute)
            "Created directory is 0700"

        Expect.equal
            (CliProfile.read path |> accepted)
            values
            "All written public values are admitted"

        let updated = Map.add CliSetting.ClientId "changed-client" values
        CliProfile.write path updated |> accepted

        Expect.equal
            (CliProfile.read path |> accepted)
            updated
            "Atomic replacement retains complete JSON"

        Expect.equal
            (Directory.GetFiles((Path.GetDirectoryName path |> nonNull))
             |> Array.map (fun path -> Path.GetFileName path |> nonNull))
            [| "profile.json" |]
            "No temporary file remains"

        let previous = File.ReadAllBytes path

        File.SetUnixFileMode(
            (Path.GetDirectoryName path |> nonNull),
            privateMode ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupWrite
        )

        CliProfile.write path values |> refused

        Expect.sequenceEqual
            (File.ReadAllBytes path)
            previous
            "Refusal leaves existing profile unchanged")

let private members () =
    withDirectory (fun root ->
        let path = Path.Combine(root, "profile.json")

        for source in
            [
                "[]"
                "null"
                "{"
                ""
                "{\"issuer\":42}"
                "{\"issuer\":\"a\",\"issuer\":\"b\"}"
                "{\"clientSecret\":\"synthetic-secret\"}"
                "{\"accessToken\":\"synthetic-token\"}"
                "{\"refresh_token\":\"synthetic-token\"}"
                "{\"secretFile\":\"/synthetic/secret\"}"
                "{\"serviceURL\":\"https://claims.example/\"}"
                "{\"unknown\":null}"
            ] do
            write path source privateMode
            CliProfile.read path |> refused

        File.WriteAllBytes(path, [| 255uy |])
        CliProfile.read path |> refused
        write path ("{}" + String(' ', CliProfile.maximumBytes - 2)) privateMode
        Expect.equal (CliProfile.read path |> accepted) Map.empty "Exact byte bound is admitted"
        File.AppendAllText(path, " ")
        CliProfile.read path |> refused)

let private fileAdmission () =
    withDirectory (fun root ->
        let target = Path.Combine(root, "target")
        let link = Path.Combine(root, "profile.json")
        File.CreateSymbolicLink(link, target) |> ignore
        CliProfile.read link |> refused

        for mode, valid in
            [
                privateMode, true
                privateMode ||| UnixFileMode.GroupRead, true
                privateMode ||| UnixFileMode.GroupWrite, false
                privateMode ||| UnixFileMode.OtherWrite, false
                privateMode ||| UnixFileMode.OtherRead, false
            ] do
            write target "{}" mode

            Expect.equal
                (CliProfile.read link |> Result.isOk)
                valid
                "Opened symlink target uses existing private admission")

let private precedence () =
    withDirectory (fun root ->
        let read =
            environment
                root
                [
                    "CLAIMCORE_SERVICE_URL", "https://override.example.test/"
                    "CLAIMCORE_OIDC_CLIENT_ID", "environment-client"
                ]

        let path = CliProfile.location read |> accepted
        CliProfile.write path values |> accepted
        let _, sources = RemoteConfiguration.sources read |> accepted

        Expect.equal
            sources[CliSetting.ServiceUrl]
            (Some "https://override.example.test/", "environment")
            "Service overridden independently"

        Expect.equal
            sources[CliSetting.Issuer]
            (Some "https://issuer.example.test/realm", "profile")
            "Issuer remains from profile"

        Expect.equal
            sources[CliSetting.ClientId]
            (Some "environment-client", "environment")
            "Client overridden independently"

        Expect.equal
            sources[CliSetting.SecretFile]
            (None, "missing")
            "No secret-file setting is inferred"

        let actual =
            RemoteConfiguration.fromSettings (fun key -> fst sources[key]) |> accepted

        Expect.equal
            actual.Service.AbsoluteUri
            "https://override.example.test/"
            "Mixed effective configuration is admitted"

        Expect.equal
            actual.Issuer.AbsoluteUri
            "https://issuer.example.test/realm"
            "Exact profile issuer remains")

let private sourceAttribution () =
    withDirectory (fun root ->
        let secret = Path.Combine(root, "secret")
        write secret "must-never-be-read" UnixFileMode.None

        let read =
            environment
                root
                [
                    "CLAIMCORE_OIDC_CLIENT_SECRET_FILE", secret
                    "CLAIMCORE_OIDC_CLIENT_ID", "environment-client"
                ]

        let path = CliProfile.location read |> accepted
        CliProfile.write path values |> accepted
        let bytes = CliConfigure.showWith read |> accepted
        let source = Encoding.UTF8.GetString bytes

        Expect.isFalse
            (source.Contains "must-never-be-read")
            "Showing sources never opens the secret file"

        use document = JsonDocument.Parse(ReadOnlyMemory bytes)
        let fields = document.RootElement.GetProperty "settings"

        Expect.equal
            (fields.GetProperty("CLAIMCORE_OIDC_CLIENT_ID").GetProperty("source").GetString())
            "environment"
            "Environment source is explicit"

        Expect.equal
            (fields.GetProperty("CLAIMCORE_OIDC_ISSUER").GetProperty("source").GetString())
            "profile"
            "Profile source is explicit"

        Expect.equal
            (fields
                .GetProperty("CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE")
                .GetProperty("source")
                .GetString())
            "missing"
            "Missing source is explicit"

        Expect.isTrue
            (fields
                .GetProperty("CLAIMCORE_OIDC_CLIENT_SECRET_FILE")
                .GetProperty("present")
                .GetBoolean())
            "Secret-file source discloses presence only"

        Expect.isFalse (source.Contains secret) "Secret-file path is not echoed"
        write path "{\"clientSecret\":\"forbidden\"}" privateMode
        RemoteConfiguration.sources read |> refused)

let private noImplicitWrites () =
    withDirectory (fun root ->
        let read = environment root []
        RemoteConfiguration.sources read |> accepted |> ignore

        Expect.isEmpty
            (Directory.GetFileSystemEntries root)
            "Reading absent configuration creates no files"

        CliConfigure.admit [ "--client-secret"; "synthetic" ] |> refused
        CliConfigure.admit [ "--show"; "--client-id"; "synthetic" ] |> refused)

[<Tests>]
let tests =
    testList
        "CLI profile"
        [
            testCase "uses one fixed Unix location" locations
            testCase "atomic writes use 0600 files and 0700 directories" atomicPermissions
            testCase "refuses malformed unknown secret and oversized members" members
            testCase "applies private admission to opened symlink targets" fileAdmission
            testCase "environment overrides public values independently" precedence
            testCase "show attributes sources without reading secret content" sourceAttribution
            testCase "reads and refused secret options create no configuration" noImplicitWrites
        ]
