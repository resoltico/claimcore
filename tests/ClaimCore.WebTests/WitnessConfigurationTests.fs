module ClaimCore.WebTests.WitnessConfigurationTests

open System
open System.IO
open Expecto
open ClaimCore.Contracts
open ClaimCore.Web

let private witnessFiles () =
    ConfigurationTests.configured (fun directory _ _ ->
        if not (OperatingSystem.IsWindows()) then
            let connectionPath = Path.Combine(directory, "witness.connection")
            let keyPath = Path.Combine(directory, "witness.key")

            let expectRefusal expected =
                match
                    try
                        let loaded = Configuration.load ()
                        loaded.Certificate.Dispose()
                        None
                    with WebStartupException reason ->
                        Some reason
                with
                | Some actual -> Expect.equal actual expected "Exact witness configuration refusal"
                | None -> failtest "Witness configuration must fail closed."

            Environment.SetEnvironmentVariable("CLAIMCORE_WITNESS_CONNECTION_FILE", null)
            expectRefusal (WebStartupProblem.MissingSetting WebSetting.WitnessConnectionFile)
            Environment.SetEnvironmentVariable("CLAIMCORE_WITNESS_CONNECTION_FILE", connectionPath)
            Environment.SetEnvironmentVariable("CLAIMCORE_WITNESS_KEY_FILE", null)
            expectRefusal (WebStartupProblem.MissingSetting WebSetting.WitnessKeyFile)

            Environment.SetEnvironmentVariable(
                "CLAIMCORE_WITNESS_KEY_FILE",
                "relative-witness.key"
            )

            expectRefusal WebStartupProblem.WitnessKeyFileRefused
            Environment.SetEnvironmentVariable("CLAIMCORE_WITNESS_KEY_FILE", keyPath)
            let loaded = Configuration.load ()
            use _certificate = loaded.Certificate
            Expect.equal loaded.WitnessKeyRingPath keyPath "Key-ring path is passed to Hosting")

let tests =
    testCase "[CC-WEB-001] witness key and connection paths fail closed" witnessFiles
