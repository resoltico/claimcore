module ClaimCore.AcceptanceTests.PublishedDiagnosticTests

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open ClaimCore.Wire
open Expecto

let private qualify () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        let canary = "private-storage-" + Guid.NewGuid().ToString("N")

        let connectionString =
            File.ReadAllText(Path.Combine(fixture.Directory, "owner.connection"))

        use source = NpgsqlDataSource.Create connectionString
        use! connection = source.OpenConnectionAsync()

        use identity =
            new NpgsqlCommand("SELECT installation_id FROM claimcore.installation", connection)

        let! installation = identity.ExecuteScalarAsync()
        let installation = unbox<Guid> installation

        let sql value =
            task {
                use command = new NpgsqlCommand(value, connection) in
                let! _ = command.ExecuteNonQueryAsync() in
                return ()
            }

        let! token = fixture.ServiceToken()

        do!
            sql
                $"""ALTER TABLE claimcore.baseline_identity RENAME TO diagnostic_identity; CREATE FUNCTION claimcore.diagnostic_failure() RETURNS text LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION '{canary}' USING DETAIL='provider-{canary}',HINT='path-{canary}',ERRCODE='XX000'; END; $$; CREATE VIEW claimcore.baseline_identity AS SELECT claimcore.diagnostic_failure() AS identifier,script_sha256 FROM claimcore.diagnostic_identity; GRANT SELECT ON claimcore.baseline_identity TO claimcore_runtime"""

        let call () =
            task {
                use request =
                    new HttpRequestMessage(HttpMethod.Post, fixture.Origin + "/api/case.get")

                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)
                request.Headers.Add("ClaimCore-Representation", "DATA")
                request.Headers.Add("ClaimCore-Installation", installation.ToString("D"))
                request.Headers.Add("X-Correlation-Id", canary)

                let body =
                    JsonSerializer.Serialize
                        {|
                            target =
                                {|
                                    kind = "REFERENCE"
                                    caseReference = canary
                                |}
                        |}

                request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                use! response = fixture.Http.SendAsync request
                let! body = response.Content.ReadAsStringAsync()

                Expect.equal
                    (int response.StatusCode)
                    200
                    "A known pre-commit storage failure has one public operation reply"

                Expect.stringContains body "FAILED" "Failure remains definite before COMMIT"

                Expect.isFalse
                    (body.Contains canary)
                    "Provider detail and submitted values never enter the reply"
            }

        let! _ = Task.WhenAll(call (), call ())

        do!
            sql
                "DROP VIEW claimcore.baseline_identity; ALTER TABLE claimcore.diagnostic_identity RENAME TO baseline_identity; DROP FUNCTION claimcore.diagnostic_failure()"
        // Disposal of the first published process drains the ordinary production sink to files.
        do! fixture.RestartWeb()
        let errors = File.ReadAllText(Path.Combine(fixture.Directory, "web.stderr"))
        let output = File.ReadAllText(Path.Combine(fixture.Directory, "web.stdout"))

        for secret in [ canary; token; connectionString; fixture.Directory ] do
            Expect.isFalse
                (errors.Contains secret || output.Contains secret)
                "No private canary in published process output"

        let entries =
            errors.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map JsonDocument.Parse

        try
            let requests =
                entries
                |> Array.filter (fun value ->
                    let found, endpoint = value.RootElement.TryGetProperty("endpoint") in
                    found && endpoint.GetString() = "case.get")

            Expect.equal requests.Length 2 "Both concurrent reads have owned log records"

            let correlations =
                requests
                |> Array.map (fun value -> value.RootElement.GetProperty("correlation").GetGuid())
                |> Set.ofArray

            Expect.equal
                correlations.Count
                2
                "Concurrent requests have isolated internal correlations"

            let storage =
                entries
                |> Array.filter (fun value ->
                    let found, kind = value.RootElement.TryGetProperty("kind") in

                    found
                    && kind.GetString() = "storageFailure"
                    && Set.contains
                        (value.RootElement.GetProperty("correlation").GetGuid())
                        correlations)

            Expect.equal
                storage.Length
                2
                "One primary event per failed published attempt, without test subscribers"

            let attempts =
                storage
                |> Array.map (fun value -> value.RootElement.GetProperty("attempt").GetGuid())
                |> Set.ofArray

            Expect.equal attempts.Count 2 "Storage attempt identities are distinct"

            for event in storage do
                let value = event.RootElement

                Expect.equal
                    (value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
                    (set [ "kind"; "phase"; "classification"; "attempt"; "correlation" ])
                    "Closed production diagnostic fields"

                Expect.equal
                    (value.GetProperty("phase").GetString())
                    "Read"
                    "Original provider phase"

                Expect.equal
                    (value.GetProperty("classification").GetString())
                    "Provider"
                    "Original provider class"

                Expect.contains
                    correlations
                    (value.GetProperty("correlation").GetGuid())
                    "Diagnostic joins its request"
        finally
            entries |> Array.iter _.Dispose()

        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "published production storage diagnostics are correlated bounded and secret-free"
        (async { do! qualify () |> Async.AwaitTask })
