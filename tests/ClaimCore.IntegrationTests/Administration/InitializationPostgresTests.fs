module ClaimCore.IntegrationTests.InitializationPostgresTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Administration
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Wire
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open System.Threading

let private token = CancellationToken.None

let private withDatabase
    (create: unit -> System.Threading.Tasks.Task<PostgresFixture.Database>)
    action
    =
    async {
        let! database = create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private run = withDatabase PostgresFixture.create

let private sql (database: PostgresFixture.Database) statement parameters =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection statement parameters token
        return ()
    }

/// Only this newly owned fixture is reset, before testing fresh installation.
let private reset database =
    sql database "DROP SCHEMA claimcore CASCADE; DROP EXTENSION pg_trgm" [||]

let private owner (database: PostgresFixture.Database) : InitialOwner =
    {
        Principal = database.Fixture.Owner
        Profile = DomainInputs.ownerProfile
        Roles = [ Role.CaseHandler; Role.PaymentRecorder; Role.DataSteward; Role.Auditor ]
    }

let private initialization (database: PostgresFixture.Database) =
    task {
        do! reset database

        use administration =
            new Administration(
                PostgresFixture.ownerConnectionString database,
                BuildIdentity.current.Version,
                database.MasterKey
            )

        let! installation = administration.Initialize("Etc/UTC", owner database, None, token)
        let! status = administration.Status token
        Expect.equal status.InstallationId installation.InstallationId "One committed installation"

        Expect.equal
            status.CaseReferencePolicy
            CaseReferencePolicy.SystemAssignedOnly
            "Fresh omission selects system assignment"

        Expect.equal
            status.Position.Sequence
            Sequence.first
            "Identity, profile, and four additional grants are one complete operation"

        Expect.equal status.Actors 1L "The owner is present with its schema"
        let initial = status.InitialOwner |> Option.get
        let! bootstrap = administration.Head token

        Expect.equal
            initial.DisplayName
            (DisplayName.Named(DomainInputs.name DomainInputs.ownerProfile.DisplayName))
            "Status identifies the authored owner"

        let settings =
            Npgsql.NpgsqlConnectionStringBuilder(PostgresFixture.ownerConnectionString database)

        settings.Username <- "claimcore_runtime"
        settings.Password <- "synthetic-only"

        let! runtime =
            Runtime.openConnection settings.ConnectionString database.MasterKey token

        use runtime = runtime |> Result.defaultWith (string >> failtest)

        let service =
            Service.create
                BuildIdentity.current.Version
                installation.InstallationId
                (Runtime.store runtime)
                database.Fixture.Calendar
                database.Fixture.Owner

        let! context = Service.context service () token

        match context with
        | Outcome.Succeeded {
                                Requester = RequesterState.Active context
                            } ->
            Expect.equal
                (context.Roles |> List.map _.Role |> Set.ofList)
                (Set.ofList
                    [
                        Role.Owner
                        Role.CaseHandler
                        Role.PaymentRecorder
                        Role.DataSteward
                        Role.Auditor
                    ])
                "All requested roles are immediately usable"
        | other -> failtestf "Active owner required: %A" other

        let! opened =
            Service.executeSubmission
                service
                (ClaimCore.TestSupport.Submissions.createRaw
                    "case.open"
                    (Guid.CreateVersion7())
                    (ClaimCore.Application.Contracts.OperationBinding.CaseNew(0L))
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (DomainInputs.registration)
                    )))
                token

        Expect.isTrue
            (match opened.Outcome with
             | ExecuteResult.Accepted _ -> true
             | _ -> false)
            "No self-grant step is needed before opening a case"

        let! verified = administration.Verify(None, token)

        Expect.isTrue
            (match verified with
             | VerificationResult.Verified _ -> true
             | _ -> false)
            "Bootstrap extra-role effects replay"

        let! erase =
            personSubmission
                service.Store
                (Guid.CreateVersion7())
                database.Fixture.Owner
                None
                "people.eraseProfile"
                Map.empty

        let! erased = Service.executeSubmission service erase token
        acceptedPeople erased

        let! reopened =
            Runtime.openConnection settings.ConnectionString database.MasterKey token

        use restarted = reopened |> Result.defaultWith (string >> failtest)

        Expect.equal
            restarted.Installation.CaseReferencePolicy
            CaseReferencePolicy.SystemAssignedOnly
            "Public bootstrap remains sufficient after the owner profile is erased"

        let! checkedHistory = administration.Verify(Some bootstrap, token)

        match checkedHistory with
        | VerificationResult.Verified _ -> ()
        | other -> failtestf "Trusted public bootstrap after profile erasure must verify: %A" other

        let mutable refused = false

        try
            let! _ = administration.Initialize("Etc/UTC", owner database, None, token)
            ()
        with :? InvalidOperationException ->
            refused <- true

        Expect.isTrue refused "Nonempty installation refuses a repeat"

    }

