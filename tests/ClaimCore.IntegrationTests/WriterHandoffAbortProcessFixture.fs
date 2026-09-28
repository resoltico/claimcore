module internal ClaimCore.IntegrationTests.WriterHandoffAbortProcessFixture

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.Fixtures

let private auditFor (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessAuditConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private inputFiles
    directory
    owner
    (app: string)
    writer
    (witness: WitnessProtocol)
    (ownerWitness: string)
    =
    let baseFiles = files directory owner writer witness

    let appFile =
        privateBytes directory "primary-app.connection" (Encoding.UTF8.GetBytes(app))

    let auditFile =
        privateBytes
            directory
            "witness-auditor.connection"
            (Encoding.UTF8.GetBytes(auditFor writer))

    let ownerFile =
        privateBytes directory "witness-owner.connection" (Encoding.UTF8.GetBytes(ownerWitness))

    let capability =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic capability file is absent.")

    baseFiles
    @ [
        "CLAIMCORE_CONNECTION_FILE", appFile
        "CLAIMCORE_WITNESS_AUDIT_CONNECTION_FILE", auditFile
        "CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE", ownerFile
        "CLAIMCORE_WRITER_CAPABILITY_FILE", capability
    ]

let private invoke files candidate signatureOne signatureTwo =
    let code, response =
        runCommand "abort-writer-handoff" [ candidate; signatureOne; signatureTwo ] files

    use response = response
    code, response.RootElement.Clone()

let draftCandidate
    (owner: string)
    (app: string)
    (writer: string)
    (witness: WitnessProtocol)
    (ownerWitness: string)
    (handoffId: Guid)
    (keyOne: Guid)
    (keyTwo: Guid)
    =
    let directory = privateRoot ()

    try
        let files = inputFiles directory owner app writer witness ownerWitness
        let output = Path.Combine(directory, "abort-draft.candidate")

        let arguments =
            [ handoffId.ToString("D"); keyOne.ToString("D"); keyTwo.ToString("D"); output ]

        let code, response = runCommand "draft-writer-handoff-abort" arguments files
        use response = response
        Expect.equal code 0 "Owner process writes exact private abort draft."

        Expect.equal
            (response.RootElement.GetProperty("operationOutcome").GetString())
            "COMPLETED"
            "Owner draft is a definite create-only file action."

        let mode = File.GetUnixFileMode(output)

        Expect.equal
            mode
            (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            "Draft candidate is owner-private 0600."

        let canonical = File.ReadAllBytes(output)
        let retry, refusal = runCommand "draft-writer-handoff-abort" arguments files
        use refusal = refusal
        Expect.notEqual retry 0 "Existing candidate path is never overwritten."

        Expect.isFalse
            (refusal.RootElement.GetRawText().Contains(output, StringComparison.Ordinal))
            "Output path is not reflected in diagnostics."

        let sameKey =
            [
                handoffId.ToString("D")
                keyOne.ToString("D")
                keyOne.ToString("D")
                Path.Combine(directory, "same-key.candidate")
            ]

        let duplicate, problem = runCommand "draft-writer-handoff-abort" sameKey files
        use problem = problem
        Expect.notEqual duplicate 0 "One signer key cannot draft both approvals."
        canonical
    finally
        Directory.Delete(directory, true)

let private assertEndpoints (owner: string) (app: string) (writer: string) (ownerWitness: string) =
    let primary = OwnerConnection.builder owner
    let runtime = NpgsqlConnectionStringBuilder(app)
    let auditor = NpgsqlConnectionStringBuilder(auditFor writer)
    let administrative = NpgsqlConnectionStringBuilder(ownerWitness)
    Expect.isTrue (runtime.Username = "claimcore_app") "Process app role is exact."

    Expect.isTrue
        (runtime.Host = primary.Host && runtime.Port = primary.Port)
        "Process app endpoint matches primary."

    Expect.isTrue (runtime.Database = primary.Database) "Process app database matches primary."

    Expect.isTrue
        (auditor.Username = "claimcore_witness_auditor")
        "Process witness auditor role is exact."

    Expect.isTrue
        (administrative.Username = "claimcore_witness_owner")
        "Process witness owner role is exact."

    Expect.isTrue
        (auditor.Host = administrative.Host && auditor.Port = administrative.Port)
        "Process auditor endpoint matches owner."

    Expect.isTrue
        (auditor.Database = administrative.Database)
        "Process auditor database matches owner."

let verifyExactRetry
    (owner: string)
    (app: string)
    (writer: string)
    (witness: WitnessProtocol)
    (ownerWitness: string)
    canonical
    signatureOne
    signatureTwo
    =
    assertEndpoints owner app writer ownerWitness

    let directory = privateRoot ()

    try
        let candidatePath = privateBytes directory "abort.candidate" canonical
        let onePath = privateBytes directory "owner-one.signature" signatureOne
        let twoPath = privateBytes directory "owner-two.signature" signatureTwo
        let files = inputFiles directory owner app writer witness ownerWitness
        let code, response = invoke files candidatePath onePath twoPath

        if code <> 0 then
            let diagnostic =
                match response.TryGetProperty("diagnostic") with
                | true, value -> value.GetProperty("id").GetString()
                | _ -> "NONE"

            failtestf "Exact owner-process abort retry failed safely: %s" diagnostic

        Expect.equal
            (response.GetProperty("operationOutcome").GetString())
            "COMPLETED"
            "Exact A3-released pair is definite."

        Expect.isFalse
            (response.GetRawText().Contains(candidatePath, StringComparison.Ordinal))
            "Private input path is not a diagnostic."

        let missing = Path.Combine(directory, "missing.candidate")
        let refused, reason = invoke files missing onePath twoPath
        Expect.notEqual refused 0 "Missing private abort file is refused."

        Expect.isFalse
            (reason.GetRawText().Contains(missing, StringComparison.Ordinal))
            "Missing private path is not reflected."

        let link = Path.Combine(directory, "linked.candidate")
        File.CreateSymbolicLink(link, candidatePath) |> ignore
        let linked, _ = invoke files link onePath twoPath
        Expect.notEqual linked 0 "Linked private abort candidate is refused."
    finally
        Directory.Delete(directory, true)
