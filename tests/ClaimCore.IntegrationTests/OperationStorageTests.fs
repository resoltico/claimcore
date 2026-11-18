module ClaimCore.IntegrationTests.OperationStorageTests

open System
open System.Text.Json.Nodes
open System.Threading
open System.Security.Cryptography
open Npgsql
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Verification
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private valid =
    function
    | Ok value -> value
    | Error error -> failtestf "Root storage fixture: %A" error

let private token = CancellationToken.None
let private sync (pending: System.Threading.Tasks.Task<'value>) = pending.GetAwaiter().GetResult()

let private operation () =
    Guid.CreateVersion7() |> OperationId.create |> Option.get

let private principal subject =
    PrincipalKey.human "https://issuer.example/" subject |> valid

let private profile name =
    ActorProfile.parse { DisplayName = name; Contact = None } |> valid

let private rootInputs
    (principal: PrincipalKey option)
    (profile: ActorProfile option)
    (roles: Role list)
    =
    let values =
        [
            yield!
                principal
                |> Option.map (fun principal ->
                    let kind, issuer, subject = PrincipalKey.parts principal in

                    [
                        TaskInputId.Target InputTarget.PrincipalKind,
                        InputValue.Text(Vocabulary.token PrincipalKind.vocabulary kind)
                        TaskInputId.Target InputTarget.PrincipalIssuer, InputValue.Text issuer
                        TaskInputId.Target InputTarget.PrincipalSubject, InputValue.Text subject
                    ])
                |> Option.defaultValue []
            yield!
                profile
                |> Option.map (fun profile ->
                    [
                        TaskInputId.Target InputTarget.ProfileDisplayName,
                        InputValue.Text(PartyName.format profile.DisplayName)
                    ])
                |> Option.defaultValue []
        ]
        |> Map.ofList

    let collections =
        if roles.IsEmpty then
            Map.empty
        else
            Map.ofList
                [
                    TaskCollectionId.PeopleGrants,
                    roles
                    |> List.map (fun role ->
                        Map.ofList
                            [
                                TaskInputId.Target InputTarget.Role,
                                InputValue.Text(Role.token role)
                                TaskInputId.Scope, InputValue.Text "INSTALLATION"
                            ])
                ]

    InputMap.create values collections |> Option.get

let private storage () =
    let database = PostgresFixture.create () |> sync

    try
        let owner = operation ()
        let executor = ActorId.ofOperationId owner
        let mutable sequence = Sequence.zero
        let mutable hash = EntryHash.zero

        let create identity target revision task binding inputs (value: PersonValue) erases nonce =
            let now = DateTimeOffset.UtcNow
            let instant = DateTimeOffset(now.Ticks / 10L * 10L, TimeSpan.Zero)
            let next = Sequence.successor sequence

            let authority =
                if task = "people.provisionOwner" then
                    Authority.Operator
                else
                    Authority.Actor executor

            let stamp =
                {
                    SubjectStamp.Position = EffectPosition.create next 0 |> Option.get
                    OperationId = identity
                    Authority = authority
                    RecordedAt = instant
                }

            let part =
                {
                    Aggregate = AggregateReference.Person target
                    PartId = ActorId.value target
                    Revision = revision
                    NaturalKey = value.Principal |> Option.map PrincipalKey.naturalKey
                    Value = PartValue.Person value
                    Erases = erases
                    Stamp = stamp
                }

            let header =
                {
                    OperationHeader.InstallationId = database.Fixture.InstallationId
                    Sequence = next
                    PreviousHash = hash
                    OperationId = identity
                    Authority = authority
                    RecordedAt = instant
                    BusinessDate = None
                }

            RetainedOperation.create header (TaskId.ofLiteral task) binding inputs [ part ] nonce
            |> valid
            |> _.Root

        let write root after =
            use connection = database.RuntimeSource.OpenConnection()
            use transaction = connection.BeginTransaction()
            OperationStorage.append connection root token |> sync |> ignore
            after connection
            transaction.Commit()
            sequence <- root.Header.Sequence
            hash <- root.Hash

        let query sql =
            use connection = database.OwnerSource.OpenConnection()
            DatabaseSession.rows connection sql [||] token |> sync

        let head () =
            query "SELECT sequence,entry_hash FROM claimcore.ledger_head" |> List.exactlyOne

        let expectFailureMatching
            expectedCode
            expectedConstraint
            (source: NpgsqlDataSource)
            sql
            parameters
            =
            let before = head ()
            use connection = source.OpenConnection()

            try
                DatabaseSession.execute connection sql parameters token |> sync |> ignore
                failtest "A forbidden root mutation was accepted"
            with :? PostgresException as error ->
                Expect.equal
                    error.SqlState
                    expectedCode
                    "Exact protected append or immutable-structure refusal"

                expectedConstraint
                |> Option.iter (fun expected ->
                    Expect.equal
                        error.ConstraintName
                        expected
                        "The intended constraint rejects the negative control")

            let after = head ()

            Expect.equal
                (DatabaseSession.value<int64> 0 after)
                (DatabaseSession.value<int64> 0 before)
                "Refusal preserves head position"

            Expect.equal
                (DatabaseSession.value<byte array> 1 after)
                (DatabaseSession.value<byte array> 1 before)
                "Refusal preserves head hash"

        let expectFailure code source sql parameters =
            expectFailureMatching code None source sql parameters

        let ownerPrincipal = principal "owner"
        let ownerProfile = profile "Owner"

        let ownerRoot =
            create
                owner
                executor
                Revision.first
                "people.provisionOwner"
                (Binding.PeopleNew Revision.zero)
                (rootInputs (Some ownerPrincipal) (Some ownerProfile) [])
                {
                    Principal = Some ownerPrincipal
                    Enabled = true
                    Profile = ProfileValue.Present(Some ownerProfile)
                    Grants =
                        [
                            {
                                Grant =
                                    {
                                        Role = Role.Owner
                                        Scope = Scope.Installation
                                    }
                                Active = true
                            }
                        ]
                }
                None
                (fun () -> RandomNumberGenerator.GetBytes 32)

        // Header date encoding is structural storage, independent of task admission. Roll each
        // probe back so the genuine append below still proves an unchanged genesis/head.
        for date in [ DateOnly.MinValue; DateOnly.MaxValue ] do
            let reference = CaseFixtures.reference "DATE-HEADER"

            let command =
                Submissions.createRaw
                    "case.open"
                    (OperationId.value owner)
                    (OperationBinding.CaseNew 0L)
                    (Submissions.fields (RegistrationInput.values registration)
                     |> Map.add
                         (Submissions.field BusinessField.CaseReference)
                         (SubmissionValue.Text "DATE-HEADER"))
                |> Submissions.command

            let change =
                {
                    PartChange.Aggregate = AggregateReference.Family(CaseId.ofOperationId owner)
                    PartId = OperationId.value owner
                    Revision = Revision.first
                    NaturalKey = Some "DATE-HEADER"
                    Erases = None
                    Stamp =
                        { ownerRoot.Structure.Effects.Head.Change.Stamp with
                            Authority = Authority.Actor executor
                        }
                    Value =
                        PartValue.Case
                            {
                                Reference = Some reference
                                Lifecycle = Lifecycle.Active
                                Handling = Handling.Open
                                Disposition = None
                                ReopenReason = None
                                Content =
                                    Some
                                        {
                                            Registration =
                                                DomainInputs.admittedRegistration registration
                                            Finance = FinancialPosition.undecided
                                        }
                            }
                }

            let root =
                RetainedOperation.create
                    { ownerRoot.Header with
                        Authority = Authority.Actor executor
                        BusinessDate = Some date
                    }
                    (TaskId.ofLiteral "case.open")
                    command.Binding
                    command.Inputs
                    [ change ]
                    (fun () -> RandomNumberGenerator.GetBytes 32)
                |> valid
                |> _.Root

            if date = DateOnly.MinValue then
                let parameters = OperationStorage.parameters root
                parameters[6] <- DBNull.Value

                expectFailureMatching
                    "23514"
                    (Some "ledger_family_actor")
                    database.RuntimeSource
                    "SELECT claimcore.append_operation($1,$2,$3,$4,$5,$6,$7::uuid,$8,$9::text::date,$10,$11,$12::jsonb,$13::jsonb,$14::jsonb)"
                    parameters

            use connection = database.RuntimeSource.OpenConnection()
            use transaction = connection.BeginTransaction()
            OperationStorage.append connection root token |> sync |> ignore

            let loaded =
                OperationStorage.find connection database.Fixture.InstallationId owner token
                |> sync
                |> Option.get

            Expect.equal
                loaded.Root.Header.BusinessDate
                (Some date)
                "Finite Gregorian header date roundtrip"

            let finite =
                DatabaseSession.rows
                    connection
                    "SELECT isfinite(business_date) FROM claimcore.ledger WHERE sequence=1"
                    [||]
                    token
                |> sync
                |> List.exactlyOne

            Expect.isTrue
                (DatabaseSession.value<bool> 0 finite)
                "No finite endpoint becomes a PostgreSQL infinity"

            transaction.Rollback()

        write ownerRoot ignore
        Expect.equal (Sequence.value sequence) 1L "Bootstrap advances the root once"

        Expect.equal
            (query "SELECT count(*) FROM claimcore.ledger_effects"
             |> List.head
             |> DatabaseSession.value<int64> 0)
            1L
            "Principal, profile and grants share one actual person part"

        let identity = operation ()
        let target = ActorId.ofOperationId identity
        let targetPrincipal = principal "target"
        let targetProfile = profile "Private target"

        let admitted =
            create
                identity
                target
                Revision.first
                "people.admit"
                (Binding.PeopleNew Revision.zero)
                (rootInputs (Some targetPrincipal) (Some targetProfile) [ Role.Auditor ])
                {
                    Principal = Some targetPrincipal
                    Enabled = true
                    Profile = ProfileValue.Present(Some targetProfile)
                    Grants =
                        [
                            {
                                Grant =
                                    {
                                        Role = Role.Auditor
                                        Scope = Scope.Installation
                                    }
                                Active = true
                            }
                        ]
                }
                None
                (fun () -> RandomNumberGenerator.GetBytes 32)

        write admitted ignore

        let read identity =
            DatabaseSession.read database.RuntimeSource token (fun connection ->
                OperationStorage.find connection database.Fixture.InstallationId identity token)
            |> sync
            |> Option.get

        let available name (record: RetainedOperation) =
            match record.Changes.Head.Value with
            | PartValue.Person {
                                   Profile = ProfileValue.Present(Some actual)
                               } ->
                Expect.equal
                    (PartyName.format actual.DisplayName)
                    name
                    "Shared typed private decoder"
            | _ -> failtest "Available accepted profile required"

        available "Private target" (read identity)

        let erased =
            create
                (operation ())
                target
                (Revision.create 2L |> Option.get)
                "people.eraseProfile"
                (Binding.People(target, Revision.first))
                InputMap.empty
                {
                    Principal = None
                    Enabled = true
                    Profile = ProfileValue.Absent
                    Grants = []
                }
                (Some(CompartmentOwner.Profile target))
                (fun () -> failtest "Erasure must not request a private nonce")

        let clear connection =
            DatabaseSession.execute
                connection
                "SELECT claimcore.clear_owner('PROFILE',$1)"
                [| box (ActorId.value target) |]
                token
            |> sync
            |> ignore

        write erased clear
        let cleared = read identity
        Expect.equal cleared.Root.Hash admitted.Hash "Erasure preserves the committed root hash"
        Expect.isNone cleared.Inputs "Erased authored profile values remain unavailable"

        match cleared.Changes.Head.Value with
        | PartValue.Person value ->
            Expect.equal
                value.Profile
                (ProfileValue.Present None)
                "Presence survives missing private bytes"

            Expect.equal value.Principal (Some targetPrincipal) "Retained principal survives"

            Expect.equal
                (value.Grants |> List.map _.Grant.Role)
                [ Role.Auditor ]
                "Retained grant assignment survives"
        | _ -> failtest "Person part required"

        let reused = RandomNumberGenerator.GetBytes 32

        let replacement revision name nonce =
            create
                (operation ())
                target
                (Revision.create revision |> Option.get)
                "people.setProfile"
                (Binding.People(target, Revision.create (revision - 1L) |> Option.get))
                (rootInputs None (Some(profile name)) [])
                {
                    Principal = None
                    Enabled = true
                    Profile = ProfileValue.Present(Some(profile name))
                    Grants = []
                }
                None
                (fun () -> nonce)

        let later = replacement 3L "Later profile" reused
        write later ignore
        use clearing = database.RuntimeSource.OpenConnection()
        clear clearing
        available "Later profile" (read later.Header.OperationId)
        let duplicate = replacement 4L "Duplicate nonce" reused
        let control = replacement 4L "Fresh controls" (RandomNumberGenerator.GetBytes 32)

        let appendSql =
            "SELECT claimcore.append_operation($1,$2,$3,$4,$5,$6,$7,$8,$9::text::date,$10,$11,$12::jsonb,$13::jsonb,$14::jsonb)"

        let parameters () = OperationStorage.parameters control

        expectFailure
            "23505"
            database.RuntimeSource
            appendSql
            (OperationStorage.parameters duplicate)

        let changed index value =
            let values = parameters () in
            values[index] <- value
            values

        expectFailure
            "CC001"
            database.RuntimeSource
            appendSql
            (changed 1 (box (Sequence.value sequence + 2L)))

        expectFailure
            "CC001"
            database.RuntimeSource
            appendSql
            (changed 2 (box (Array.zeroCreate<byte> 32)))

        expectFailure "CC004" database.RuntimeSource appendSql (changed 4 (box 1s))

        expectFailure
            "23505"
            database.RuntimeSource
            appendSql
            (changed 5 (box (OperationId.value owner)))

        let malformed mutate =
            let values = parameters ()
            let rows = JsonNode.Parse(unbox<string> values[13]) |> nonNull
            mutate (rows[0] |> nonNull)
            values[13] <- box (rows.ToJsonString())
            expectFailure "CC004" database.RuntimeSource appendSql values

        malformed (fun row -> row["ordinal"] <- JsonValue.Create 1)

        malformed (fun row ->
            (row["owner"] |> nonNull)["id"] <- JsonValue.Create(Guid.NewGuid().ToString("D")))

        malformed (fun row -> row["nonce"] <- null)
        malformed (fun row -> row["payload"] <- null)
        malformed (fun row -> row["payload"] <- JsonValue.Create "")
        expectFailure "CC004" database.RuntimeSource appendSql (changed 12 (box "[]"))
        expectFailure "23503" database.RuntimeSource appendSql (changed 13 (box "[]"))
        expectFailure "CC004" database.RuntimeSource appendSql (changed 0 (box (Guid.NewGuid())))

        let lateChild =
            "INSERT INTO claimcore.ledger_effects(operation_sequence,effect_index,aggregate_kind,aggregate_id,part_kind,part_id,revision) SELECT operation_sequence,49,aggregate_kind,aggregate_id,part_kind,gen_random_uuid(),1 FROM claimcore.ledger_effects LIMIT 1"

        expectFailure "42501" database.RuntimeSource lateChild [||]
        expectFailure "CC003" database.OwnerSource lateChild [||]

        expectFailure
            "CC003"
            database.OwnerSource
            "UPDATE claimcore.ledger SET entry_hash=decode(repeat('01',32),'hex') WHERE sequence=1"
            [||]

        expectFailure
            "CC003"
            database.OwnerSource
            "UPDATE claimcore.ledger_effects SET retained=retained WHERE operation_sequence=1"
            [||]

        expectFailure "CC003" database.OwnerSource "DELETE FROM claimcore.ledger_compartments" [||]
        expectFailure "CC003" database.OwnerSource "TRUNCATE claimcore.ledger_effects CASCADE" [||]

        Expect.equal
            (DatabaseSession.value<int64> 0 (head ()))
            4L
            "All negative controls leave four committed operations"

        write control ignore
        available "Fresh controls" (read control.Header.OperationId)

        Expect.equal
            (Sequence.value sequence)
            5L
            "The unmutated control appends successfully after all rejections"
    finally
        database.Fixture.Dispose()

[<Tests>]
let tests =
    testCase
        "generic PostgreSQL roots preserve private erasure frontiers and protected append boundaries"
        storage

[<Tests>]
let familyMarkers =
    testCaseAsync
        "generic family erasure retains independent payment removal markers and selected binding"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                let fixture = database.Fixture
                let! service = caseWorker fixture |> Async.AwaitTask

                let! permission =
                    grant
                        service
                        (id fixture 920001)
                        fixture.Owner
                        {
                            Role = Role.DataSteward
                            Scope = GrantTarget.Installation
                        }
                        true
                    |> Async.AwaitTask

                acceptedPeople permission

                let! opened =
                    Service.executeSubmission
                        service
                        (request
                            fixture
                            10
                            "CASE-MARKERS"
                            0L
                            "case.open"
                            (Submissions.fields (RegistrationInput.values DomainInputs.registration)))
                        token
                    |> Async.AwaitTask

                let parent = accepted opened |> _.OperationId |> CaseId.ofOperationId

                let! decided =
                    Service.executeSubmission
                        service
                        (request
                            fixture
                            11
                            "CASE-MARKERS"
                            1L
                            "case.decide"
                            (Submissions.fields (DecisionInput.values DomainInputs.decision)))
                        token
                    |> Async.AwaitTask

                accepted decided |> ignore

                for number, revision, amount in [ 12, 2L, "10"; 13, 3L, "5" ] do
                    let input =
                        DomainInputs.paymentWith
                            [ BusinessField.PaidAmount, amount ]
                            DomainInputs.payment
                        |> PaymentInput.values
                        |> Submissions.fields

                    let! paid =
                        Service.executeSubmission
                            service
                            (request
                                fixture
                                number
                                "CASE-MARKERS"
                                revision
                                "case.recordPayment"
                                input)
                            token
                        |> Async.AwaitTask

                    accepted paid |> ignore

                let first = OperationId.create (id fixture 12) |> Option.get

                let removal =
                    Submissions.create
                        "case.removePayment"
                        (id fixture 14)
                        (Binding.CasePayment(
                            CaseSelector.Id parent,
                            Revision.create 4L |> Option.get,
                            first
                        ))
                        Map.empty

                let! removed = Service.executeSubmission service removal token |> Async.AwaitTask
                accepted removed |> ignore

                use! connection =
                    database.OwnerSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask

                let markers () =
                    task {
                        let! rows =
                            DatabaseSession.rows
                                connection
                                "SELECT payment_id,removed,payment_date IS NULL AND paid_amount IS NULL FROM claimcore.case_payments WHERE case_id=$1"
                                [| box (CaseId.value parent) |]
                                token

                        return
                            rows
                            |> List.map (fun row ->
                                DatabaseSession.value<Guid> 0 row,
                                (DatabaseSession.value<bool> 1 row,
                                 DatabaseSession.value<bool> 2 row))
                            |> Map.ofList
                    }

                let! before = markers () |> Async.AwaitTask

                Expect.equal
                    before[id fixture 12]
                    (true, false)
                    "Selected payment removal is persisted before erasure"

                Expect.equal
                    before[id fixture 13]
                    (false, false)
                    "Other payment remains active before erasure"

                let erase = request fixture 15 "CASE-MARKERS" 5L "case.erase" Map.empty
                let! erased = Service.executeSubmission service erase token |> Async.AwaitTask
                accepted erased |> ignore
                let! after = markers () |> Async.AwaitTask

                Expect.equal
                    after[id fixture 12]
                    (true, true)
                    "Erasure retains the selected removal marker and clears payment values"

                Expect.equal
                    after[id fixture 13]
                    (false, true)
                    "Erasure preserves the other marker independently"

                let! stored =
                    OperationStorage.find
                        connection
                        fixture.InstallationId
                        (OperationId.create removal.OperationId |> Option.get)
                        token
                    |> Async.AwaitTask

                let stored = stored |> Option.get

                match stored.Root.Structure.Binding with
                | Binding.CasePayment(CaseSelector.Id actual, revision, selected) ->
                    Expect.equal
                        (actual, Revision.value revision, selected)
                        (parent, 4L, first)
                        "Public selection and reviewed version survive"
                | _ -> failtest "Retained payment selection required"

                for change in stored.Changes do
                    match change.Value with
                    | PartValue.Payment payment ->
                        Expect.isNone payment.Entry "Private payment bytes are unavailable"
                    | PartValue.Case state ->
                        Expect.isNone state.Content "Private case bytes are unavailable"
                    | _ -> failtest "Only payment and dependent case parts expected"

                let! privateRows =
                    DatabaseSession.rows
                        connection
                        "SELECT count(*) FROM claimcore.ledger_compartments WHERE owner_kind='FAMILY' AND owner_id=$1 AND (payload IS NOT NULL OR nonce IS NOT NULL)"
                        [| box (CaseId.value parent) |]
                        token
                    |> Async.AwaitTask

                Expect.equal
                    (DatabaseSession.value<int64> 0 privateRows.Head)
                    0L
                    "Family erasure clears every private byte and nonce"

                let! verified =
                    Verification.verify database.OwnerSource None token |> Async.AwaitTask

                match verified with
                | VerificationResult.Verified _ -> ()
                | other -> failtestf "Erased family failed full verification: %A" other
            finally
                database.Fixture.Dispose()
        })
