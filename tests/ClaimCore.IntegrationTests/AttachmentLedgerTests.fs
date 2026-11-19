module ClaimCore.IntegrationTests.AttachmentLedgerTests

open System
open System.Text
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

let private token = CancellationToken.None

let private id number =
    Guid.Parse(sprintf "00000000-0000-4000-8000-%012d" number)

let private fixtureSha =
    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

let private fixtureBytes = Encoding.UTF8.GetBytes "abc"

let private nullable value =
    value |> Option.map box |> Option.defaultValue (box DBNull.Value)

/// Hand-authored structural envelopes intentionally omit registered task semantics. These
/// statements exercise the storage guards independently of the native codec and admission.
let private append
    connection
    installation
    caseId
    operation
    part
    kind
    revision
    (retained: string option)
    (descriptor: string option)
    binary
    erase
    =
    task {
        let! _ =
            DatabaseSession.execute
                connection
                """
                WITH current_head AS (
                    SELECT sequence+1 AS next, entry_hash FROM claimcore.ledger_head WHERE singleton
                ), effect AS (
                    SELECT jsonb_strip_nulls(jsonb_build_object(
                        'aggregate',jsonb_build_object('kind','FAMILY','id',$2::uuid),
                        'part',jsonb_build_object('kind',$5::text,'id',$4::uuid),
                        'revision',$6::bigint,'retained',$7::jsonb,
                        'compartment',CASE WHEN $8::jsonb IS NOT NULL THEN 0 END,
                        'erases',CASE WHEN $10::boolean THEN jsonb_build_object('kind','FAMILY','id',$2::uuid) END)) AS value
                )
                SELECT claimcore.append_operation(
                    $1::uuid,next,entry_hash,decode(lpad(to_hex(next),64,'0'),'hex'),$11::smallint,
                    $3::uuid,'00000000-0000-4000-8000-000000000001',clock_timestamp(),DATE '2026-11-16',
                    'FAMILY','sample.change','{"binding":{},"retained":{}}'::jsonb,jsonb_build_array(effect.value),
                    CASE WHEN $8::jsonb IS NULL THEN '[]'::jsonb ELSE jsonb_build_array(jsonb_build_object(
                        'ordinal',0,'owner',jsonb_build_object('kind','FAMILY','id',$2::uuid),
                        'commitment',repeat('11',32),'nonce',lpad(to_hex(next),64,'0'),
                        'payload',encode(convert_to(jsonb_build_object('inputs','{}'::jsonb,
                            'values',jsonb_build_object('0',$8::jsonb))::text,'UTF8'),'hex'))) END,
                    $9::bytea)
                FROM current_head CROSS JOIN effect
                """
                [|
                    box installation
                    box caseId
                    box operation
                    box part
                    box kind
                    box revision
                    nullable retained
                    nullable descriptor
                    nullable binary
                    box erase
                    box (int16 InstallationFormat.Current)
                |]
                token

        return ()
    }

let private rejection state action =
    task {
        try
            do! action ()
            failtestf "Expected SQLSTATE %s" state
        with :? PostgresException as fault ->
            Expect.equal fault.SqlState state "The intended storage guard rejected the transaction"
    }

let private execute connection sql =
    task {
        let! _ = DatabaseSession.execute connection sql [||] token
        return ()
    }

