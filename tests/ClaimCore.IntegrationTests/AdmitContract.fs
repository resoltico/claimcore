module ClaimCore.TestSupport.AdmitContract

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private required result =
    result
    |> Result.defaultWith (fun error -> failtestf "Unexpected admission fault: %A" error)

let private successful =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected admitted read: %A" other

let private principal value =
    PrincipalKey.human "https://issuer.example/" value |> required

let private execute (owner: ServiceContext) operation target taskName values =
    task {
        let declaration =
            ClaimCore.Domain.Tasks.Registry.findDeclaration (TaskId.ofLiteral taskName)
            |> Option.get

        let! input =
            personSubmission
                owner.Store
                operation
                target
                (Some(if declaration.Creates then 0L else 1L))
                taskName
                values

        return! Service.executeSubmission owner input token
    }

let private profile =
    {
        ActorProfileInput.DisplayName = "Authored admission name"
        DigestEmail = None
        Contact = Some "authored@example.invalid"
    }

let private lookup fixture operation =
    fixture.Store.Read
        {
            Work = (fun tx -> LedgerReads.findOperation tx (CaseFixtures.operationId operation))
            CancellationToken = token
        }

let private actor fixture target =
    fixture.Store.Read
        {
            Work = (fun tx -> AccessReads.access tx (target) (None))
            CancellationToken = token
        }

let private effectKinds (receipt: OperationReceipt) =
    match receipt.Subject with
    | ReceiptSubject.People value -> value, value.Effects |> List.map _.Act
    | _ -> failtest "Expected people receipt"