let private rollback (database: PostgresFixture.Database) =
    task {
        do! reset database

        // A cluster event trigger injects a late failure into ordinary owner initialization.
        use! faultInjector = database.SuperuserSource.OpenConnectionAsync()

        let! _ =
            DatabaseSession.execute
                faultInjector
                "CREATE FUNCTION public.reject_initial_grant() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.role='PAYMENT_RECORDER' THEN RAISE EXCEPTION 'synthetic bootstrap failure'; END IF; RETURN NEW; END $$; CREATE FUNCTION public.arm_initial_failure() RETURNS event_trigger LANGUAGE plpgsql AS $$ DECLARE item record; BEGIN FOR item IN SELECT * FROM pg_event_trigger_ddl_commands() LOOP IF item.object_identity='claimcore.grants' AND item.object_type='table' AND NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid=to_regclass('claimcore.grants') AND tgname='reject_initial_grant') THEN EXECUTE 'CREATE TRIGGER reject_initial_grant BEFORE INSERT ON claimcore.grants FOR EACH ROW EXECUTE FUNCTION public.reject_initial_grant()'; END IF; END LOOP; END $$; CREATE EVENT TRIGGER arm_initial_failure ON ddl_command_end WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION public.arm_initial_failure()"
                [||]
                token

        use administration =
            new Administration(
                PostgresFixture.ownerConnectionString database,
                BuildIdentity.current.Version,
                database.MasterKey
            )

        let mutable failure = None

        try
            let! _ = administration.Initialize("Etc/UTC", owner database, None, token)
            ()
        with :? Npgsql.PostgresException as error ->
            failure <- Some(error.SqlState, error.MessageText)

        Expect.equal
            failure
            (Some("P0001", "synthetic bootstrap failure"))
            "The injected late grant failure was reached"

        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! present = Baseline.exists connection token

        Expect.isFalse
            present
            "DDL, owner identity, profile, grant sources and ledger roll back together"
    }

