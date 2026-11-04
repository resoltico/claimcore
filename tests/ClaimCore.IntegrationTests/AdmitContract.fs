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
    Service.execute
        owner
        (OperationInput.Authority(Inputs.authorityRequest operation target act))
        token

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
            Work = (fun tx -> tx.Authority { Principal = target; CaseId = None })
            CancellationToken = token
        }

let private effectKinds (receipt: OperationReceipt) =
    match receipt.Subject with
    | ReceiptSubject.Authority value -> value, value.Effects |> List.map _.Act
    | _ -> failtest "Expected authority receipt"

let internal lifecycle fixture =
    task {
        let! owner = caseWorker fixture
        let first = "ADMIT@CASE"
        let uuidReference = "10000000-0000-4000-8000-000000000777"

        let! a =
            Service.execute
                owner
                (Inputs.caseOperation (request 731 first 0L (AuthoredCaseAct.Open registration)))
                token

        let a = accepted a |> caseReceipt

        let! b =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request 732 uuidReference 0L (AuthoredCaseAct.Open registration)
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
                    (id operation)
                    fixture.Owner
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    false

            acceptedAuthority removed

        let! ownerContext = Service.context owner () token
        let ownerContext = successful ownerContext |> Inputs.activeContext

        Expect.isFalse
            (List.contains Capability.ReadCase ownerContext.Capability)
            "Control owner has authority management without case-read access"

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
            Inputs.authorityRequest
                (id 740)
                target
                (AuthorityAct.Admit { Profile = profile; Grants = grants })
            |> OperationInput.Authority

        let! preview = Service.preview owner operation token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Authority review)) ->
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
            | AuthorityReviewEffect.Register identity :: AuthorityReviewEffect.SetProfile _ :: effects ->
                Expect.equal identity target "Review identifies the admitted principal"

                Expect.equal
                    (effects
                     |> List.choose (function
                         | AuthorityReviewEffect.Grant grant -> Some grant
                         | _ -> None)
                     |> List.length)
                    3
                    "Only typed grant effects follow profile"
            | _ -> failtest "Wrong ordered review effects"
        | other -> failtestf "Admission review failed: %A" other

        let! acceptedReply = Service.execute owner operation token
        let receipt = accepted acceptedReply
        let metadata, kinds = effectKinds receipt
        Expect.equal metadata.Act AuthorityKind.Admit "Logical operation remains ADMIT"

        Expect.equal
            kinds
            [
                AuthorityKind.Register
                AuthorityKind.SetProfile
                AuthorityKind.Grant
                AuthorityKind.Grant
                AuthorityKind.Grant
            ]
            "Receipt includes every physical effect in order"

        Expect.equal
            (List.last metadata.Effects).Position
            receipt.Position
            "Receipt proof is the final effect"

        let! stored = lookup fixture (id 740)

        let stored =
            required stored
            |> Option.defaultWith (fun () -> failtest "Complete operation missing")

        Expect.equal stored.Entries.Length 5 "Atomic operation exposes every effect"
        Expect.equal stored.Position receipt.Position "Store and receipt use the same tail proof"

        Expect.equal
            (LedgerEffects.authorityOperation stored.Root.Effects)
            (Some AuthorityKind.Admit)
            "Root operation kind is separate from REGISTER effect kind"

        let nonces = stored.Entries |> List.choose _.PayloadNonce
        Expect.equal nonces.Length 5 "Every live effect has a salt"
        Expect.equal (List.distinct nonces).Length 5 "Effect salts are independently generated"

        for index, entry in List.indexed stored.Entries do
            let bytes =
                entry.Payload
                |> Option.defaultWith (fun () -> failtest "Live effect payload missing")

            if index <> 1 then
                Expect.isFalse
                    (Text.Encoding.UTF8
                        .GetString(bytes)
                        .Contains(profile.DisplayName, StringComparison.Ordinal))
                    "Identity and grant payloads contain no authored profile"

        let! directory = Service.actors owner { AfterCursor = None; Limit = 50 } token

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
                (id 740)
                target
                (AuthorityAct.Admit
                    {
                        Profile = profile
                        Grants = aliasGrants
                    })

        Expect.equal
            replay.Outcome
            (ExecuteResult.AlreadyAccepted receipt)
            "Reference and ID aliases replay the same normalized operation"

        let! erased = execute owner (id 741) target AuthorityAct.EraseProfile
        acceptedAuthority erased
        let! hidden = Service.execute owner operation token

        Expect.equal
            hidden.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Erased profile never leaves an exact replay fingerprint"

        let! resolved = Service.status owner { OperationId = id 740 } token

        match resolved.Outcome with
        | Outcome.Succeeded(OperationStatus.Accepted _) -> ()
        | other -> failtestf "Erasure cannot make accepted admission not accepted: %A" other
    }

let internal boundary fixture =
    task {
        let! owner = caseWorker fixture

        let! opened =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request 750 "ADMIT-DUPLICATE" 0L (AuthoredCaseAct.Open registration)
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
                (id 751)
                target
                (AuthorityAct.Admit
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
                    (id operation)
                    target
                    (AuthorityAct.Admit { Profile = profile; Grants = grants })

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
                (id 754)
                target
                (AuthorityAct.Admit
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
                (id 755)
                target
                (AuthorityAct.Admit
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
            (ExecuteResult.Rejected(Refusal.Authority AuthorityRefusal.RoleScopeNotApplicable))
            "Existing role/scope admission remains authoritative"

        let service =
            PrincipalKey.service "https://issuer.example/" "admit-service" |> required

        let! wrongKind =
            execute
                owner
                (id 756)
                service
                (AuthorityAct.Admit
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
            (ExecuteResult.Rejected(Refusal.Authority AuthorityRefusal.HumanPrincipalRequired))
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
                        request (760 + index) reference 0L (AuthoredCaseAct.Open registration)
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
                (id 810)
                target
                (AuthorityAct.Admit
                    {
                        Profile = profile
                        Grants = List.rev grants
                    })

        let receipt = accepted reply
        let metadata, _ = effectKinds receipt

        Expect.equal
            metadata.Effects.Length
            50
            "48 distinct grants commit with identity and profile within the physical budget"

        let! stored = lookup fixture (id 810)
        let stored = required stored |> Option.get
        Expect.equal stored.Entries.Length 50 "Every bounded effect is retained"

        Expect.equal
            (stored.Entries |> List.choose _.PayloadNonce |> List.distinct |> List.length)
            50
            "The maximum group still has independent salts"
    }
