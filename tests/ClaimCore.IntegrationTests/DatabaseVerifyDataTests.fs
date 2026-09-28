module ClaimCore.IntegrationTests.DatabaseVerifyDataTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.FixtureEnvironment
open ClaimCore.IntegrationTests.FixturePrivateFiles

let private databaseDll () =
    let rec find (directory: DirectoryInfo | null) =
        match directory with
        | null -> failtest "Repository root is absent."
        | current when File.Exists(Path.Combine(current.FullName, "ClaimCore.slnx")) ->
            Path.Combine(
                current.FullName,
                "artifacts/bin/ClaimCore.Database/release/ClaimCore.Database.dll"
            )
        | current -> find current.Parent

    find (DirectoryInfo AppContext.BaseDirectory)

let internal runCommand command arguments files =
    let start = ProcessStartInfo("dotnet")
    start.ArgumentList.Add(databaseDll ())
    start.ArgumentList.Add(command)

    for argument in arguments do
        start.ArgumentList.Add(argument)

    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false

    for name, path in files do
        start.Environment[name] <- path

    use runner =
        match Process.Start start with
        | null -> failtest "Owner data-audit process did not start."
        | started -> started

    let stdout = runner.StandardOutput.ReadToEndAsync()
    let stderr = runner.StandardError.ReadToEndAsync()

    if not (runner.WaitForExit(120000)) then
        runner.Kill(true)
        failtest "Owner data-audit process timed out."

    let output = if runner.ExitCode = 0 then stdout.Result else stderr.Result

    runner.ExitCode, JsonDocument.Parse(output)

let private run files = runCommand "verify-data" [] files

let private assertTopologyRefused directory (owner: string) inputs =
    let sameHost = NpgsqlConnectionStringBuilder(owner)
    sameHost.Username <- "claimcore_witness_writer"
    let file = Path.Combine(directory, "same-host-witness.connection")
    privateFile directory file sameHost.ConnectionString

    let replaced =
        inputs
        |> List.map (fun (name, path) ->
            if name = "CLAIMCORE_WITNESS_CONNECTION_FILE" then
                name, file
            else
                name, path)

    let code, result = run replaced
    use result = result
    Expect.equal code 3 "A same-host witness is not admitted for an owner audit."

    Expect.equal
        (result.RootElement
            .GetProperty("diagnostic")
            .GetProperty("parameters")
            .GetProperty("category")
            .GetString())
        "TOPOLOGY_REFUSED"
        "Topology refusal is not mislabeled as corrupt accepted evidence."

let internal files directory owner writer (witness: WitnessProtocol) =
    let ownerPath = Path.Combine(directory, "owner.connection")
    let writerPath = Path.Combine(directory, "witness.connection")
    let ringPath = Path.Combine(directory, "witness.keys")
    privateFile directory ownerPath owner
    privateFile directory writerPath writer
    let keyId, _ = witness.EvidenceStore.ReadKeyCheck()
    let material = witnessKey ()

    try
        JsonSerializer.Serialize(
            {|
                version = 1
                activeKeyId = keyId
                keys =
                    [|
                        {|
                            id = keyId
                            materialBase64 = Convert.ToBase64String(material)
                        |}
                    |]
            |}
        )
        |> privateFile directory ringPath
    finally
        CryptographicOperations.ZeroMemory(material)

    [
        "CLAIMCORE_ADMIN_CONNECTION_FILE", ownerPath
        "CLAIMCORE_WITNESS_CONNECTION_FILE", writerPath
        "CLAIMCORE_WITNESS_KEY_FILE", ringPath
        "CLAIMCORE_SUPPRESSION_KEY_FILE", suppressionKeyFile ()
    ]

let private acceptedCase owner app writer witness =
    let principal = human "verify-data-owner"
    provision owner witness principal |> applied
    use source = RuntimeDataSource.create app
    let registry = new ActorGrantRegistry(source, witness)
    let grants = new ActorGrantStore(source)

    registry.SetGrant(
        principal,
        actorId grants principal,
        {
            Role = Role.CaseEditor
            Scope = GrantScope.Installation
        },
        true
    )
    |> await
    |> applied

    use runtime =
        Runtime.OpenPostgres(
            app,
            writer,
            witnessKey (),
            suppressionKeyFile (),
            artifactKeyRingFile (),
            CancellationToken.None
        )
        |> await
        |> accepted

    let request =
        openRequest (Guid.NewGuid()) ("AUDIT-COMMAND-" + Guid.NewGuid().ToString("N"))

    match (runtime.ForActor principal).Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> request.CaseReference
    | _ -> failtest "One synthetic case must be accepted for owner audit."

