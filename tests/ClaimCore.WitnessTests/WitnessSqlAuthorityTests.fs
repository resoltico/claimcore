module ClaimCore.WitnessTests.WitnessSqlAuthorityTests

open System
open Expecto
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness
open ClaimCore.WitnessTests.WitnessTestSupport

open ClaimCore.WitnessTests.WitnessSqlAuthoritySupport

let private roleDenials =
    testCase
        "[CC-WIT-001] every owner entry and private helper denies actual writer and auditor execution"
        (fun _ ->
            fixtureRoles (fun owner writer auditor identity capability ->
                use store = new Store(writer, identity, capability)
                store.Admit(cancellation) |> await
                let calls = ownerCalls owner

                Expect.isGreaterThan
                    calls.Length
                    0
                    "Actual native catalog supplies a nonempty helper inventory"

                for role in [ writer; auditor ] do
                    for sql in calls do
                        denied role sql

                    denied role "SET ROLE claimcore_witness_owner"

                Expect.equal
                    (scalar<int64> auditor "SELECT count(*) FROM claimcore_witness.journal")
                    0L
                    "Auditor retains SELECT"))

let private privilegeDrift =
    testCase
        "[CC-WIT-001] helper execute and composite usage drift close admission and invoker authority remains confined"
        (fun _ ->
            fixtureRoles (fun owner writer _ identity capability ->
                use store = new Store(writer, identity, capability)
                store.Admit(cancellation) |> await

                let grant =
                    "ON FUNCTION claimcore_witness.validate_journal_tip(claimcore_witness.journal_tip) "

                run owner ("GRANT EXECUTE " + grant + "TO claimcore_witness_writer")

                Expect.throws
                    (fun () -> store.Admit(cancellation) |> await)
                    "Unexpected helper grant refuses"

                run
                    owner
                    "GRANT USAGE ON TYPE claimcore_witness.journal_tip TO claimcore_witness_writer"

                requireInvokerDenied
                    writer
                    "SELECT claimcore_witness.validate_journal_tip(NULL::claimcore_witness.journal_tip)"

                run
                    owner
                    "REVOKE USAGE ON TYPE claimcore_witness.journal_tip FROM claimcore_witness_writer"

                run owner ("REVOKE EXECUTE " + grant + "FROM claimcore_witness_writer")
                store.Admit(cancellation) |> await

                run
                    owner
                    "GRANT USAGE ON TYPE claimcore_witness.journal_tip TO claimcore_witness_auditor"

                Expect.throws
                    (fun () -> store.Admit(cancellation) |> await)
                    "Unexpected type usage refuses"

                run
                    owner
                    "REVOKE USAGE ON TYPE claimcore_witness.journal_tip FROM claimcore_witness_auditor"

                store.Admit(cancellation) |> await
                run owner "ALTER TYPE claimcore_witness.journal_tip ADD ATTRIBUTE forged boolean"

                Expect.throws
                    (fun () -> store.Admit(cancellation) |> await)
                    "Changed composite members refuse"))

let private roleMembership =
    testCase "[CC-WIT-001] owner role membership closes ordinary witness admission" (fun _ ->
        fixture (fun owner writer identity capability ->
            use store = new Store(writer, identity, capability)
            store.Admit(cancellation) |> await
            run owner "GRANT claimcore_witness_owner TO claimcore_witness_writer"

            Expect.throws
                (fun () -> store.Admit(cancellation) |> await)
                "Owner membership is not runtime authority"))

let private insertRollback =
    testCase
        "[CC-WIT-001] failure after journal insert rolls back payload and tip without consuming sequence"
        (fun _ ->
            fixture (fun owner writer identity capability ->
                run
                    owner
                    "CREATE FUNCTION public.refuse_payload() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN RAISE EXCEPTION 'synthetic payload boundary refusal'; END$$; CREATE TRIGGER refuse_payload BEFORE INSERT ON claimcore_witness.journal_payloads FOR EACH ROW EXECUTE FUNCTION public.refuse_payload();"

                use connection = new NpgsqlConnection(writer)
                connection.Open()

                use command =
                    appendCommand
                        connection
                        identity
                        capability
                        (Guid.NewGuid())
                        (identityRow, installationSubject)

                Expect.throwsT<PostgresException>
                    (fun () -> command.ExecuteScalar() |> ignore)
                    "Payload boundary refuses after journal insertion"

                Expect.equal
                    (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal")
                    0L
                    "Journal rolled back"

                Expect.equal
                    (scalar<int64> owner "SELECT count(*) FROM claimcore_witness.journal_payloads")
                    0L
                    "Payload absent"

                Expect.equal
                    (scalar<int64> owner "SELECT tip_sequence FROM claimcore_witness.installation")
                    0L
                    "Tip unchanged"

                run
                    owner
                    "DROP TRIGGER refuse_payload ON claimcore_witness.journal_payloads; DROP FUNCTION public.refuse_payload();"

                use store = new Store(writer, identity, capability)

                let ticket =
                    store.Append(Guid.NewGuid(), None, Intent, keyId, payload 2uy, cancellation)
                    |> await

                Expect.equal ticket.Sequence 1L "A valid append still receives the first sequence"))

let private compositePresence =
    testCase
        "[CC-WIT-001] whole and partial null proofs refuse exact retry while settlement inherits nullable subject"
        (fun _ ->
            fixture (fun owner writer identity capability ->
                use store = new Store(writer, identity, capability)
                let operation = Guid.NewGuid()

                store.Append(operation, None, Intent, keyId, payload 1uy, cancellation)
                |> await
                |> ignore

                use connection = new NpgsqlConnection(writer)
                connection.Open()

                assertIncompleteProofs connection identity capability operation

                Expect.equal
                    (scalar<int64> owner "SELECT tip_sequence FROM claimcore_witness.installation")
                    1L
                    "Refusals preserve the accepted intent"

                let settled =
                    store.Append(
                        operation,
                        None,
                        SettledAccepted,
                        keyId,
                        payload 2uy,
                        cancellation
                    )
                    |> await

                Expect.equal
                    settled.Sequence
                    2L
                    "Settlement inherits its original installation subject"))

[<Tests>]
let tests =
    testList
        "witness SQL authority"
        [
            roleDenials
            privilegeDrift
            roleMembership
            insertRollback
            compositePresence
        ]
