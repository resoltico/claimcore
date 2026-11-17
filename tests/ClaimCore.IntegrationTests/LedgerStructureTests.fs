module ClaimCore.IntegrationTests.LedgerStructureTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application.Storage
open ClaimCore.TestSupport

let private token = CancellationToken.None

// Deliberately unregistered task/part tokens and opaque payload bytes exercise SQL structure only.
let private appendFunction =
    """
CREATE FUNCTION public.test_append_part(i uuid,a text,aggregate_id uuid,k text,part_id uuid,r bigint,ref_key text,erase text,erase_id uuid,private_content boolean)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE h claimcore.ledger_head%ROWTYPE; n bigint; effect jsonb; compartments jsonb;
BEGIN
 SELECT * INTO STRICT h FROM claimcore.ledger_head WHERE singleton;
 n:=h.sequence+1;
 effect:=jsonb_build_object('aggregate',jsonb_build_object('kind',a,'id',aggregate_id),'part',jsonb_build_object('kind',k,'id',part_id),'revision',r);
 IF ref_key IS NOT NULL THEN effect:=effect||jsonb_build_object('naturalKey',ref_key); END IF;
 IF erase IS NOT NULL THEN effect:=effect||jsonb_build_object('erases',jsonb_build_object('kind',erase,'id',erase_id)); END IF;
 compartments:='[]'::jsonb;
 IF private_content THEN
  effect:=effect||jsonb_build_object('compartment',0);
  compartments:=jsonb_build_array(jsonb_build_object('ordinal',0,'owner',jsonb_build_object('kind',CASE a WHEN 'FAMILY' THEN 'FAMILY' ELSE 'PROFILE' END,'id',aggregate_id),'commitment',repeat('11',32),'nonce',lpad(to_hex(n),64,'0'),'payload','7b7d'));
 END IF;
 PERFORM claimcore.append_operation(i,n,h.entry_hash,decode(lpad(to_hex(n),64,'0'),'hex'),6::smallint,gen_random_uuid(),'00000000-0000-4000-8000-000000000001',clock_timestamp(),CASE a WHEN 'PERSON' THEN NULL ELSE DATE '2026-11-16' END,a,'sample.change',jsonb_build_object('binding','{}'::jsonb,'retained','{}'::jsonb),jsonb_build_array(effect),compartments);
END;
$$;
GRANT EXECUTE ON FUNCTION public.test_append_part(uuid,text,uuid,text,uuid,bigint,text,text,uuid,boolean) TO claimcore_runtime;
"""

let private id number =
    Guid.Parse(sprintf "00000000-0000-4000-8000-%012d" number)

let private nullable value =
    value |> Option.map box |> Option.defaultValue (box DBNull.Value)

let private expectSql state constraintName action =
    task {
        let mutable rejection = None

        try
            do! action ()
        with :? PostgresException as error ->
            rejection <- Some(error.SqlState, Option.ofObj error.ConstraintName)

        Expect.equal
            rejection
            (Some(state, constraintName))
            "The intended structural protection rejected the mutation"
    }

let private controls (database: PostgresFixture.Database) =
    task {
        use! owner = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute owner appendFunction [||] token
        use! runtime = database.RuntimeSource.OpenConnectionAsync()

        let append aggregate aggregateId kind partId revision key erase erasedId privateContent =
            task {
                let! _ =
                    DatabaseSession.execute
                        runtime
                        "SELECT public.test_append_part($1::uuid,$2::text,$3::uuid,$4::text,$5::uuid,$6::bigint,$7::text,$8::text,$9::uuid,$10::boolean)"
                        [|
                            box database.Fixture.InstallationId
                            box aggregate
                            box aggregateId
                            box kind
                            box partId
                            box revision
                            nullable key
                            nullable erase
                            nullable erasedId
                            box privateContent
                        |]
                        token

                return ()
            }

        let clear kind identity =
            task {
                let! rows =
                    DatabaseSession.rows
                        runtime
                        "SELECT claimcore.clear_owner($1,$2)"
                        [| box kind; box identity |]
                        token

                return DatabaseSession.value<int64> 0 (List.exactlyOne rows)
            }

        do! append "FAMILY" (id 10) "THING" (id 11) 1L None None None true

        do!
            expectSql "CC004" (Some "part_revision_chain") (fun () ->
                append "FAMILY" (id 10) "THING" (id 11) 3L None None None false)

        do!
            expectSql "CC004" (Some "part_aggregate_owner") (fun () ->
                append "FAMILY" (id 20) "THING" (id 11) 2L None None None false)

        do! expectSql "CC002" None (fun () -> task { let! _ = clear "FAMILY" (id 10) in return () })

        let! untouched =
            DatabaseSession.rows
                runtime
                "SELECT count(*) FROM claimcore.ledger_compartments WHERE owner_id=$1 AND nonce IS NOT NULL AND payload IS NOT NULL"
                [| box (id 10) |]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 untouched.Head)
            1L
            "Rejected clearing preserves the private payload"

        do! append "FAMILY" (id 30) "THING" (id 30) 1L (Some "Mixed-Reference") None None false

        do!
            expectSql "23505" (Some "ledger_natural_key") (fun () ->
                append "FAMILY" (id 40) "THING" (id 40) 1L (Some "mixed-reference") None None false)

        use! familyErasure = runtime.BeginTransactionAsync()
        do! append "FAMILY" (id 10) "THING" (id 11) 2L None (Some "FAMILY") (Some(id 10)) false
        let! removed = clear "FAMILY" (id 10)
        Expect.equal removed 1L "An accepted erasing effect clears exactly its owner"
        do! familyErasure.CommitAsync()

        let! retained =
            DatabaseSession.rows
                runtime
                "SELECT octet_length(commitment),nonce IS NULL AND payload IS NULL FROM claimcore.ledger_compartments WHERE owner_id=$1"
                [| box (id 10) |]
                token

        Expect.equal
            (DatabaseSession.value<int> 0 retained.Head, DatabaseSession.value<bool> 1 retained.Head)
            (32, true)
            "Commitment survives clearing"

        do! append "PERSON" (id 50) "THING" (id 50) 1L None None None true
        use! profileErasure = runtime.BeginTransactionAsync()
        do! append "PERSON" (id 50) "THING" (id 50) 2L None (Some "PROFILE") (Some(id 50)) false
        let! removed = clear "PROFILE" (id 50)
        Expect.equal removed 1L "The earlier profile is cleared"
        do! profileErasure.CommitAsync()
        do! append "PERSON" (id 50) "THING" (id 50) 3L None None None true
        let! removed = clear "PROFILE" (id 50)
        Expect.equal removed 0L "An old erasure does not authorize clearing its replacement"

        let directClear source () =
            task {
                let! _ =
                    DatabaseSession.execute
                        source
                        "UPDATE claimcore.ledger_compartments SET nonce=NULL,payload=NULL WHERE owner_id=$1 AND payload IS NOT NULL"
                        [| box (id 50) |]
                        token

                return ()
            }

        do! expectSql "42501" None (directClear runtime)
        do! expectSql "CC003" None (directClear owner)

    }

[<Tests>]
let tests =
    testCaseAsync
        "ledger SQL enforces revision chains, aggregate ownership, erasure authority and case-folded natural keys"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                do! controls database |> Async.AwaitTask
            finally
                database.Fixture.Dispose()
        })