let private exercise (database: PostgresFixture.Database) =
    task {
        use! runtime = database.RuntimeSource.OpenConnectionAsync()
        use! owner = database.OwnerSource.OpenConnectionAsync()
        let firstCase, otherCase = id 10, id 20
        let first, second, other = id 11, id 12, id 21
        let installation = database.Fixture.InstallationId

        let descriptor blob length digest =
            Some(
                sprintf
                    """{"blob":"%O","name":"synthetic.bin","length":%d,"digest":"%s"}"""
                    blob
                    length
                    digest
            )

        let add connection caseId attachment blob introduces sha data =
            append
                connection
                installation
                caseId
                attachment
                attachment
                "ATTACHMENT"
                1L
                (Some(
                    if introduces then
                        """{"introducesBlob":true}"""
                    else
                        """{"introducesBlob":false}"""
                ))
                (descriptor blob 3 sha)
                data
                false

        let erase connection =
            append connection installation firstCase (id 90) firstCase "CASE" 1L None None None true

        let counts () =
            task {
                let! rows =
                    DatabaseSession.rows
                        runtime
                        """
                        SELECT sequence,
                            (SELECT count(*) FROM claimcore.attachment_blobs WHERE case_id=$1 AND payload IS NOT NULL),
                            (SELECT count(*) FROM claimcore.attachment_references WHERE case_id=$1 AND blob_id IS NOT NULL),
                            (SELECT count(*) FROM claimcore.case_attachments WHERE case_id=$1)
                        FROM claimcore.ledger_head
                        """
                        [| box firstCase |]
                        token

                let row = List.exactlyOne rows

                return
                    DatabaseSession.value<int64> 0 row,
                    DatabaseSession.value<int64> 1 row,
                    DatabaseSession.value<int64> 2 row,
                    DatabaseSession.value<int64> 3 row
            }

        // A writer reaching the lower structural seam still cannot commit an accepting
        // effect without its payload and link, even when the head and compartment exist.
        do!
            task {
                use! transaction = owner.BeginTransactionAsync(token)

                let unbound = id 9

                let! _ =
                    DatabaseSession.execute
                        owner
                        """
                        INSERT INTO claimcore.ledger
                          (sequence,previous_hash,entry_hash,installation_format,operation_id,actor_id,recorded_at,business_date,aggregate_kind,task,structure)
                        SELECT 1,entry_hash,sha256(decode('61','hex')),$1,$2,$2,clock_timestamp(),DATE '2026-11-16',
                          'FAMILY','sample.change','{"binding":{},"retained":{}}'
                        FROM claimcore.ledger_head
                        """
                        [| box (int16 InstallationFormat.Current); box unbound |]
                        token

                let! _ =
                    DatabaseSession.execute
                        owner
                        """
                        INSERT INTO claimcore.ledger_effects
                          (operation_sequence,effect_index,aggregate_kind,aggregate_id,part_kind,part_id,revision,retained,compartment)
                        VALUES(1,0,'FAMILY',$1,'ATTACHMENT',$2,1,'{"introducesBlob":true}',0)
                        """
                        [| box firstCase; box unbound |]
                        token

                let! _ =
                    DatabaseSession.execute
                        owner
                        """
                        INSERT INTO claimcore.ledger_compartments
                          (operation_sequence,ordinal,owner_kind,owner_id,commitment,nonce,payload)
                        VALUES(1,0,'FAMILY',$1,sha256(decode('62','hex')),sha256(decode('63','hex')),decode('7b7d','hex'))
                        """
                        [| box firstCase |]
                        token

                do!
                    execute
                        owner
                        "UPDATE claimcore.ledger_head SET sequence=1,entry_hash=sha256(decode('61','hex'))"

                try
                    do! transaction.CommitAsync(token)
                    failtest "A bare attachment effect bypassed the atomic storage closure"
                with :? PostgresException as fault ->
                    Expect.equal
                        fault.SqlState
                        "CC004"
                        "The independent accepting-effect guard rejects the root"

                    Expect.equal
                        fault.MessageText
                        "attachment evidence must bind to its accepting effect and family"
                        "Rejection comes from the attachment closure"
            }

        do! rejection "CC004" (fun () -> add runtime firstCase first first true fixtureSha None)

        do!
            rejection "CC004" (fun () ->
                add runtime firstCase first first true fixtureSha (Some [| 0uy; 0uy; 0uy |]))

        do! add runtime firstCase first first true fixtureSha (Some fixtureBytes)
        do! add runtime otherCase other other true fixtureSha (Some fixtureBytes)

        do! rejection "23503" (fun () -> add runtime firstCase second other false fixtureSha None)

        do!
            rejection "42501" (fun () ->
                execute
                    runtime
                    "UPDATE claimcore.attachment_blobs SET payload=NULL,length=NULL,digest=NULL")

        do!
            rejection "CC003" (fun () ->
                execute
                    owner
                    "UPDATE claimcore.attachment_blobs SET payload=NULL,length=NULL,digest=NULL")

        do!
            rejection "CC003" (fun () ->
                execute
                    owner
                    """
                    INSERT INTO claimcore.attachment_references(case_id,attachment_id,operation_sequence,effect_index,blob_id)
                    SELECT case_id,gen_random_uuid(),operation_sequence,effect_index,blob_id FROM claimcore.attachment_references LIMIT 1
                    """)

        do!
            append
                runtime
                installation
                firstCase
                (id 13)
                first
                "ATTACHMENT"
                2L
                (Some """{"withdrawn":true}""")
                None
                None
                false

        let! withdrawn = counts ()
        Expect.equal withdrawn (3L, 1L, 1L, 0L) "Withdrawal removes only current list membership"

        use! interrupted = runtime.BeginTransactionAsync()
        do! add runtime firstCase second first false fixtureSha None
        let! staged = counts ()

        Expect.equal
            staged
            (4L, 1L, 2L, 1L)
            "Reattachment takes one new accepting link and reuses the blob"

        do! interrupted.RollbackAsync()

        let! rolledBack = counts ()
        Expect.equal rolledBack withdrawn "Head, link, and projection roll back together"
        do! add runtime firstCase second first false fixtureSha None

        // The missing case projection cannot hide an incomplete attachment clearing.
        do!
            rejection "CC003" (fun () ->
                task {
                    use! transaction = owner.BeginTransactionAsync()
                    do! erase owner

                    let! _ =
                        DatabaseSession.execute
                            owner
                            "UPDATE claimcore.ledger_compartments SET nonce=NULL,payload=NULL WHERE owner_kind='FAMILY' AND owner_id=$1"
                            [| box firstCase |]
                            token

                    do! transaction.CommitAsync()
                })

        use! erased = runtime.BeginTransactionAsync()
        do! erase runtime

        let! _ =
            DatabaseSession.execute
                runtime
                "SELECT claimcore.clear_owner('FAMILY',$1)"
                [| box firstCase |]
                token

        do! erased.CommitAsync()

        let! cleared = counts ()

        Expect.equal
            cleared
            (5L, 0L, 0L, 0L)
            "Erasure clears private blob and link columns and current membership"

        let! evidence =
            DatabaseSession.rows
                runtime
                """
                SELECT (SELECT count(*) FROM claimcore.attachment_blobs WHERE case_id=$1 AND length IS NULL AND digest IS NULL AND payload IS NULL),
                    (SELECT count(*) FROM claimcore.attachment_references WHERE case_id=$1 AND blob_id IS NULL),
                    (SELECT count(*) FROM claimcore.attachment_blobs WHERE case_id=$2 AND payload=decode('616263','hex'))
                """
                [| box firstCase; box otherCase |]
                token

        let row = List.exactlyOne evidence
        Expect.equal (DatabaseSession.value<int64> 0 row) 1L "The first-case blob origin remains"

        Expect.equal
            (DatabaseSession.value<int64> 1 row)
            2L
            "Both added attachment identities remain"

        Expect.equal (DatabaseSession.value<int64> 2 row) 1L "Distinct case's exact bytes remain"

        do!
            rejection "CC004" (fun () ->
                add runtime firstCase (id 14) (id 14) true fixtureSha (Some fixtureBytes))
    }

[<Tests>]
let tests =
    testCaseAsync
        "attachment ledger binds binary acceptance, withdrawal and erasure atomically"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                do! exercise database |> Async.AwaitTask
            finally
                database.Fixture.Dispose()
        })
