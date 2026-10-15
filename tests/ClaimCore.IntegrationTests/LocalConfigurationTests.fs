module ClaimCore.IntegrationTests.LocalConfigurationTests

open System
open System.IO
open System.Diagnostics
open System.Security.Cryptography
open Expecto
open ClaimCore.Application
open ClaimCore.Database
open ClaimCore.HostSecurity

let private configure directory =
    let tools = Path.Combine(directory, "tools")
    Directory.CreateDirectory tools |> ignore
    let docker = Path.Combine(tools, "docker")
    // Only container ownership handoff is substituted; configuration and OpenSSL run normally.
    File.WriteAllText(docker, "#!/bin/sh\nexit 0\n")

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
    config.ArgumentList.Add(Path.Combine(directory, "configuration"))
    config.ArgumentList.Add("Etc/UTC")
    config.ArgumentList.Add("7443")
    config.ArgumentList.Add("7444")
    config.Environment["PATH"] <- tools + string Path.PathSeparator + config.Environment["PATH"]
    config

let private admittedOwner directory =
    let path = Path.Combine(directory, "configuration/administration/owner.principal")

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

let private producerRoundtrip () =
    async {
        let directory = Directory.CreateTempSubdirectory("claimcore-local-config-").FullName

        try
            let! code, _, _ =
                AdministrationProcess.execute (configure directory) |> Async.AwaitTask

            Expect.equal
                code
                0
                "Fresh configuration producer succeeds without disclosing its inputs"

            admittedOwner directory
        finally
            Directory.Delete(directory, true)
    }

[<Tests>]
let tests =
    testCaseAsync
        "local configuration producer emits an admitted owner input [CC-DB-002]"
        (producerRoundtrip ())
