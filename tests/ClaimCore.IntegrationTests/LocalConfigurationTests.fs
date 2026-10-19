module ClaimCore.IntegrationTests.LocalConfigurationTests

open System
open System.IO
open System.Diagnostics
open System.Security.Cryptography
open System.Text.Json
open Microsoft.VisualBasic.FileIO
open Expecto
open ClaimCore.Application
open ClaimCore.Database
open ClaimCore.HostSecurity

let private configuration directory =
    Path.Combine(directory, "configuration $CLAIMCORE_QA_PATH # :, \" \\\' end\\")

let private configure directory =
    let tools = Path.Combine(directory, "tools")
    Directory.CreateDirectory tools |> ignore
    let docker = Path.Combine(tools, "docker")
    // Only container ownership handoff is substituted; configuration and OpenSSL run normally.
    File.WriteAllText(
        docker,
        "#!/bin/sh\nwhile [ \"$#\" -gt 0 ]; do\n  if [ \"$1\" = --mount ]; then printf '%s' \"$2\" >\"$CLAIMCORE_CONFIGURATION_MOUNT\"; fi\n  if [ \"$1\" = --env-file ]; then printf '%s' \"$2\" >\"$CLAIMCORE_CONFIGURATION_START\"; fi\n  shift\ndone\n"
    )

    File.SetUnixFileMode(
        docker,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )

    let root = ClaimCore.TestSupport.RepositoryRoot.find ()

    let config =
        ProcessStartInfo(
            "bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    config.ArgumentList.Add(Path.Combine(root, "deployment/configure-local.sh"))
    config.ArgumentList.Add(configuration directory)
    config.ArgumentList.Add("Etc/UTC")
    config.ArgumentList.Add("7443")
    config.ArgumentList.Add("7444")
    config.Environment["PATH"] <- tools + string Path.PathSeparator + config.Environment["PATH"]
    config.Environment["CLAIMCORE_CONFIGURATION_MOUNT"] <- Path.Combine(directory, "mount.csv")
    config

let private admittedOwner directory =
    let path = Path.Combine(configuration directory, "administration/owner.principal")

    let bytes =
        PrivateFile.read 8192 path
        |> Result.defaultWith (fun _ -> failtest "Generated owner file must be admitted as private")

    try
        let operationId, principal = Inputs.decodeOwner bytes
        Expect.isTrue (operationId <> Guid.Empty) "Generated operation identity is nonzero"
        Expect.isTrue (PrincipalKey.isHuman principal) "Generated owner is a human principal"
        let identity = PrincipalKey.describe principal

        Expect.equal
            identity.Issuer
            "https://identity.localhost:7444/realms/claimcore"
            "Selected issuer"
    finally
        CryptographicOperations.ZeroMemory bytes

let private literalMount directory =
    use input = new StringReader(File.ReadAllText(Path.Combine(directory, "mount.csv")))
    use fields = new TextFieldParser(input)
    fields.SetDelimiters [| "," |]

    Expect.equal
        (fields.ReadFields() |> nonNull)
        [| "type=bind"; "source=" + configuration directory; "target=/config" |]
        "Docker's CSV mount argument preserves the admitted path"

let private literalComposePaths directory (document: JsonDocument) =
    for service in document.RootElement.GetProperty("services").EnumerateObject() do
        for volume in service.Value.GetProperty("volumes").EnumerateArray() do
            if volume.GetProperty("type").GetString() = "bind" then
                Expect.isFalse
                    (volume.GetProperty("bind").GetProperty("create_host_path").GetBoolean())
                    "Structured bind mounts preserve colons at Docker's API boundary"

                if
                    volume.GetProperty("target").GetString()
                    <> "/docker-entrypoint-initdb.d/10-claimcore-roles.sh"
                then
                    let source = volume.GetProperty("source").GetString() |> nonNull

                    Expect.isTrue
                        (source
                            .Replace("$$", "$")
                            .StartsWith(configuration directory + "/", StringComparison.Ordinal))
                        "Compose rendering preserves each exact host path"

let private composePaths directory =
    async {
        let root = ClaimCore.TestSupport.RepositoryRoot.find ()
        let start = ProcessStartInfo("docker", UseShellExecute = false)
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true

        for name in start.Environment.Keys |> Seq.toArray do
            if
                name.StartsWith("CLAIMCORE_", StringComparison.Ordinal)
                || name.StartsWith("COMPOSE_", StringComparison.Ordinal)
            then
                start.Environment.Remove name |> ignore

        start.Environment["CLAIMCORE_QA_PATH"] <- "unexpected-interpolation"

        for arg in
            [
                "compose"
                "--env-file"
                Path.Combine(configuration directory, "compose.env")
                "--file"
                Path.Combine(root, "deployment/compose.yaml")
                "config"
                "--format"
                "json"
            ] do
            start.ArgumentList.Add arg

        let! code, output, _ = AdministrationProcess.execute start |> Async.AwaitTask
        Expect.equal code 0 "Compose admits generated paths without syntax reinterpretation"
        use document = JsonDocument.Parse(output)

        literalComposePaths directory document
    }

let private lineBreaks directory =
    async {
        for character in [ '\r'; '\n' ] do
            let target = Path.Combine(directory, "refused" + string character)
            let start = configure directory
            start.ArgumentList[1] <- target
            let! code, _, _ = AdministrationProcess.execute start |> Async.AwaitTask
            Expect.equal code 2 "Line breaks refuse before producing configuration"
            Expect.isFalse (Directory.Exists target) "A refused path is untouched"
    }

let private printedStart directory (output: string) =
    async {
        let prefix = "Start in Bash: "

        let command =
            output.Split '\n'
            |> Array.find (fun line -> line.StartsWith(prefix, StringComparison.Ordinal))
            |> fun line -> line.Substring prefix.Length

        let start = ProcessStartInfo("bash", UseShellExecute = false)
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.ArgumentList.Add "-c"
        start.ArgumentList.Add command

        start.Environment["PATH"] <-
            Path.Combine(directory, "tools") + ":" + start.Environment["PATH"]

        let captured = Path.Combine(directory, "start-path")
        start.Environment["CLAIMCORE_CONFIGURATION_START"] <- captured
        let! code, _, _ = AdministrationProcess.execute start |> Async.AwaitTask
        Expect.equal code 0 "The printed Bash command executes with its literal path"

        Expect.equal
            (File.ReadAllText captured)
            (Path.Combine(configuration directory, "compose.env"))
            "Shell syntax in the admitted path cannot alter the printed command's arguments"
    }

let private producerRoundtrip () =
    async {
        let directory = Directory.CreateTempSubdirectory("claimcore-local-config-").FullName

        try
            let! code, output, _ =
                AdministrationProcess.execute (configure directory) |> Async.AwaitTask

            Expect.equal
                code
                0
                "Fresh configuration producer succeeds without disclosing its inputs"

            admittedOwner directory
            literalMount directory
            do! composePaths directory
            do! printedStart directory output
            do! lineBreaks directory
        finally
            Directory.Delete(directory, true)
    }

[<Tests>]
let tests =
    testCaseAsync
        "local configuration producer emits an admitted owner input [CC-DB-002]"
        (producerRoundtrip ())
