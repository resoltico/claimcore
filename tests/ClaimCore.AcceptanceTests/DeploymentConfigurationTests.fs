module ClaimCore.AcceptanceTests.DeploymentConfigurationTests

open System.Diagnostics
open System.IO
open Expecto
open ClaimCore.TestSupport

let private run root owned cleanup =
    let start = ProcessStartInfo("node", UseShellExecute = false)
    start.WorkingDirectory <- root
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add(Path.Combine(root, "web/e2e/deployment-qualification.mjs"))
    start.ArgumentList.Add root
    start.ArgumentList.Add owned
    // A broken environment scrub must fail against this owned decoy, never an operator installation.
    start.Environment["CLAIMCORE_CONFIG_DIR"] <- Path.Combine(owned, "unselected-configuration")
    start.Environment["CLAIMCORE_UID"] <- "999999"
    start.Environment["CLAIMCORE_GID"] <- "999999"
    start.Environment["COMPOSE_PROJECT_NAME"] <- "claimcore-unselected-decoy"

    if cleanup then
        start.ArgumentList.Add "cleanup"

    BoundedProcess.run start None 1_048_576 (if cleanup then 300_000 else 1_500_000)

let private retainFailure owned name (error: exn) =
    match error with
    | :? BoundedProcessFailure as failure ->
        FixtureFiles.write owned (name + ".stdout") failure.StandardOutput |> ignore
        FixtureFiles.write owned (name + ".stderr") failure.StandardError |> ignore
    | _ -> ()

let private protectedRun root owned =
    try
        run root owned false
    with original ->
        retainFailure owned "deployment" original

        try
            let cleanup = run root owned true

            if cleanup.ExitCode <> 0 then
                invalidOp "Owned deployment cleanup did not complete."
        with cleanupFailure ->
            retainFailure owned "cleanup" cleanupFailure
            raise (System.AggregateException(original, cleanupFailure))

        reraise ()

let private freshDeployment () =
    async {
        let root = RepositoryRoot.find ()
        let owned = FixtureFiles.directory ()
        let result = protectedRun root owned
        FixtureFiles.write owned "deployment.stdout" result.StandardOutput |> ignore
        FixtureFiles.write owned "deployment.stderr" result.StandardError |> ignore

        if result.ExitCode <> 0 then
            let cleanup = run root owned true
            FixtureFiles.write owned "cleanup.stderr" cleanup.StandardError |> ignore

            Expect.equal
                cleanup.ExitCode
                0
                ("Owned deployment cleanup must finish; private evidence: " + owned)

        Expect.equal
            result.ExitCode
            0
            ("Fresh Compose qualification must finish; private evidence: " + owned)

        Expect.equal
            (File.ReadAllText(Path.Combine(owned, "deployment.progress")))
            "qualified\n"
            "A real owner login, restart and retained cookie/antiforgery exchange completed"

        Expect.isFalse
            (Directory.Exists(Path.Combine(owned, "unselected-configuration")))
            "Ambient Compose interpolation never selects or creates the owned decoy configuration"

        Directory.Delete(owned, true)
    }

[<Tests>]
let tests =
    testCaseAsync
        "[CC-WEB-001][CC-DB-002] fresh Compose owner login and session restart use generated private inputs"
        (freshDeployment ())
