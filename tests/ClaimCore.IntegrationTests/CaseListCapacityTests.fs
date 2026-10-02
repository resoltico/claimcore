module ClaimCore.IntegrationTests.CaseListCapacityTests

open System
open System.Text.Json
open Expecto
open Npgsql
open NpgsqlTypes
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.FreshBaselineSupport

// Projection-only fixtures isolate query filtering/plans. Witnessed workflow capacity is separate.
let private actor = Guid.Parse("90000000-0000-4000-8000-000000000001")

let private populate (connection: NpgsqlConnection) =
    use command =
        new NpgsqlCommand(
            """
INSERT INTO claimcore.cases(case_id,incident_date,incident_notification_date,incident_country,
claimant_name,insurer_name,claimed_amount,claimed_currency,case_reference,status,revision)
SELECT gen_random_uuid(),DATE '2026-01-01',DATE '2026-01-02','Synthetic','Synthetic','Synthetic',
1,'EUR','CAP-'||lpad(n::text,8,'0'),'OPENED',1 FROM generate_series(1,100000) n;
INSERT INTO claimcore.actors VALUES
('90000000-0000-4000-8000-000000000001','HUMAN','https://synthetic.example','capacity-reader',true,1);
INSERT INTO claimcore.actor_grants
SELECT '90000000-0000-4000-8000-000000000001','CASE',c.case_id,r.role,true,1
FROM claimcore.cases c CROSS JOIN (VALUES ('CASE_READER'),('CASE_EDITOR')) r(role)
WHERE right(c.case_reference,4)='0000';
ANALYZE claimcore.cases; ANALYZE claimcore.actors; ANALYZE claimcore.actor_grants;
""",
            connection
        )

    command.CommandTimeout <- 120
    command.ExecuteNonQuery() |> ignore

let private query (connection: NpgsqlConnection) prefix after window issuer =
    let command = new NpgsqlCommand(prefix + Sql.listVisibleCases, connection)
    Sql.optional command "after" NpgsqlDbType.Text after
    Sql.uuid command "actor" actor
    Sql.text command "kind" "HUMAN"
    Sql.text command "issuer" issuer
    Sql.text command "principal" "capacity-reader"

    Sql.add
        command
        "roles"
        (NpgsqlDbType.Array ||| NpgsqlDbType.Text)
        (box [| "CASE_READER"; "CASE_EDITOR" |])

    Sql.add command "window" NpgsqlDbType.Integer (box window)
    command

let private references connection after window issuer =
    use command = query connection "" after window issuer
    use reader = command.ExecuteReader()
    let result = ResizeArray<string>()

    while reader.Read() do
        result.Add(reader.GetString(reader.GetOrdinal("case_reference")))

    List.ofSeq result

let private sharedWork connection after =
    use command =
        query
            connection
            "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
            after
            6
            "https://synthetic.example"

    let json =
        match command.ExecuteScalar() with
        | :? string as value -> value
        | _ -> failtest "Expected an explain document."

    use plan = JsonDocument.Parse(json)
    let node = plan.RootElement[0].GetProperty("Plan")

    node.GetProperty("Shared Hit Blocks").GetInt64()
    + node.GetProperty("Shared Read Blocks").GetInt64()

let private execute (connection: NpgsqlConnection) sql =
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private sparseAccessUsesGrantedKeys () =
    withDatabase (fun owner _ ->
        initialize owner
        use connection = new NpgsqlConnection(owner)
        connection.Open()
        populate connection
        let expected = [ for n in 1..6 -> sprintf "CAP-%08d" (n * 10000) ]

        Expect.isTrue
            (references connection None 6 "https://synthetic.example" = expected)
            "Roles do not duplicate visible cases."

        let continuation = Some "CAP-00050000"
        let tail = [ for n in 6..10 -> sprintf "CAP-%08d" (n * 10000) ]

        Expect.isTrue
            (references connection continuation 6 "https://synthetic.example" = tail)
            "Sparse continuation has no skipped or duplicate rows."

        Expect.isLessThan
            (sharedWork connection None)
            1000L
            "A sparse page does not scan all hidden case projections."

        Expect.isLessThan
            (sharedWork connection continuation)
            1000L
            "Later sparse pages keep selective work."

        Expect.equal
            (references connection None 6 "https://foreign.example" |> List.length)
            0
            "Foreign principal identity sees no projection."

        execute
            connection
            "UPDATE claimcore.cases SET disposition='VOIDED_DATA_ENTRY_ERROR' WHERE case_reference='CAP-00020000'; UPDATE claimcore.cases SET privacy_phase='ERASURE_REQUESTED' WHERE case_reference='CAP-00040000'; UPDATE claimcore.actor_grants SET active=false WHERE scope_case_id=(SELECT case_id FROM claimcore.cases WHERE case_reference='CAP-00060000')"

        Expect.equal
            (references connection None 20 "https://synthetic.example" |> List.length)
            7
            "Disposition, privacy and inactive grants filter before paging."

        execute connection "UPDATE claimcore.actors SET enabled=false"

        Expect.equal
            (references connection None 20 "https://synthetic.example" |> List.length)
            0
            "Disabled actors see no cases.")

let private installationAccessKeepsOrderedWindow () =
    withDatabase (fun owner _ ->
        initialize owner
        use connection = new NpgsqlConnection(owner)
        connection.Open()
        populate connection

        execute
            connection
            "INSERT INTO claimcore.actor_grants VALUES('90000000-0000-4000-8000-000000000001','INSTALLATION','00000000-0000-0000-0000-000000000000','CASE_READER',true,1)"

        let expected = [ for n in 50001..50006 -> sprintf "CAP-%08d" n ]
        let continuation = Some "CAP-00050000"

        Expect.isTrue
            (references connection continuation 6 "https://synthetic.example" = expected)
            "Installation scope and case grants form one ordered page."

        Expect.isLessThan
            (sharedWork connection continuation)
            1000L
            "Installation continuation uses its ordered window.")

let tests =
    testList
        "case-list capacity"
        [
            testCase
                "[CC-AUTH-001] sparse actor access selects indexed granted cases at volume"
                sparseAccessUsesGrantedKeys
            testCase
                "[CC-AUTH-001] installation access keeps bounded ordered work at volume"
                installationAccessKeepsOrderedWindow
        ]
