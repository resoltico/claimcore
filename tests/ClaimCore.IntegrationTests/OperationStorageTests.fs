module ClaimCore.IntegrationTests.OperationStorageTests

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.Postgres
open ClaimCore.TestSupport

let private valid result =
    Result.defaultWith (fun error -> failtestf "Root storage fixture: %A" error) result

let private token = CancellationToken.None
let private sync (pending: System.Threading.Tasks.Task<'a>) = pending.GetAwaiter().GetResult()

let private operation () =
    Guid.CreateVersion7() |> OperationId.create |> Option.get

let private principal subject =
    PrincipalKey.human "https://issuer.example/" subject |> valid

let private profile name =
    ActorProfile.parse { DisplayName = name; Contact = None } |> valid

let private storage () =
    let database = PostgresFixture.create () |> sync

    try
        let installation = database.Fixture.InstallationId
        let owner = operation ()
        let actor = ActorId.ofOperationId owner
        let mutable sequence = 0L
        let mutable hash = EntryHash.zero

        let create identity target principal act saved nonce =
            let header =
                {
                    OperationHeader.InstallationId = installation
                    Sequence = Sequence.create (sequence + 1L) |> Option.get
                    PreviousHash = hash
                    OperationId = identity
                    ActorId = actor
                    RecordedAt = DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)
                    BusinessDate = None
                }

            let command =
                CommandSkeleton.Authority
                    {
                        TargetActorId = target
                        Principal = principal
                        Act = act
                    }

            RetainedOperation.create
                header
                command
                (OperationValues.Authority { Profile = saved })
                nonce
            |> valid
            |> _.Root

        let write root after =
            use connection = database.RuntimeSource.OpenConnection()
            use transaction = connection.BeginTransaction()
            OperationStorage.append connection root token |> sync |> ignore
            after connection
            transaction.Commit()
            sequence <- Sequence.value root.Header.Sequence
            hash <- root.Hash

        let query sql =
            use connection = database.OwnerSource.OpenConnection()
            DatabaseSession.rows connection sql [||] token |> sync

        let head () =
            query "SELECT sequence,entry_hash FROM claimcore.ledger_head" |> List.exactlyOne

        let expectFailure expectedCode (source: NpgsqlDataSource) sql parameters =
            let before = head ()
            use connection = source.OpenConnection()

            try
                DatabaseSession.execute connection sql parameters token |> sync |> ignore
                failtest "The forbidden root mutation was accepted"
            with :? PostgresException as error ->
                Expect.equal
                    error.SqlState
                    expectedCode
                    "Exact protected append or immutable-structure refusal"

            let after = head ()

            Expect.equal
                (DatabaseSession.value<int64> 0 after)
                (DatabaseSession.value<int64> 0 before)
                "Refused mutation does not advance the head"

            Expect.equal
                (DatabaseSession.value<byte array> 1 after)
                (DatabaseSession.value<byte array> 1 before)
                "Refused mutation does not change the accepted hash"

        let ownerRoot =
            create
                owner
                actor
                (principal "owner")
                (AuthorityAct.ProvisionOwner(0, Authority.initialOwnerRoles))
                (Some(profile "Owner"))
                (fun () -> RandomNumberGenerator.GetBytes 32)
        // Bootstrap executor is the actor created by this same operation.
        write ownerRoot ignore

        Expect.equal
            sequence
            1L
            "Six owner/profile/role effects advance the operation sequence once"

        Expect.equal
            (query "SELECT count(*) FROM claimcore.ledger_effects"
             |> List.head
             |> DatabaseSession.value<int64> 0)
            6L
            "All bootstrap effects exist together"

        let targetOperation = operation ()
        let target = ActorId.ofOperationId targetOperation
        let targetPrincipal = principal "target"

        let grants =
            [
                {
                    Role = Role.Auditor
                    Scope = Scope.Installation
                }
            ]

        let admitted =
            create
                targetOperation
                target
                targetPrincipal
                (AuthorityAct.Admit { Profile = 0; Grants = grants })
                (Some(profile "Private target"))
                (fun () -> RandomNumberGenerator.GetBytes 32)

        write admitted ignore

        let read identity =
            DatabaseSession.read database.RuntimeSource token (fun connection ->
                OperationStorage.find connection installation identity token)
            |> sync
            |> Option.get

        match (read targetOperation).Values with
        | RetainedOperationValues.Authority(RetainedProfile.Available actual) ->
            Expect.equal
                (ActorProfile.authored actual).DisplayName
                "Private target"
                "Actual PostgreSQL root uses the shared typed private decoder"
        | _ -> failtest "Available profile required"

        let erased =
            create (operation ()) target targetPrincipal AuthorityAct.EraseProfile None (fun () ->
                failtest "Erasure requested a private nonce")

        write erased (fun connection ->
            DatabaseSession.execute
                connection
                "SELECT claimcore.erase_profile_payload($1)"
                [| box (ActorId.value target) |]
                token
            |> sync
            |> ignore)

        let cleared = read targetOperation
        Expect.equal cleared.Root.Hash admitted.Hash "Erasure preserves the accepted operation root"

        Expect.equal
            cleared.Values
            (RetainedOperationValues.Authority RetainedProfile.Unavailable)
            "Only the private profile becomes unavailable"

        Expect.equal
            cleared.Root.Structure.Effects
            admitted.Structure.Effects
            "Registration and grants survive selective erasure"

        let reused = RandomNumberGenerator.GetBytes 32

        let later =
            create
                (operation ())
                target
                targetPrincipal
                (AuthorityAct.SetProfile 0)
                (Some(profile "Later profile"))
                (fun () -> reused)

        write later ignore
        use connection = database.RuntimeSource.OpenConnection()

        DatabaseSession.execute
            connection
            "SELECT claimcore.erase_profile_payload($1)"
            [| box (ActorId.value target) |]
            token
        |> sync
        |> ignore

        match (read later.Header.OperationId).Values with
        | RetainedOperationValues.Authority(RetainedProfile.Available actual) ->
            Expect.equal
                (ActorProfile.authored actual).DisplayName
                "Later profile"
                "An older erase frontier cannot clear a later re-profile"
        | _ -> failtest "Later profile was incorrectly erased"

        let duplicate =
            create
                (operation ())
                target
                targetPrincipal
                (AuthorityAct.SetProfile 0)
                (Some(profile "Duplicate nonce"))
                (fun () -> reused)

        let appendSql =
            "SELECT claimcore.append_operation($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::jsonb,$11::jsonb,$12::jsonb)"

        expectFailure
            "23505"
            database.RuntimeSource
            appendSql
            (OperationStorage.parameters duplicate)

        let incomplete = OperationStorage.parameters duplicate
        incomplete[10] <- box "[]"
        expectFailure "CC004" database.RuntimeSource appendSql incomplete
        let missing = OperationStorage.parameters duplicate
        missing[11] <- box "[]"
        expectFailure "CC004" database.RuntimeSource appendSql missing
        let foreign = OperationStorage.parameters duplicate
        foreign[0] <- box (Guid.NewGuid())
        expectFailure "CC004" database.RuntimeSource appendSql foreign

        expectFailure
            "42501"
            database.RuntimeSource
            "INSERT INTO claimcore.ledger_effects(operation_sequence,effect_index,descriptor) SELECT operation_sequence,49,descriptor FROM claimcore.ledger_effects LIMIT 1"
            [||]

        expectFailure
            "CC003"
            database.OwnerSource
            "INSERT INTO claimcore.ledger_effects(operation_sequence,effect_index,descriptor) SELECT operation_sequence,49,descriptor FROM claimcore.ledger_effects LIMIT 1"
            [||]

        expectFailure
            "CC003"
            database.OwnerSource
            "UPDATE claimcore.ledger SET entry_hash=decode(repeat('01',32),'hex') WHERE sequence=1"
            [||]

        expectFailure
            "CC003"
            database.OwnerSource
            "UPDATE claimcore.ledger_effects SET descriptor=descriptor WHERE operation_sequence=1"
            [||]

        expectFailure "CC003" database.OwnerSource "DELETE FROM claimcore.ledger_compartments" [||]
        expectFailure "CC003" database.OwnerSource "TRUNCATE claimcore.ledger_effects CASCADE" [||]

        Expect.equal
            (DatabaseSession.value<int64> 0 (head ()))
            4L
            "Four accepted operations remain after all negative controls"

        let mutable current: CaseSnapshot option = None

        let case act context =
            let identity = operation ()

            let hdr =
                { ownerRoot.Header with
                    Sequence = Sequence.create (sequence + 1L) |> Option.get
                    PreviousHash = hash
                    OperationId = identity
                    BusinessDate = Some(DateOnly(2026, 11, 9))
                }

            let input =
                Case.parse
                    {
                        OperationId = OperationId.value identity
                        CaseReference = "CASE-MARKERS"
                        ExpectedRevision =
                            current
                            |> Option.map (_.Revision >> Revision.value)
                            |> Option.defaultValue 0L
                        Act = act
                    }
                |> valid

            let decided =
                Case.decideSnapshot (DateOnly(2026, 11, 9)) (context current) input current
                |> valid

            let selected =
                match input.Act with
                | CaseAct.RemovePayment id -> Some id
                | _ -> None

            let command =
                CommandSkeleton.Case
                    {
                        CaseId = decided.After.CaseId
                        Reference = decided.After.Reference
                        ExpectedRevision = input.ExpectedRevision
                        Act = CaseAct.kind input.Act
                        SelectedPayment = selected
                    }

            let retained =
                RetainedOperation.create
                    hdr
                    command
                    (OperationValues.Case { Command = input; Decision = decided })
                    (fun () -> RandomNumberGenerator.GetBytes 32)
                |> valid

            let projection =
                match current, decided.After.State with
                | _, CaseState.Erased ->
                    CaseProjection.Erase(
                        decided.After.CaseId,
                        decided.After.Revision,
                        hdr.RecordedAt
                    )
                | None, _ ->
                    CaseProjection.Insert(
                        decided.After.CaseId,
                        decided.After,
                        CaseIndex.create hdr.RecordedAt decided.After
                    )
                | Some _, _ ->
                    CaseProjection.Update(
                        decided.After.CaseId,
                        decided.After,
                        CaseIndex.create hdr.RecordedAt decided.After,
                        decided.Payment
                    )

            write retained.Root (fun connection ->
                Persistence.case connection projection token |> sync)

            current <- Some decided.After
            retained, decided

        let noContext _ = CasePaymentContext.NoneRequired
        case (CaseAct.Open Inputs.registration) noContext |> ignore
        case (CaseAct.Decide Inputs.decision) noContext |> ignore

        let recording (before: CaseSnapshot option) =
            let before = before |> Option.get
            CasePaymentContext.Recording(before.CaseId, before.Revision, false)

        let first, firstDecision =
            case
                (CaseAct.RecordPayment(
                    Inputs.paymentWith [ BusinessField.PaidAmount, "10" ] Inputs.payment
                ))
                recording

        case
            (CaseAct.RecordPayment(
                Inputs.paymentWith [ BusinessField.PaidAmount, "5" ] Inputs.payment
            ))
            recording
        |> ignore

        let selected =
            match firstDecision.Payment with
            | Some(PaymentDifference.Recorded entry) -> entry
            | _ -> failtest "Recorded payment required"

        let removal (before: CaseSnapshot option) =
            let before = before |> Option.get
            let date = Inputs.date "2026-08-20"

            CasePaymentContext.Removing(
                before.CaseId,
                before.Revision,
                Some selected,
                PaymentDateBounds.create date date
            )

        let removed, _ =
            case (CaseAct.RemovePayment(OperationId.value first.Root.Header.OperationId)) removal

        let beforeRows =
            query
                "SELECT payment_id,removed FROM claimcore.case_payments ORDER BY accepting_sequence"

        Expect.equal
            (beforeRows |> List.map (DatabaseSession.value<bool> 1))
            [ true; false ]
            "Removal marker is independently persisted before erasure"

        case (CaseAct.Bare BareAct.Erase) noContext |> ignore

        let markers =
            query
                "SELECT payment_id,removed,payment_date IS NULL AND paid_amount IS NULL FROM claimcore.case_payments ORDER BY accepting_sequence"

        Expect.equal
            (markers |> List.map (DatabaseSession.value<bool> 1))
            [ true; false ]
            "Case erasure preserves removal history"

        Expect.equal
            (markers |> List.map (DatabaseSession.value<bool> 2))
            [ true; true ]
            "Case erasure clears all private payment facts"

        match (read removed.Root.Header.OperationId).Values with
        | RetainedOperationValues.Case None -> ()
        | _ -> failtest "Case-owned retained contents were not cleared"

        match (read removed.Root.Header.OperationId).Root.Structure.Command with
        | CommandSkeleton.Case scope ->
            Expect.equal
                scope.SelectedPayment
                (Some first.Root.Header.OperationId)
                "Erased removal remains reconstructible from retained structure"
        | _ -> failtest "Case command required"

        Expect.equal
            (query
                "SELECT count(*) FROM claimcore.ledger_compartments WHERE owner_kind='CASE' AND (payload IS NOT NULL OR nonce IS NOT NULL)"
             |> List.head
             |> DatabaseSession.value<int64> 0)
            0L
            "Case owner erasure clears bytes and nonces together"
    finally
        database.Fixture.Dispose()

[<Tests>]
let tests =
    testCase
        "protected PostgreSQL roots append atomically and preserve selectively erased authority"
        storage
