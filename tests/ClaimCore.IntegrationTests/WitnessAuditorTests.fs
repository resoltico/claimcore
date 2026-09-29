module ClaimCore.IntegrationTests.WitnessAuditorTests

open System
open System.Security.Cryptography
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures

let internal auditorFor (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessAuditConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let private checkConstrainedReadPool writer (witness: WitnessProtocol) raw =
    let constrained = NpgsqlConnectionStringBuilder(writer)
    constrained.MaxPoolSize <- 1
    use limited = new Store(constrained.ConnectionString, witness.Identity, raw)
    limited.Admit()
    use _readFence = limited.AcquireReadFence(witness.Snapshot().WriterGeneration)

    Expect.equal
        (limited.Snapshot().TipSequence)
        (witness.Snapshot().TipSequence)
        "A held read fence cannot exhaust the snapshot connection pool."

let private noWriterAuthority _ _ writer (witness: WitnessProtocol) =
    let auditorConnection = auditorFor writer
    use store = Store.OpenAudit(auditorConnection, witness.Identity)
    store.AdmitReadOnly()

    Expect.equal
        (store.Snapshot().TipSequence)
        (witness.Snapshot().TipSequence)
        "Auditor reads the exact current witness tip."

    Expect.throwsT<InvalidOperationException>
        (fun () -> store.Admit())
        "Auditor cannot admit case-work writer authority."

    Expect.throwsT<InvalidOperationException>
        (fun () -> store.AcquireReadFence(1L) |> ignore)
        "Auditor cannot hold a writer-generation lease."

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            store.Append(Guid.NewGuid(), None, Intent, Guid.NewGuid(), [| 0x43uy |])
            |> ignore)
        "Auditor append is refused before SQL."

    use connection = new NpgsqlConnection(auditorConnection)
    connection.Open()

    use privilege =
        new NpgsqlCommand(
            "SELECT has_table_privilege(current_user,'claimcore_witness.journal','SELECT'),"
            + "has_table_privilege(current_user,'claimcore_witness.journal','INSERT'),"
            + "has_table_privilege(current_user,'claimcore_witness.journal','UPDATE'),"
            + "has_table_privilege(current_user,'claimcore_witness.journal','DELETE'),"
            + "has_function_privilege(current_user,"
            + "'claimcore_witness.append(uuid,uuid,bigint,uuid,text,uuid,text,uuid,bytea,bytea)',"
            + "'EXECUTE')",
            connection
        )

    use reader = privilege.ExecuteReader()
    Expect.isTrue (reader.Read()) "Auditor ACL row exists."
    Expect.isTrue (reader.GetBoolean(0)) "Auditor may read witness evidence."

    for index in 1..4 do
        Expect.isFalse (reader.GetBoolean(index)) "Auditor has no mutation privilege."

    let capabilityPath =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic capability path is unavailable.")

    let raw = IO.File.ReadAllBytes(capabilityPath)

    try
        checkConstrainedReadPool writer witness raw
        use wrongMode = new Store(auditorConnection, witness.Identity, raw)

        Expect.throwsT<InvalidOperationException>
            (fun () -> wrongMode.Admit())
            "Supplying a writer token cannot turn the auditor login into a writer."
    finally
        CryptographicOperations.ZeroMemory(raw)

let tests =
    testList
        "witness auditor"
        [
            testCase
                "[CC-WIT-001] audit-only credential reads evidence but cannot append or admit case work"
                (fun _ -> withAuthorityRuntimeDatabase noWriterAuthority)
        ]
