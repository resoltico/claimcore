module internal ClaimCore.IntegrationTests.GrowthEnvironment

open System
open System.Diagnostics
open System.Threading
open ClaimCore.Postgres

let private git (arguments: string) =
    let start = ProcessStartInfo("git", arguments)
    start.WorkingDirectory <- ClaimCore.TestSupport.RepositoryRoot.find ()
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    use runner = new Process(StartInfo = start)

    if not (runner.Start()) then
        invalidOp "Cannot record qualification source identity."

    let output = runner.StandardOutput.ReadToEnd()
    runner.WaitForExit()
    runner.ExitCode, output.Trim()

let source () =
    let status, revision = git "rev-parse HEAD"

    if status <> 0 then
        invalidOp "Cannot record qualification source revision."

    let changed, _ = git "diff --quiet HEAD"

    if changed <> 0 && changed <> 1 then
        invalidOp "Cannot record qualification source state."

    {|
        revision = revision
        trackedChanges = changed = 1
        operatingSystem = Runtime.InteropServices.RuntimeInformation.OSDescription
        architecture = string Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
        dotnet = Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        processors = Environment.ProcessorCount
    |}

let database (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT version(),temp_files,temp_bytes,(SELECT count(*) FROM claimcore.cases),(SELECT count(*) FROM claimcore.actors),(SELECT count(*) FROM claimcore.grants),(SELECT count(*) FROM claimcore.sessions) FROM pg_stat_database WHERE datname=current_database()"
                [||]
                CancellationToken.None

        return
            {|
                version = DatabaseSession.value<string> 0 rows.Head
                tempFiles = DatabaseSession.value<int64> 1 rows.Head
                tempBytes = DatabaseSession.value<int64> 2 rows.Head
                cases = DatabaseSession.value<int64> 3 rows.Head
                actors = DatabaseSession.value<int64> 4 rows.Head
                grants = DatabaseSession.value<int64> 5 rows.Head
                sessions = DatabaseSession.value<int64> 6 rows.Head
            |}
    }