let private policyAdmission (database: PostgresFixture.Database) =
    task {
        use administration =
            new Administration(
                PostgresFixture.ownerConnectionString database,
                BuildIdentity.current.Version,
                database.MasterKey
            )

        let skip requested =
            task {
                try
                    let! result =
                        administration.InitializeIfMissing(
                            "Etc/UTC",
                            owner database,
                            requested,
                            token
                        )

                    return Ok result
                with error ->
                    return Error(Administration.ClassifyFailure error)
            }

        let runtimeSettings = PostgresFixture.runtimeConnectionString database

        let checkRuntimeRefusal expected =
            task {
                let! admitted = Runtime.openConnection runtimeSettings database.MasterKey token

                match admitted with
                | Error fault -> Expect.equal fault expected "Exact runtime admission refusal"
                | Ok runtime ->
                    runtime.Dispose()
                    failtest "Unexpected runtime admission"
            }

        let installation = database.Installation

        Expect.equal
            installation.CaseReferencePolicy
            CaseReferencePolicy.CustomAllowed
            "Explicit opt-in is installed"

        let! status = administration.Status token

        Expect.equal
            status.CaseReferencePolicy
            CaseReferencePolicy.CustomAllowed
            "Owner status reports opt-in"

        let! checkpoint = administration.Head token

        Expect.equal
            checkpoint.Position.Sequence
            Sequence.first
            "The initial owner remains at sequence one"

        for request in [ None; Some CaseReferencePolicy.CustomAllowed ] do
            let! result = skip request

            Expect.equal
                result
                (Ok(InitializationResult.ExistingInstallation installation))
                "Omission and an explicit matching option both keep the existing policy"

        let! conflicting = skip (Some CaseReferencePolicy.SystemAssignedOnly)

        Expect.equal
            conflicting
            (Error(AdministrationFailure.Fault OperatorFault.CaseReferencePolicyMismatch))
            "A conflicting explicit policy is a bounded refusal"

        let! unchanged = administration.Head token
        Expect.equal unchanged checkpoint "Neither skip nor option conflict writes ledger evidence"

        let! admitted = Runtime.openConnection runtimeSettings database.MasterKey token
        use runtime = admitted |> Result.defaultWith (string >> failtest)

        let setPolicy value =
            sql
                database
                "UPDATE claimcore.installation SET case_reference_policy=$1"
                [| box value |]

        do! setPolicy "UNRECOGNIZED"
        do! checkRuntimeRefusal RuntimeOpenFault.InstallationInvalid
        let! invalid = skip None

        Expect.equal
            invalid
            (Error(AdministrationFailure.Fault OperatorFault.InstallationInvalid))
            "Unrecognized policy never defaults to a supported mode"

        do! setPolicy "SYSTEM_ASSIGNED_ONLY"
        do! checkRuntimeRefusal RuntimeOpenFault.InstallationInvalid

        let! ready = runtime.Ready token
        Expect.isFalse ready "The admitted process rejects a changed typed singleton"

        let mutable reachedWrite = false

        let! write =
            (Runtime.store runtime).Write
                {
                    CancellationToken = token
                    Work =
                        fun _ ->
                            reachedWrite <- true
                            System.Threading.Tasks.Task.FromResult(Verdict.Proceed())
                }

        Expect.equal
            write
            (Error StoreFailure.SchemaMismatch)
            "Live transaction refuses policy drift"

        Expect.isFalse reachedWrite "No business write executes under the changed configuration"

        let! mismatched = skip None

        Expect.equal
            mismatched
            (Error(AdministrationFailure.Fault OperatorFault.InstallationInvalid))
            "Recognized singleton tampering does not pass skip"

        let! status =
            task {
                try
                    let! value = administration.Status token
                    return Ok value
                with error ->
                    return Error(Administration.ClassifyFailure error)
            }

        Expect.equal
            status
            (Error(AdministrationFailure.Fault OperatorFault.InstallationInvalid))
            "Recognized singleton tampering cannot report an effective policy"

        let finding =
            {
                Sequence = Some Sequence.first
                Finding = VerificationFinding.BootstrapPolicy
            }

        let! verified = administration.Verify(Some checkpoint, token)

        Expect.equal
            verified
            (VerificationResult.Diverged finding)
            "Verification compares the root with installed policy"

        do! setPolicy "CUSTOM_ALLOWED"

        let rewriteBootstrap changeHeader changePolicy =
            VerificationSupport.rewrite database Sequence.first (fun record ->
                let root = record.Root

                let values =
                    root.Structure.Retained.Values
                    |> Map.add
                        (TaskInputId.Target InputTarget.CaseReferencePolicy)
                        (InputValue.Text changePolicy)

                let collections =
                    root.Structure.Retained.Collections |> Map.map (fun _ -> List.map _.Values)

                let retained = InputMap.create values collections |> Option.get

                VerificationSupport.rehashed
                    (changeHeader root.Header)
                    root.Structure.Task
                    root.Structure.Binding
                    retained
                    root.Structure.Effects
                    root.Structure.Compartments)

        do! rewriteBootstrap (fun header -> header) "SYSTEM_ASSIGNED_ONLY"
        do! checkRuntimeRefusal RuntimeOpenFault.InstallationInvalid
        let! alteredRoot = administration.Verify(None, token)

        Expect.equal
            alteredRoot
            (VerificationResult.Diverged finding)
            "Validly rehashed token mismatch is detected"

        do! setPolicy "SYSTEM_ASSIGNED_ONLY"
        let! internallyVerified = administration.Verify(None, token)

        match internallyVerified with
        | VerificationResult.Verified _ -> ()
        | other -> failtestf "Coherent unchecked history should internally verify: %A" other

        let! trustedVerified = administration.Verify(Some checkpoint, token)

        Expect.equal
            trustedVerified
            (VerificationResult.Diverged
                { finding with
                    Finding = VerificationFinding.ExpectedPosition
                })
            "The original trusted prefix detects a coherent rewrite"

        do!
            rewriteBootstrap
                (fun header ->
                    { header with
                        PreviousHash = checkpoint.Position.EntryHash
                    })
                "SYSTEM_ASSIGNED_ONLY"

        do! checkRuntimeRefusal RuntimeOpenFault.InstallationInvalid
        let! unlinked = administration.Verify(None, token)

        Expect.equal
            unlinked
            (VerificationResult.Diverged
                { finding with
                    Finding = VerificationFinding.Link
                })
            "The correctly hashed first root must follow the zero predecessor"
    }

let private schemaOnlyRefused (database: PostgresFixture.Database) =
    task {
        let! runtime =
            Runtime.openConnection
                (PostgresFixture.runtimeConnectionString database)
                database.MasterKey
                token

        match runtime with
        | Error fault ->
            Expect.equal fault RuntimeOpenFault.InstallationInvalid "Schema alone cannot be served"
        | Ok runtime ->
            runtime.Dispose()
            failtest "A first owner is required for runtime admission"

        use administration =
            new Administration(
                PostgresFixture.ownerConnectionString database,
                BuildIdentity.current.Version,
                database.MasterKey
            )

        let! refusal =
            task {
                try
                    let! _ =
                        administration.InitializeIfMissing("Etc/UTC", owner database, None, token)

                    return None
                with error ->
                    return Some(Administration.ClassifyFailure error)
            }

        Expect.equal
            refusal
            (Some(AdministrationFailure.Fault OperatorFault.InstallationInvalid))
            "Skip cannot adopt a schema-only fixture"
    }

[<Tests>]
let tests =
    testList
        "atomic owner initialization"
        [
            testCaseAsync
                "owner roles are usable at the first runtime connection"
                (run initialization)
            testCaseAsync "late owner grant failure leaves no partial schema" (run rollback)
            testCaseAsync "schema alone cannot be served or skipped" (run schemaOnlyRefused)
            testCaseAsync
                "setup and verification admit the policy proved by the first public root"
                (withDatabase
                    (fun () ->
                        PostgresFixture.createWithReferencePolicy CaseReferencePolicy.CustomAllowed)
                    policyAdmission)
        ]