let private tamper owner reference =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.cases SET claimant_name='Synthetic changed claimant' "
            + "WHERE case_reference=@reference",
            connection
        )

    command.Parameters.AddWithValue("reference", reference) |> ignore
    Expect.equal (command.ExecuteNonQuery()) 1 "One isolated projection changed."

let private assertPendingIntent (witness: WitnessProtocol) inputs expectedCaseTips =
    witness.BeginAuthority(Guid.NewGuid(), [| 0x43uy; 0x43uy; 0x50uy |], None)
    |> ignore

    let code, result = run inputs
    use result = result
    Expect.equal code 0 "An orphan intent has a typed audit report."
    let root = result.RootElement

    Expect.equal
        (root.GetProperty("status").GetString())
        "VERIFIED_WITH_PENDING_INTENTS"
        "An orphan is not a clean restore certificate."

    Expect.equal
        (root.GetProperty("counts").GetProperty("pendingIntents").GetString())
        "1"
        "Uncertain authority remains counted."

    Expect.equal
        (root.GetProperty("verifiedCaseTipsSha256").GetString())
        expectedCaseTips
        "A pending intent does not change verified current-case tips."

let private assertVerified (root: JsonElement) =
    Expect.equal (root.GetProperty("status").GetString()) "VERIFIED" "Exact safe status"

    Expect.equal
        (root.GetProperty("scope").GetString())
        "CURRENT_PRIMARY_AND_WITNESS"
        "A current-pair audit is not an external freshness or restore certificate."

    Expect.equal
        (root.GetProperty("counts").GetProperty("cases").GetString())
        "1"
        "One case audited"

    Expect.equal
        (root.GetProperty("counts").GetProperty("acceptedOperations").GetString())
        "1"
        "One accepted event audited"

    let digest =
        root.GetProperty("verifiedCaseTipsSha256").GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Verified case-tip digest is absent.")

    Expect.isTrue
        (Regex.IsMatch(digest, "^[0-9a-f]{64}$"))
        "The complete verified-case-tip digest is bounded and payload-free."

    digest

let private verifiesAndQuarantines =
    testCase
        "[CC-AUDIT-001] owner verify-data reports full counts and quarantines a changed case"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun owner app writer witness ->
                let reference = acceptedCase owner app writer witness

                let directory =
                    Path.Combine(
                        canonicalRoot (),
                        "claimcore-verify-data-" + Guid.NewGuid().ToString("N")
                    )

                try
                    let inputs = files directory owner writer witness
                    let code, verified = run inputs
                    use verified = verified

                    if code <> 0 then
                        let diagnostic =
                            verified.RootElement
                                .GetProperty("diagnostic")
                                .GetProperty("id")
                                .GetString()

                        failtestf "Owner full audit safe failure category: %s" diagnostic

                    Expect.equal code 0 "Owner full audit completed."
                    let root = verified.RootElement
                    let caseTips = assertVerified root

                    Expect.isFalse
                        (root.GetRawText().Contains(reference, StringComparison.Ordinal))
                        "An audit report must not disclose the synthetic case reference."

                    assertTopologyRefused directory owner inputs
                    assertPendingIntent witness inputs caseTips
                    tamper owner reference
                    let deniedCode, denied = run inputs
                    use denied = denied
                    Expect.equal deniedCode 3 "Changed projection refuses audit completion."

                    Expect.equal
                        (denied.RootElement.GetProperty("status").GetString())
                        "QUARANTINED"
                        "No false verified report"

                    Expect.equal
                        (denied.RootElement
                            .GetProperty("diagnostic")
                            .GetProperty("parameters")
                            .GetProperty("category")
                            .GetString())
                        "EVIDENCE_DIVERGENCE"
                        "A changed accepted projection is classified distinctly from unavailable infrastructure."
                finally
                    if Directory.Exists(directory) then
                        Directory.Delete(directory, true)))

let tests = testList "owner full data audit command" [ verifiesAndQuarantines ]