let internal lifecycle fixture =
    task {
        let! owner = caseWorker fixture
        let first = "ADMIT@CASE"
        let uuidReference = "10000000-0000-4000-8000-000000000777"

        let! a =
            Service.executeSubmission
                owner
                (((request
                    (fixture)
                    (731)
                    (first)
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                token

        let a = accepted a |> caseReceipt

        let! b =
            Service.executeSubmission
                owner
                (((request
                    (fixture)
                    (732)
                    (uuidReference)
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                token

        let b = accepted b |> caseReceipt

        Expect.notEqual
            (CaseId.value b.CaseId)
            (Guid.Parse uuidReference)
            "A UUID-looking reference still resolves as a reference"

        for operation, role in [ 733, Role.CaseHandler; 734, Role.PaymentRecorder ] do
            let! removed =
                grant
                    owner
                    (id fixture operation)
                    fixture.Owner
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    false

            acceptedPeople removed

        let! ownerContext = Service.context owner () token
        let ownerContext = successful ownerContext |> Inputs.activeContext

        Expect.isFalse
            (List.contains Capability.ReadCase ownerContext.Capability)
            "Control owner has people management without case-read access"

        let target = principal "admitted-person"

        let! pending =
            Service.context
                (core fixture target
                 |> Service.withClaims (
                     RequesterClaims.admit (Some "IdP hint must not become a profile") None None
                 ))
                ()
                token

        match pending with
        | Outcome.Succeeded {
                                Requester = RequesterState.NotRegistered _
                            } -> ()
        | other -> failtestf "Expected access request: %A" other

        let grants =
            [
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.CaseReference first
                }
                {
                    Role = Role.Auditor
                    Scope = GrantTarget.CaseReference uuidReference
                }
            ]

        let values =
            Submissions.profile profile
            |> Map.add
                (Submissions.input InputTarget.Grants)
                (SubmissionValue.Rows(grants |> List.map Submissions.grant))

        let! operation =
            personSubmission fixture.Store (id fixture 740) target (Some 0L) "people.admit" values

        let! preview = Service.previewSubmission owner operation token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed review) ->
            Expect.equal
                review.Changes.Length
                1
                "One accepting person part carries identity, profile and grants"

            match review.Changes.Head.Description with
            | PartDescription.Person changes ->
                Expect.equal changes.Grants.Length 3 "Every distinct scope remains explicit"

                Expect.equal
                    (changes.Profiles.After
                     |> Option.map (fun value -> PartyName.format value.DisplayName))
                    (Some profile.DisplayName)
                    "IdP hints never select the authored profile"

                Expect.equal
                    (changes.Principal |> Option.bind _.After)
                    (Some target)
                    "Admitted principal is exact"
            | _ -> failtest "Person description required"
        | other -> failtestf "Admission review failed: %A" other

        let! acceptedReply = Service.executeSubmission owner operation token
        let receipt = accepted acceptedReply
        let metadata, kinds = effectKinds receipt

        Expect.equal
            metadata.Act
            (TaskId.ofLiteral "people.admit")
            "Logical operation remains ADMIT"

        Expect.equal
            kinds
            [ TaskId.ofLiteral "people.admit" ]
            "Receipt identifies the single accepting part"

        Expect.equal
            (metadata.Effects |> List.map (fun effect -> effect.Id, effect.Position))
            [
                (EffectId.create receipt.OperationId 0 |> Option.get),
                (EffectPosition.create receipt.Position.Sequence 0 |> Option.get)
            ]
            "All assignments share the actual accepting position"

        let! stored = lookup fixture (id fixture 740)

        let stored =
            required stored
            |> Option.defaultWith (fun () -> failtest "Complete operation missing")

        Expect.equal
            stored.Root.Structure.Effects.Length
            1
            "One person part owns every accepted value"

        Expect.equal stored.Root.Position receipt.Position "Store and receipt have one root proof"

        Expect.equal
            stored.Root.Structure.Task
            (TaskId.ofLiteral "people.admit")
            "Task identity is retained"

        match stored.Changes.Head.Value with
        | PartValue.Person value ->
            Expect.equal value.Grants.Length 3 "All accepted assignments are present"
        | _ -> failtest "Person part required"

        let privateProfile = stored.Root.Structure.Compartments |> List.exactlyOne

        Expect.equal
            privateProfile.Descriptor.Owner
            (CompartmentOwner.Profile(ActorId.ofOperationId receipt.OperationId))
            "Profile owns its private values"

        Expect.isSome
            (Compartment.content privateProfile)
            "The profile has one live salt/content pair"

        let publicBytes =
            Array.concat (
                OperationRoot.structureBytes stored.Root
                :: (OperationRoot.effectBytes stored.Root |> List.map snd)
            )
            |> Text.Encoding.UTF8.GetString

        Expect.isFalse
            (publicBytes.Contains(profile.DisplayName, StringComparison.Ordinal))
            "Public command and grant metadata contain no authored profile"

        let! directory =
            Service.actors
                owner
                {
                    Role = None
                    Text = None
                    AfterCursor = None
                    Limit = 50
                }
                token

        let person =
            (successful directory).Items
            |> List.find (fun value -> value.Principal = target)

        Expect.equal
            person.Actor.DisplayName
            (DisplayName.Named(PartyName.parse profile.DisplayName |> required))
            "Current names come from the authored profile"

        let! requests = Service.accessRequests owner { AfterCursor = None; Limit = 50 } token

        Expect.isFalse
            ((successful requests).Items
             |> List.exists (fun value -> value.Principal = target))
            "Admission deletes its matching access request"

        let aliasGrants =
            [
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.CaseId a.CaseId
                }
                {
                    Role = Role.Auditor
                    Scope = GrantTarget.CaseId b.CaseId
                }
            ]

        let! replay =
            execute
                (owner)
                ((id fixture 740))
                (target)
                "people.admit"
                (ClaimCore.TestSupport.Submissions.profile (profile)
                 |> Map.add
                     (TaskInputId.Target InputTarget.Grants)
                     (ClaimCore.Application.Contracts.SubmissionValue.Rows(
                         (aliasGrants) |> List.map ClaimCore.TestSupport.Submissions.grant
                     )))

        expectReceipt
            (replayReceipt replay)
            receipt
            "Reference and ID aliases replay the same normalized operation"

        let! erased =
            execute (owner) ((id fixture 741)) (target) "people.eraseProfile" (Map.empty)

        acceptedPeople erased
        let! hidden = Service.executeSubmission owner operation token

        Expect.equal
            hidden.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Erased profile never leaves an exact replay fingerprint"

        let! resolved = Service.operationGet owner { OperationId = id fixture 740 } token

        match resolved.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted(_, true)) -> ()
        | other -> failtestf "Erasure cannot make accepted admission not accepted: %A" other
    }

let internal boundary fixture =
    task {
        let! owner = caseWorker fixture

        let! opened =
            Service.executeSubmission
                owner
                (((request
                    (fixture)
                    (750)
                    ("ADMIT-DUPLICATE")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                token

        let current = accepted opened |> caseReceipt
        let target = principal "duplicate-admission"

        let duplicate =
            [
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseReference "admit-duplicate"
                }
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseId current.CaseId
                }
            ]

        let! refused =
            execute
                (owner)
                ((id fixture 751))
                (target)
                "people.admit"
                (ClaimCore.TestSupport.Submissions.profile (profile)
                 |> Map.add
                     (TaskInputId.Target InputTarget.Grants)
                     (ClaimCore.Application.Contracts.SubmissionValue.Rows(
                         (duplicate) |> List.map ClaimCore.TestSupport.Submissions.grant
                     )))

        match refused.Outcome with
        | ExecuteResult.Rejected(Refusal.InvalidInput issues) ->
            Expect.equal
                (InputIssues.head issues).Violation
                Violation.DuplicateGrant
                "Normalized role/scope pairs must be distinct"
        | other -> failtestf "Expected normalized duplicate refusal: %A" other

        let! absent = actor fixture target
        Expect.isNone (required absent).Actor "Refused admission creates no partial identity"

        for operation, grants in
            [
                752, []
                753,
                List.replicate
                    49
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.Installation
                    }
            ] do
            let! refused =
                execute
                    (owner)
                    ((id fixture operation))
                    (target)
                    "people.admit"
                    (ClaimCore.TestSupport.Submissions.profile (profile)
                     |> Map.add
                         (TaskInputId.Target InputTarget.Grants)
                         (ClaimCore.Application.Contracts.SubmissionValue.Rows(
                             (grants) |> List.map ClaimCore.TestSupport.Submissions.grant
                         )))

            match refused.Outcome with
            | ExecuteResult.Rejected(Refusal.InvalidInput issues) ->
                Expect.equal
                    (InputIssues.head issues).Violation
                    (Violation.GrantCountRange 48)
                    "Bounded nonempty grants have their own meaning"
            | other -> failtestf "Expected grant-count refusal: %A" other

        let! unavailable =
            execute
                (owner)
                ((id fixture 754))
                (target)
                "people.admit"
                (ClaimCore.TestSupport.Submissions.profile (profile)
                 |> Map.add
                     (TaskInputId.Target InputTarget.Grants)
                     (ClaimCore.Application.Contracts.SubmissionValue.Rows(
                         ([
                             {
                                 Role = Role.CaseReader
                                 Scope = GrantTarget.CaseReference "NO-SUCH-ADMIT-CASE"
                             }
                         ])
                         |> List.map ClaimCore.TestSupport.Submissions.grant
                     )))

        Expect.equal
            unavailable.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Absent scope target remains unavailable"

        let! wrongScope =
            execute
                (owner)
                ((id fixture 755))
                (target)
                "people.admit"
                (ClaimCore.TestSupport.Submissions.profile (profile)
                 |> Map.add
                     (TaskInputId.Target InputTarget.Grants)
                     (ClaimCore.Application.Contracts.SubmissionValue.Rows(
                         ([
                             {
                                 Role = Role.Owner
                                 Scope = GrantTarget.CaseId current.CaseId
                             }
                         ])
                         |> List.map ClaimCore.TestSupport.Submissions.grant
                     )))

        Expect.equal
            wrongScope.Outcome
            (ExecuteResult.Rejected(Refusal.People PeopleRefusal.RoleScopeNotApplicable))
            "Existing role/scope admission remains authoritative"

        let service =
            PrincipalKey.service "https://issuer.example/" "admit-service" |> required

        let! wrongKind =
            execute
                (owner)
                ((id fixture 756))
                (service)
                "people.admit"
                (ClaimCore.TestSupport.Submissions.profile (profile)
                 |> Map.add
                     (TaskInputId.Target InputTarget.Grants)
                     (ClaimCore.Application.Contracts.SubmissionValue.Rows(
                         ([
                             {
                                 Role = Role.Owner
                                 Scope = GrantTarget.Installation
                             }
                         ])
                         |> List.map ClaimCore.TestSupport.Submissions.grant
                     )))

        Expect.equal
            wrongKind.Outcome
            (ExecuteResult.Rejected(Refusal.People PeopleRefusal.HumanPrincipalRequired))
            "Principal-kind restrictions are unchanged"
    }

let internal maximum fixture =
    task {
        let! owner = caseWorker fixture
        let mutable grants = []

        for index in [ 0..47 ] do
            let reference = $"ADMIT-MAX-{index:D2}"

            let! opened =
                Service.executeSubmission
                    owner
                    (((request
                        (fixture)
                        ((760 + index))
                        (reference)
                        (0L)
                        "case.open"
                        (ClaimCore.TestSupport.Submissions.fields (
                            RegistrationInput.values (registration)
                        )))))
                    token

            accepted opened |> ignore

            grants <-
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.CaseReference reference
                }
                :: grants

        let target = principal "maximum-admission"

        let! reply =
            execute
                (owner)
                ((id fixture 810))
                (target)
                "people.admit"
                (ClaimCore.TestSupport.Submissions.profile (profile)
                 |> Map.add
                     (TaskInputId.Target InputTarget.Grants)
                     (ClaimCore.Application.Contracts.SubmissionValue.Rows(
                         (List.rev grants) |> List.map ClaimCore.TestSupport.Submissions.grant
                     )))

        let receipt = accepted reply
        let metadata, _ = effectKinds receipt

        Expect.equal
            metadata.Effects.Length
            1
            "All forty-eight grants share one accepting person part"

        let! stored = lookup fixture (id fixture 810)
        let stored = required stored |> Option.get

        Expect.equal
            stored.Root.Structure.Effects.Length
            1
            "One part retains the complete atomic admission"

        match stored.Changes.Head.Value with
        | PartValue.Person value ->
            Expect.equal value.Grants.Length 48 "Every distinct accepted assignment is retained"
        | _ -> failtest "Person part required"

        Expect.equal
            stored.Root.Structure.Compartments.Length
            1
            "Forty-eight public grants do not mint private salts"

        Expect.isSome
            (stored.Root.Structure.Compartments |> List.exactlyOne |> Compartment.content)
            "The maximum admission retains its one private profile"

    }

let internal atomicWrites fixture =
    task {
        let! owner = bootstrap fixture
        let target = principal "rollback-admission"
        let hints = RequesterClaims.admit (Some "Original waiting hint") None None
        let! _ = Service.context (core fixture target |> Service.withClaims hints) () token
        let! pending = Service.accessRequests owner { AfterCursor = None; Limit = 50 } token

        let original =
            (successful pending).Items |> List.find (fun value -> value.Principal = target)

        let grants =
            [
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                {
                    Role = Role.CaseRegistrar
                    Scope = GrantTarget.Installation
                }
                {
                    Role = Role.Auditor
                    Scope = GrantTarget.Installation
                }
            ]

        for after in [ 1; 2 ] do
            let operation = id fixture (850 + after)

            let values =
                Submissions.profile profile
                |> Map.add
                    (Submissions.input InputTarget.Grants)
                    (SubmissionValue.Rows(grants |> List.map Submissions.grant))

            let! submission =
                personSubmission fixture.Store operation target (Some 0L) "people.admit" values

            let! failed =
                fixture.Store.Write
                    {
                        CancellationToken = token
                        Work =
                            fun tx ->
                                task {
                                    let input = Submissions.command submission

                                    let selected =
                                        ClaimCore.Domain.Tasks.Registry.find submission.Task
                                        |> Option.get

                                    let context =
                                        {
                                            DecisionContext.BusinessDate = None
                                            ObservedAt = tx.ObservedAt
                                            Principal = owner.Principal
                                            Access = AccessView.empty
                                            Maintenance = false
                                            Lifetime = DecisionLifetime.Execution None
                                        }

                                    let! prepared =
                                        Engine.prepare
                                            tx
                                            tx.Head
                                            context
                                            selected
                                            (CaseFixtures.operationId submission.OperationId)
                                            input.Binding
                                            input.Inputs

                                    let plan = prepared |> required

                                    let record =
                                        RetainedOperation.create
                                            plan.Header
                                            submission.Task
                                            plan.Binding
                                            plan.Inputs
                                            plan.Changes
                                            tx.NewPayloadNonce
                                        |> required

                                    do! tx.Append record

                                    let rows =
                                        match plan.After with
                                        | AggregateState.Person(Some person) ->
                                            ProjectionRows.person person |> required
                                        | _ -> failtest "Person plan required"

                                    if after = 1 then
                                        do! PartProjections.personIdentity tx.Connection rows token
                                    else
                                        do! tx.ProjectPerson rows

                                    return
                                        failwith
                                            "injected after actual person append and projection phase"
                                }
                    }

            Expect.equal
                failed
                (Error StoreFailure.Internal: Result<Verdict<unit>, StoreFailure>)
                "A partial or fully staged admission rolls back before commit"

            let! missing = lookup fixture operation
            Expect.equal missing (Ok None) "No accepted operation prefix survives rollback"
            let! absent = actor fixture target

            Expect.isNone
                (required absent).Actor
                "No identity, profile or grant projection survives rollback"

            let! waiting = Service.accessRequests owner { AfterCursor = None; Limit = 50 } token

            let retained =
                (successful waiting).Items |> List.find (fun value -> value.Principal = target)

            Expect.equal
                retained
                original
                "Atomic rollback restores the exact waiting request, hints and timestamps"
    }
