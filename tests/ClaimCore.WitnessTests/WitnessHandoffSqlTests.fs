module ClaimCore.WitnessTests.WitnessHandoffSqlTests

open System
open Expecto
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness
open ClaimCore.WitnessTests.WitnessTestSupport

let private preparation (connection: NpgsqlConnection) (identity: Identity) capability =
    let command =
        new NpgsqlCommand(
            "SELECT sequence FROM claimcore_witness.prepare_writer_handoff("
            + "ROW(@installation,@lineage,@epoch)::claimcore_witness.installation_identity,"
            + "ROW(@handoff,1,sha256(decode(repeat('02',32),'hex')))::claimcore_witness.handoff_plan,"
            + "ROW(0,decode(repeat('00',32),'hex'))::claimcore_witness.journal_tip,"
            + "ROW(decode('01','hex'),decode(repeat('00',64),'hex'),@signer,@one,@two)::claimcore_witness.handoff_approval,"
            + "ROW(@key,decode('01','hex'),@cap)::claimcore_witness.writer_delivery)",
            connection
        )

    command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
    |> ignore

    command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
    |> ignore

    command.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, identity.Epoch)
    |> ignore

    for name in [ "handoff"; "signer"; "one"; "two" ] do
        command.Parameters.AddWithValue(name, NpgsqlDbType.Uuid, Guid.NewGuid())
        |> ignore

    command.Parameters.AddWithValue("key", NpgsqlDbType.Uuid, keyId) |> ignore
    command.Parameters.AddWithValue("cap", NpgsqlDbType.Bytea, capability) |> ignore
    command

[<Tests>]
let tests =
    testCase
        "[CC-WIT-001] prepared owner handoff retry binds its exact installation and historical ticket"
        (fun _ ->
            fixture (fun owner _ identity capability ->
                use connection = new NpgsqlConnection(owner)
                connection.Open()
                use command = preparation connection identity capability

                Expect.equal
                    (command.ExecuteScalar() :?> int64)
                    1L
                    "Fresh native owner preparation"

                Expect.equal
                    (command.ExecuteScalar() :?> int64)
                    1L
                    "Exact retry uses qualified historical sequence"

                for name in [ "installation"; "lineage"; "epoch" ] do
                    let parameter = command.Parameters[name]
                    let original = parameter.Value
                    parameter.Value <- if name = "epoch" then box 2L else box (Guid.NewGuid())

                    Expect.throwsT<PostgresException>
                        (fun () -> command.ExecuteScalar() |> ignore)
                        "Another identity cannot receive the old ticket"

                    parameter.Value <- original

                Expect.equal
                    (command.ExecuteScalar() :?> int64)
                    1L
                    "Original identity still receives its exact ticket"

                Expect.equal
                    (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                    1L
                    "No retry appended another authority row"))
