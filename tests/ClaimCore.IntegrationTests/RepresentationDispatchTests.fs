module ClaimCore.IntegrationTests.RepresentationDispatchTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Server
open ClaimCore.Postgres
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None
let private sync (pending: Task<'a>) = pending.GetAwaiter().GetResult()

let private memberValue name value =
    JsonValue.tryMember name value |> Option.defaultValue JsonValue.Null

let private outcome wire =
    wire |> memberValue "outcome" |> memberValue "kind"

let private dispatch () =
    let database = PostgresFixture.create () |> sync

    try
        let fixture = database.Fixture
        let owner = caseWorker fixture |> sync

        let opened =
            Service.execute
                owner
                (Inputs.caseOperation (
                    actRequest fixture 20 "REP-READ" 0L (AuthoredCaseAct.Open registration)
                ))
                token
            |> sync
            |> accepted

        let mutable reads = 0
        let mutable writes = 0

        let observed =
            { new IStore with
                member _.Context query = fixture.Store.Context query

                member _.Read query =
                    Interlocked.Increment(&reads) |> ignore
                    fixture.Store.Read query

                member _.Write query =
                    Interlocked.Increment(&writes) |> ignore
                    fixture.Store.Write query
            }

        let service = { owner with Store = observed }
        let read = ApiRoutes.route (ActorEndpoint.Query QueryTaskId.CaseGet)

        let prepared =
            read.Prepare(
                Api.caseGet.Input.Write
                    {
                        Target = CaseSelector.Reference "REP-READ"
                    }
            )
            |> Result.defaultWith (fun error -> failtestf "%A" error)

        let mutable completed = 0

        for representation in [ Representation.Human; Representation.Data ] do
            let before = reads

            let reply =
                prepared.Invoke representation (Some service) token (fun () ->
                    completed <- completed + 1)
                |> sync

            Expect.equal reads (before + 1) "One authorized read transaction per representation"
            Expect.equal writes 0 "Read presentation never writes"

            Expect.equal
                (memberValue "representation" reply.Wire)
                (JsonValue.String(Representation.token representation))
                "Requested channel retained"

            Expect.equal
                (outcome reply.Wire)
                (JsonValue.String "SUCCEEDED")
                "Both channels share the successful use case"

            Expect.isOk
                (read.Description.ValidateOutput reply.Wire)
                "Actual exported read envelope is admitted"

        Expect.equal completed 2 "One completion callback per use case"
        let identity = Guid.CreateVersion7()

        let input =
            Inputs.caseOperation
                {
                    OperationId = identity
                    CaseReference = "REP-WRITE"
                    ExpectedRevision = 0L
                    Act = AuthoredCaseAct.Open registration
                }

        let preview = ApiRoutes.route (ActorEndpoint.Operation OperationEndpoint.Preview)

        let preparedPreview =
            preview.Prepare(Api.operationPreview.Input.Write(Submissions.ofCommand None input))
            |> Result.defaultWith (fun error -> failtestf "%A" error)

        let beforeWrites = writes

        for representation in [ Representation.Human; Representation.Data ] do
            let before = reads

            let reply =
                preparedPreview.Invoke representation (Some service) token ignore |> sync

            Expect.equal reads (before + 1) "Preview selects representation after one current read"
            Expect.equal writes beforeWrites "Preview appends nothing"

            Expect.equal
                (outcome reply.Wire)
                (JsonValue.String "REVIEWED")
                "Same operation can be reviewed through either channel"

        let execute = ApiRoutes.route (ActorEndpoint.Operation OperationEndpoint.Execute)

        let prepared =
            execute.Prepare(Api.operationExecute.Input.Write(Submissions.ofCommand None input))
            |> Result.defaultWith (fun error -> failtestf "%A" error)

        let head () =
            LedgerAdministration.head database.OwnerSource token |> sync

        let before = head ()
        let human = prepared.Invoke Representation.Human (Some service) token ignore |> sync
        Expect.equal writes (beforeWrites + 1) "Exactly one write dispatch for HUMAN"
        Expect.equal (outcome human.Wire) (JsonValue.String "ACCEPTED") "First dispatch accepts"
        let acceptedHead = head ()

        Expect.equal
            acceptedHead.Position.Sequence
            (Sequence.successor before.Position.Sequence)
            "Acceptance advances one operation root"

        let data = prepared.Invoke Representation.Data (Some service) token ignore |> sync
        Expect.equal writes (beforeWrites + 2) "DATA exact retry is one replay dispatch"

        Expect.equal
            (outcome data.Wire)
            (JsonValue.String "ALREADY_ACCEPTED")
            "Representation is excluded from business identity"

        Expect.equal (head ()).Position acceptedHead.Position "Replay creates no second root"

        let changed =
            Inputs.caseOperation
                {
                    OperationId = identity
                    CaseReference = "REP-CHANGED"
                    ExpectedRevision = 0L
                    Act = AuthoredCaseAct.Open registration
                }

        let changed =
            execute.Prepare(Api.operationExecute.Input.Write(Submissions.ofCommand None changed))
            |> Result.defaultWith (fun error -> failtestf "%A" error)

        let conflict =
            changed.Invoke Representation.Data (Some service) token ignore |> sync

        Expect.equal
            (outcome conflict.Wire)
            (JsonValue.String "REJECTED")
            "Changed content conflicts under same ID"

        Expect.equal
            (conflict.Wire
             |> memberValue "outcome"
             |> memberValue "rejection"
             |> memberValue "code")
            (JsonValue.String "IDEMPOTENCY_CONFLICT")
            "Conflict is semantic, not duplicate append refusal"

        Expect.equal (head ()).Position acceptedHead.Position "Conflict appends nothing"
        let unavailable = prepared.Invoke Representation.Human None token ignore |> sync

        Expect.equal
            (memberValue "representation" unavailable.Wire)
            (JsonValue.String "HUMAN")
            "Closed runtime keeps channel"

        Expect.equal
            (outcome unavailable.Wire)
            (JsonValue.String "FAILED")
            "No canned success when runtime is absent"

        let lookup = ApiRoutes.route (ActorEndpoint.Query QueryTaskId.OperationGet)

        let lookup =
            lookup.Prepare(
                Api.operationGet.Input.Write
                    {
                        OperationId = OperationId.value opened.OperationId
                    }
            )
            |> Result.defaultWith (fun error -> failtestf "%A" error)

        for representation in [ Representation.Human; Representation.Data ] do
            let reply = lookup.Invoke representation (Some service) token ignore |> sync

            Expect.equal
                (outcome reply.Wire)
                (JsonValue.String "ACCEPTED")
                "Accepted lookup works in both representations"

            Expect.isOk
                ((Api.forActor (ActorEndpoint.Query QueryTaskId.OperationGet)).ValidateOutput
                    reply.Wire)
                "Lookup contract is admitted"
        // Exercise current person controls through the actual authorized query snapshot.
        let reader =
            PrincipalKey.service "https://issuer.example/" "representation-auditor"
            |> Result.defaultWith (string >> failtest)

        let registered =
            Service.execute
                owner
                (OperationInput.People(
                    peopleRequest
                        (id fixture 40001)
                        reader
                        (PeopleAct.Register
                            {
                                DisplayName = "Auditor input"
                                Contact = None
                            })
                ))
                token
            |> sync
            |> accepted

        let readerId =
            match registered.Subject with
            | ReceiptSubject.People value -> PeopleTargetDisclosure.actorId value.TargetActor
            | _ -> failtest "People receipt required"

        grant
            owner
            (id fixture 40002)
            reader
            {
                Role = Role.Auditor
                Scope = GrantTarget.Installation
            }
            true
        |> sync
        |> acceptedPeople

        let person services =
            match
                Service.grants
                    services
                    {
                        ActorId = ActorId.value readerId
                        AfterCursor = None
                        Limit = 50
                    }
                    token
                |> sync
            with
            | Outcome.Succeeded page -> page
            | other -> failtestf "Person query refused: %A" other

        let editable = person owner

        Expect.contains
            editable.Offers
            PeopleActKind.SetProfile
            "Owner receives permitted person task"

        Expect.equal
            editable.ProfileSeed
            (Some
                {
                    DisplayName = "Auditor input"
                    Contact = None
                })
            "Core editable profile"

        let readOnly = person (core fixture reader)
        Expect.isEmpty readOnly.Offers "Auditor has no input controls"
        Expect.isNone readOnly.ProfileSeed "Auditor receives saved display, not an editable seed"

        use administration =
            new Administration(
                database.Container.GetConnectionString(),
                BuildIdentity.current.Version
            )

        administration.Maintenance(MaintenanceChange.On, token) |> sync |> ignore
        let maintained = person owner
        Expect.isEmpty maintained.Offers "Maintenance suppresses person tasks at the query snapshot"
        Expect.isNone maintained.ProfileSeed "Maintenance removes editable profile input"
    finally
        database.Fixture.Dispose()

[<Tests>]
let tests =
    testCase
        "real PostgreSQL representation dispatch shares use cases and operation identity"
        dispatch
