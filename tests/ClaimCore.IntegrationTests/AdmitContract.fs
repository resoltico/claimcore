module ClaimCore.TestSupport.AdmitContract

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
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

let private execute owner operation target act =
    Service.execute owner (OperationInput.People(Inputs.peopleRequest operation target act)) token

let private profile =
    {
        ActorProfileInput.DisplayName = "Authored admission name"
        Contact = Some "authored@example.invalid"
    }

let private lookup fixture operation =
    fixture.Store.Read
        {
            Work = (fun tx -> tx.FindOperation(CaseFixtures.operationId operation))
            CancellationToken = token
        }

let private actor fixture target =
    fixture.Store.Read
        {
            Work = (fun tx -> tx.Access { Principal = target; CaseId = None })
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
            Service.execute
                owner
                (Inputs.caseOperation (
                    request fixture 731 first 0L (AuthoredCaseAct.Open registration)
                ))
                token

        let a = accepted a |> caseReceipt

        let! b =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request fixture 732 uuidReference 0L (AuthoredCaseAct.Open registration)
                ))
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

        let operation =
            Inputs.peopleRequest
                (id fixture 740)
                target
                (PeopleAct.Admit { Profile = profile; Grants = grants })
            |> OperationInput.People

        let! preview = Service.preview owner operation token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.People review)) ->
            Expect.equal
                review.Effects.Length
                5
                "Review exposes identity, authored profile and every grant"

            Expect.equal review.After.Grants.Length 3 "Review preserves every distinct scope"

            Expect.equal
                (review.After.Profile
                 |> Option.map (fun value -> PartyName.format value.DisplayName))
                (Some profile.DisplayName)
                "Token hints never select the authored profile"

            match review.Effects with
            | PeopleReviewEffect.Register identity :: PeopleReviewEffect.SetProfile _ :: effects ->
                Expect.equal identity target "Review identifies the admitted principal"

                Expect.equal
                    (effects
                     |> List.choose (function
                         | PeopleReviewEffect.Grant grant -> Some grant
                         | _ -> None)
                     |> List.length)
                    3
                    "Only typed grant effects follow profile"
            | _ -> failtest "Wrong ordered review effects"
        | other -> failtestf "Admission review failed: %A" other

        let! acceptedReply = Service.execute owner operation token
        let receipt = accepted acceptedReply
        let metadata, kinds = effectKinds receipt
        Expect.equal metadata.Act PeopleActKind.Admit "Logical operation remains ADMIT"

        Expect.equal
            kinds
            [
                PeopleActKind.Register
                PeopleActKind.SetProfile
                PeopleActKind.Grant
                PeopleActKind.Grant
                PeopleActKind.Grant
            ]
            "Receipt includes every logical effect in order"

        Expect.equal
            (metadata.Effects |> List.map (fun effect -> effect.Id, effect.Position))
            ([ 0..4 ]
             |> List.map (fun index ->
                 (EffectId.create receipt.OperationId index |> Option.get),
                 (EffectPosition.create receipt.Position.Sequence index |> Option.get)))
            "Every ordered effect belongs to the one accepting root"

        let! stored = lookup fixture (id fixture 740)

        let stored =
            required stored
            |> Option.defaultWith (fun () -> failtest "Complete operation missing")

        Expect.equal stored.Root.Structure.Effects.Length 5 "Atomic operation exposes every effect"
        Expect.equal stored.Position receipt.Position "Store and receipt have one root proof"

        match stored.Root.Structure.Command with
        | CommandSkeleton.People { Act = PeopleAct.Admit _ } -> ()
        | _ -> failtest "The command remains ADMIT, not its REGISTER effect"

        let privateProfile = stored.Root.Structure.Compartments |> List.exactlyOne

        Expect.equal
            privateProfile.Descriptor.Purpose
            CompartmentPurpose.Profile
            "Only profile values are private"

        Expect.isSome
            (Compartment.content privateProfile)
            "The profile has one live salt/content pair"

        let publicBytes =
            Array.concat (
                OperationRoot.commandBytes stored.Root
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
                owner
                (id fixture 740)
                target
                (PeopleAct.Admit
                    {
                        Profile = profile
                        Grants = aliasGrants
                    })

        expectReceipt
            (replayReceipt replay)
            receipt
            "Reference and ID aliases replay the same normalized operation"

        let! erased = execute owner (id fixture 741) target PeopleAct.EraseProfile
        acceptedPeople erased
        let! hidden = Service.execute owner operation token

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
            Service.execute
                owner
                (Inputs.caseOperation (
                    request fixture 750 "ADMIT-DUPLICATE" 0L (AuthoredCaseAct.Open registration)
                ))
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
                owner
                (id fixture 751)
                target
                (PeopleAct.Admit
                    {
                        Profile = profile
                        Grants = duplicate
                    })

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
                    owner
                    (id fixture operation)
                    target
                    (PeopleAct.Admit { Profile = profile; Grants = grants })

            match refused.Outcome with
            | ExecuteResult.Rejected(Refusal.InvalidInput issues) ->
                Expect.equal
                    (InputIssues.head issues).Violation
                    (Violation.GrantCountRange 48)
                    "Bounded nonempty grants have their own meaning"
            | other -> failtestf "Expected grant-count refusal: %A" other

        let! unavailable =
            execute
                owner
                (id fixture 754)
                target
                (PeopleAct.Admit
                    {
                        Profile = profile
                        Grants =
                            [
                                {
                                    Role = Role.CaseReader
                                    Scope = GrantTarget.CaseReference "NO-SUCH-ADMIT-CASE"
                                }
                            ]
                    })

        Expect.equal
            unavailable.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Absent scope target remains unavailable"

        let! wrongScope =
            execute
                owner
                (id fixture 755)
                target
                (PeopleAct.Admit
                    {
                        Profile = profile
                        Grants =
                            [
                                {
                                    Role = Role.Owner
                                    Scope = GrantTarget.CaseId current.CaseId
                                }
                            ]
                    })

        Expect.equal
            wrongScope.Outcome
            (ExecuteResult.Rejected(Refusal.People PeopleRefusal.RoleScopeNotApplicable))
            "Existing role/scope admission remains authoritative"

        let service =
            PrincipalKey.service "https://issuer.example/" "admit-service" |> required

        let! wrongKind =
            execute
                owner
                (id fixture 756)
                service
                (PeopleAct.Admit
                    {
                        Profile = profile
                        Grants =
                            [
                                {
                                    Role = Role.Owner
                                    Scope = GrantTarget.Installation
                                }
                            ]
                    })

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
                Service.execute
                    owner
                    (Inputs.caseOperation (
                        request
                            fixture
                            (760 + index)
                            reference
                            0L
                            (AuthoredCaseAct.Open registration)
                    ))
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
                owner
                (id fixture 810)
                target
                (PeopleAct.Admit
                    {
                        Profile = profile
                        Grants = List.rev grants
                    })

        let receipt = accepted reply
        let metadata, _ = effectKinds receipt

        Expect.equal
            metadata.Effects.Length
            50
            "48 distinct grants commit with identity and profile within the declared effect bound"

        let! stored = lookup fixture (id fixture 810)
        let stored = required stored |> Option.get

        Expect.equal
            stored.Root.Structure.Effects.Length
            50
            "Every bounded logical effect is retained"

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

        for after in [ 1..5 ] do
            let operation = id fixture (850 + after)

            let command =
                People.parse (
                    Inputs.peopleRequest
                        operation
                        target
                        (PeopleAct.Admit { Profile = profile; Grants = grants })
                )
                |> required

            let! failed =
                fixture.Store.Write
                    {
                        CancellationToken = token
                        Work =
                            fun tx ->
                                task {
                                    let! view =
                                        ExecutionFacts.target
                                            owner
                                            tx
                                            (People.factsNeeded command.Act)
                                            command.OperationId
                                            command.Target
                                            (PeopleRequests.targetCases command)

                                    let verdict =
                                        ClaimCore.TestSupport.StoreFixtures.executePeople
                                            owner
                                            command
                                            {
                                                Head = tx.Head
                                                NewPayloadNonce = tx.NewPayloadNonce

                                            }
                                            view

                                    match verdict with
                                    | Verdict.Proceed(operation, rows, _) ->
                                        do! tx.Append operation

                                        let grants =
                                            rows.Grants
                                            |> Map.toList
                                            |> List.sortBy snd
                                            |> List.truncate (max 0 (after - 2))
                                            |> Map.ofList

                                        let partial =
                                            if after = 1 then
                                                { rows with
                                                    Actor = { rows.Actor with Profile = None }
                                                    DisplayNameSearch = None
                                                    ContactSearch = None
                                                    Grants = grants
                                                }
                                            else
                                                { rows with Grants = grants }

                                        do! tx.ProjectPeople partial

                                        return
                                            failwith
                                                "Injected failure after an admission effect and projection"
                                    | other ->
                                        return
                                            failtestf "Injection requires valid admission: %A" other
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
