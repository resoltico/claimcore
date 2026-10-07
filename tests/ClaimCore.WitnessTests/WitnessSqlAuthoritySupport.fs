module internal ClaimCore.WitnessTests.WitnessSqlAuthoritySupport

open System
open Expecto
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness
open ClaimCore.WitnessTests.WitnessTestSupport

let ownerCalls owner =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT format('SELECT * FROM claimcore_witness.%I(%s)',p.proname,"
            + "coalesce((SELECT string_agg(format('NULL::%s',format_type(t,NULL)),',') "
            + "FROM unnest(p.proargtypes) t),'')) FROM pg_proc p "
            + "JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='claimcore_witness' "
            + "AND p.proname NOT IN ('append','acquire_read_fence') "
            + "AND p.prorettype<>'trigger'::regtype ORDER BY p.proname",
            connection
        )

    use reader = command.ExecuteReader()
    let calls = ResizeArray<string>()

    while reader.Read() do
        calls.Add(reader.GetString(0))

    calls |> Seq.toList

let denied connection sql =
    try
        run connection sql
        failtest "An ordinary witness role executed owner-only SQL"
    with :? PostgresException as error ->
        Expect.equal error.SqlState PostgresErrorCodes.InsufficientPrivilege "Native EXECUTE denied"

let appendCommand
    (connection: NpgsqlConnection)
    (identity: Identity)
    capability
    operation
    (identitySql, subjectSql)
    =
    let command =
        new NpgsqlCommand(
            "SELECT sequence FROM claimcore_witness.append("
            + identitySql
            + ","
            + subjectSql
            + ",ROW(@operation,'INTENT',@key,@payload)::claimcore_witness.journal_request,@cap)",
            connection
        )

    command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
    |> ignore

    command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
    |> ignore

    command.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, identity.Epoch)
    |> ignore

    command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operation)
    |> ignore

    command.Parameters.AddWithValue("key", NpgsqlDbType.Uuid, keyId) |> ignore

    command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, payload 1uy)
    |> ignore

    command.Parameters.AddWithValue("cap", NpgsqlDbType.Bytea, capability) |> ignore
    command

let identityRow =
    "ROW(@installation,@lineage,@epoch)::claimcore_witness.installation_identity"

let installationSubject =
    "ROW('INSTALLATION',NULL)::claimcore_witness.journal_subject"

let requireInvokerDenied connection sql =
    try
        run connection sql
        failtest "A writer executed an owner-context helper"
    with :? PostgresException as error ->
        Expect.equal error.SqlState PostgresErrorCodes.RaiseException "Invoker guard refused"

        Expect.equal
            error.MessageText
            "private witness function requires schema owner"
            "Effective owner required"

let assertIncompleteProofs (connection: NpgsqlConnection) identity capability operation =
    for proof in
        [
            "NULL::claimcore_witness.installation_identity"
            "ROW(@installation,NULL,@epoch)::claimcore_witness.installation_identity"
            "ROW(NULL,@lineage,@epoch)::claimcore_witness.installation_identity"
        ] do
        use command =
            appendCommand connection identity capability operation (proof, installationSubject)

        Expect.throwsT<PostgresException>
            (fun () -> command.ExecuteScalar() |> ignore)
            "Incomplete identity refuses before retry"

    use invalidSubject =
        appendCommand
            connection
            identity
            capability
            operation
            (identityRow, "ROW(NULL,NULL)::claimcore_witness.journal_subject")

    Expect.throwsT<PostgresException>
        (fun () -> invalidSubject.ExecuteScalar() |> ignore)
        "Null INTENT scope refuses"
