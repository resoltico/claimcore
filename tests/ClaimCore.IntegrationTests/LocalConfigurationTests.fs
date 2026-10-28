module ClaimCore.IntegrationTests.LocalConfigurationTests

open ClaimCore.Domain

open ClaimCore.Protocol

open System
open System.IO
open System.Diagnostics
open System.Security.Cryptography
open System.Text.Json
open System.Text
open System.Threading.Tasks
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
        let identity = PrincipalKey.toInput principal

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

let private docker arguments =
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

    for argument in arguments do
        start.ArgumentList.Add argument

    Task.Run(fun () ->
        let result = ClaimCore.TestSupport.BoundedProcess.run start None 1_048_576 60_000

        result.ExitCode,
        Encoding.UTF8.GetString result.StandardOutput,
        Encoding.UTF8.GetString result.StandardError)

let private compose directory file project arguments =
    docker (
        [
            "compose"
            "--project-name"
            project
            "--env-file"
            Path.Combine(configuration directory, "compose.env")
            "--file"
            file
        ]
        @ arguments
    )

let private composePaths directory =
    async {
        let root = ClaimCore.TestSupport.RepositoryRoot.find ()
        let project = "claimcore-config-" + Guid.NewGuid().ToString("N")
        let file = Path.Combine(root, "deployment/compose.yaml")

        let! code, output, _ =
            compose directory file project [ "config"; "--format"; "json" ]
            |> Async.AwaitTask

        Expect.equal code 0 "Compose admits generated paths without syntax reinterpretation"
        use document = JsonDocument.Parse(output)
        literalComposePaths directory document
    }

let private inspectRoleMount directory file project expected =
    async {
        let! code, output, _ =
            compose directory file project [ "ps"; "--all"; "--quiet"; "postgres" ]
            |> Async.AwaitTask

        Expect.equal code 0 "Only the owned postgres container is selected"
        let identity = output.Trim()

        Expect.isTrue
            (identity.Length = 64 && identity |> Seq.forall Char.IsAsciiHexDigitLower)
            "One owned container identity"

        let! code, output, _ =
            docker [ "inspect"; "--format"; "{{json .Mounts}}"; identity ]
            |> Async.AwaitTask

        Expect.equal code 0 "Owned mount inspection succeeds"
        use document = JsonDocument.Parse output

        let role =
            document.RootElement.EnumerateArray()
            |> Seq.find (fun mount ->
                mount.GetProperty("Destination").GetString() =
                    "/docker-entrypoint-initdb.d/10-claimcore-roles.sh")

        Expect.equal
            (role.GetProperty("Source").GetString())
            expected
            "Docker receives the exact colon-containing checkout path"

        Expect.isFalse
            (role.GetProperty("RW").GetBoolean())
            "The repository role script is read-only"
    }

let private createdMount directory file expected shouldCreate =
    async {
        let project = "claimcore-mount-" + Guid.NewGuid().ToString("N")
        let mutable original: exn option = None

        try
            let! code, _, errors =
                compose directory file project [ "create"; "--no-build"; "postgres" ]
                |> Async.AwaitTask

            if shouldCreate then
                Expect.equal code 0 "Structured bind creates from a checkout containing a colon"
                do! inspectRoleMount directory file project expected
            else
                Expect.notEqual
                    code
                    0
                    "The short-bind negative control cannot represent the checkout path"

                Expect.isTrue
                    (errors.Contains(
                        "invalid volume specification",
                        StringComparison.OrdinalIgnoreCase
                     )
                     && errors.Contains(expected, StringComparison.Ordinal))
                    "The negative control refuses the intended colon-containing role mount"
        with failure ->
            original <- Some failure

        try
            let! code, _, _ =
                compose directory file project [ "down"; "--volumes"; "--timeout"; "10" ]
                |> Async.AwaitTask

            Expect.equal code 0 "Only the owned project and its volumes are removed"
        with cleanup ->
            match original with
            | Some failure -> raise (AggregateException(failure, cleanup))
            | None -> raise cleanup

        original |> Option.iter raise
    }

let private checkoutMount directory =
    async {
        let root = ClaimCore.TestSupport.RepositoryRoot.find ()

        let checkout =
            Directory
                .CreateDirectory(Path.Combine(directory, "checkout:colon", "deployment"))
                .FullName

        let role = Path.Combine(checkout, "postgres-roles.sh")
        File.Copy(Path.Combine(root, "deployment/postgres-roles.sh"), role)
        let file = Path.Combine(checkout, "compose.yaml")
        File.Copy(Path.Combine(root, "deployment/compose.yaml"), file)
        do! createdMount directory file role true

        let structured =
            """      - type: bind
        source: ./postgres-roles.sh
        target: /docker-entrypoint-initdb.d/10-claimcore-roles.sh
        read_only: true
        bind:
          create_host_path: false"""

        let source = File.ReadAllText file

        Expect.isTrue
            (source.Contains(structured, StringComparison.Ordinal))
            "The negative control replaces the production role bind"

        let negative = Path.Combine(checkout, "compose.short-bind.yaml")

        File.WriteAllText(
            negative,
            source.Replace(
                structured,
                "      - ./postgres-roles.sh:/docker-entrypoint-initdb.d/10-claimcore-roles.sh:ro",
                StringComparison.Ordinal
            )
        )

        do! createdMount directory negative role false
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
            do! checkoutMount directory
            do! printedStart directory output
            do! lineBreaks directory
        finally
            Directory.Delete(directory, true)
    }

[<Tests>]
let tests =
    testCaseAsync
        "local configuration producer emits an admitted owner input"
        (producerRoundtrip ())
