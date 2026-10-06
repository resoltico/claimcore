module ClaimCore.WebTests.ProbeProcessTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open ClaimCore.Web
open ClaimCore.Contracts
open ClaimCore.WebTests.PrivateTestPaths
open ClaimCore.WebTests.ProbeHttpsFixture

let private withEnvironment values work =
    let originals =
        values
        |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable(name))

    try
        values
        |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

        work ()
    finally
        originals
        |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

let private configuredProbe () =
    withServer (fun root binding ->
        task {
            let directory = newPrivateDirectory "claimcore-probe-"

            try
                let ca = Path.Combine(directory, "ca.pem")
                File.WriteAllText(ca, root.ExportCertificatePem())

                if not (OperatingSystem.IsWindows()) then
                    File.SetUnixFileMode(ca, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

                let settings =
                    [
                        "CLAIMCORE_WEB_ORIGIN", binding.Origin.AbsoluteUri
                        "CLAIMCORE_WEB_LISTEN_ADDRESS", "0.0.0.0"
                        "CLAIMCORE_WEB_LISTEN_PORT", string binding.ListenPort
                        "CLAIMCORE_WEB_PROBE_CA_CERT_FILE", ca
                        "CLAIMCORE_CONNECTION_FILE", "not-a-runtime-input"
                    ]

                withEnvironment settings (fun () ->
                    if OperatingSystem.IsWindows() then
                        Expect.throwsT<WebStartupException>
                            (fun () ->
                                WebProbe.run "/health/live"
                                |> _.GetAwaiter().GetResult()
                                |> ignore)
                            "Unsupported private-file security refuses"
                    else
                        Expect.equal
                            (WebProbe.run "/health/live" |> _.GetAwaiter().GetResult())
                            0
                            "Live listener succeeds without database inputs"

                        Expect.equal
                            (WebProbe.run "/health/ready" |> _.GetAwaiter().GetResult())
                            3
                            "Unready listener stays unready")
            finally
                Directory.Delete(directory, true)
        })

let private refusals () =
    let settings =
        [
            "CLAIMCORE_WEB_ORIGIN", "https://probe.localhost:9443"
            "CLAIMCORE_WEB_LISTEN_ADDRESS", "127.0.0.1"
            "CLAIMCORE_WEB_LISTEN_PORT", "0"
            "CLAIMCORE_WEB_PROBE_CA_CERT_FILE", ""
        ]

    withEnvironment settings (fun () ->
        Expect.equal
            (ClaimCore.Web.Program.main [| "probe"; "live" |])
            3
            "Invalid listener is a typed process refusal"

        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_LISTEN_PORT", "invalid")

        Expect.equal
            (ClaimCore.Web.Program.main [| "probe"; "ready" |])
            3
            "Malformed listener port refuses"

        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_LISTEN_PORT", "1")

        Expect.equal
            (WebProbe.run "/health/live" |> _.GetAwaiter().GetResult())
            3
            "Closed listener is unavailable"

        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_ORIGIN", "https://remote.example.test")
        Environment.SetEnvironmentVariable("CLAIMCORE_WEB_PROBE_CA_CERT_FILE", "unread-foreign-ca")

        Expect.equal
            (ClaimCore.Web.Program.main [| "probe"; "live" |])
            3
            "A remote listener cannot request synthetic trust")

let tests =
    testList
        "probe process admission"
        [
            testCaseAsync
                "[CC-WEB-001] native probe observes liveness and readiness without runtime credentials"
                (async { do! configuredProbe () |> Async.AwaitTask })
            testCase
                "[CC-WEB-001] probe refuses invalid binding, unavailable transport and remote synthetic trust"
                refusals
        ]
    |> testSequenced
