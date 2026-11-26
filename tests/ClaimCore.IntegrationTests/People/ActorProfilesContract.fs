module ClaimCore.TestSupport.ActorProfilesContract

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
    |> Result.defaultWith (fun issue -> failtestf "Unexpected profile fault: %A" issue)

let private read fixture action =
    fixture.Store.Read
        {
            Work = action
            CancellationToken = token
        }

let private operation fixture id =
    task {
        let! result =
            read fixture (fun tx -> LedgerReads.findOperation tx (CaseFixtures.operationId id))

        return
            result
            |> required
            |> Option.defaultWith (fun () -> failtest "Accepted metadata is missing")
    }

let private execute (owner: ServiceContext) operation target taskName values =
    task {
        let! input = personSubmission owner.Store operation target (None) taskName values
        return! Service.executeSubmission owner input token
    }

let private self owner =
    task {
        let! result = Service.context owner () token

        match result with
        | Outcome.Succeeded {
                                Requester = RequesterState.Active context
                            } -> return context.Self
        | other -> return failtestf "Owner context failed: %A" other
    }

let internal lifecycle fixture =
    task {
        let! owner = bootstrap fixture
        let ownerId = CaseFixtures.actorId (id fixture 1)

        let profile: ActorProfileInput =
            {
                DisplayName = "Authored profile name"
                DigestEmail = None
                Contact = Some "private-contact@example.test"
            }

        let! changed =
            execute
                (owner)
                ((id fixture 301))
                (fixture.Owner)
                "people.setProfile"
                (ClaimCore.TestSupport.Submissions.profile (profile))

        let changed = accepted changed

        Expect.equal
            (changed.RecordedBy |> AuthorityRef.actor |> Option.get).DisplayName
            (DisplayName.Named(PartyName.parse profile.DisplayName |> required))
            "Self edit returns the post-state name"

        let! current = self owner

        Expect.equal
            (AuthorityRef.Actor current)
            changed.RecordedBy
            "Context uses the same current name"

        let! granted =
            execute
                (owner)
                ((id fixture 302))
                (fixture.Owner)
                "people.grant"
                (ClaimCore.TestSupport.Submissions.grant (
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    }
                ))

        accepted granted |> ignore
        let! identityBefore = operation fixture (id fixture 1)
        let! grantBefore = operation fixture (id fixture 302)
        let! profileBefore = operation fixture (id fixture 301)

        let! erased =
            execute (owner) ((id fixture 303)) (fixture.Owner) "people.eraseProfile" (Map.empty)

        let erased = accepted erased

        Expect.equal
            (erased.RecordedBy |> AuthorityRef.actor |> Option.get).DisplayName
            DisplayName.Erased
            "Self erasure receipt cannot retain the old name"

        let! after = self owner
        Expect.equal after.DisplayName DisplayName.Erased "No identity-provider subject fallback"
        Expect.equal after.ActorId ownerId "Identity is retained"
        let! identityAfter = operation fixture (id fixture 1)
        let! grantAfter = operation fixture (id fixture 302)
        let! profileAfter = operation fixture (id fixture 301)

        Expect.equal
            identityAfter.Root.Position
            identityBefore.Root.Position
            "Registration proof position is unchanged"

        Expect.equal
            identityAfter.Root.Structure.Binding
            identityBefore.Root.Structure.Binding
            "Registration identity remains live"

        Expect.equal grantAfter.Root.Hash grantBefore.Root.Hash "Accepted grant proof remains live"

        Expect.equal
            (grantAfter.Changes.Head.Value |> PartValue.clear)
            (grantBefore.Changes.Head.Value |> PartValue.clear)
            "Public grant meaning survives"

        Expect.equal
            profileAfter.Root.Position
            profileBefore.Root.Position
            "Profile proof position is unchanged"

        Expect.isTrue
            (profileAfter.Root.Structure.Compartments
             |> List.forall (Compartment.content >> Option.isNone))
            "Profile bytes and nonce are cleared together"

        Expect.isNone
            (OperationIntent.accepted identityAfter (fun _ -> None))
            "Erased registration profile prevents exact replay"

        let! replay =
            Service.provisionOwner
                owner
                (id fixture 1)
                DomainInputs.ownerProfile
                []
                CaseReferencePolicy.CustomAllowed
                token

        Expect.equal
            replay
            (Ok(Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)))
            "Accepted existence must not become exact replay after erasure"

        let! resolved = Service.operationGet owner { OperationId = id fixture 301 } token

        match resolved.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, true)) ->
            Expect.equal
                receipt.Position
                profileBefore.Root.Position
                "Lookup keeps accepted metadata"
        | other -> failtestf "Erasure keeps accepted metadata available: %A" other

        let replacement: ActorProfileInput =
            {
                DisplayName = "New authored profile"
                DigestEmail = None
                Contact = None
            }

        let! renamed =
            execute
                (owner)
                ((id fixture 304))
                (fixture.Owner)
                "people.setProfile"
                (ClaimCore.TestSupport.Submissions.profile (replacement))

        accepted renamed |> ignore

        let! reapplied =
            fixture.Store.Write
                {
                    Work =
                        (fun tx ->
                            task {
                                let! _ =
                                    DatabaseSession.execute
                                        tx.Connection
                                        "SELECT claimcore.clear_owner('PROFILE',$1)"
                                        [| box (ActorId.value ownerId) |]
                                        token

                                return Verdict.Proceed()
                            })
                    CancellationToken = token
                }

        Expect.equal
            reapplied
            (Ok(Verdict.Proceed()))
            "The retained erasure frontier can be reapplied"

        let! present = self owner

        Expect.equal
            present.DisplayName
            (DisplayName.Named(PartyName.parse replacement.DisplayName |> required))
            "A later re-profile survives an older erasure"

        let! fresh = operation fixture (id fixture 304)

        Expect.isTrue
            (fresh.Root.Structure.Compartments
             |> List.exists (Compartment.content >> Option.isSome))
            "Later profile content survives"

        let! replay =
            Service.provisionOwner
                owner
                (id fixture 1)
                DomainInputs.ownerProfile
                []
                CaseReferencePolicy.CustomAllowed
                token

        Expect.equal
            replay
            (Ok(Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)))
            "A new profile cannot restore old exact identity"

        let! grants =
            read fixture (fun tx ->
                PeopleReads.grantPage
                    tx
                    {
                        ActorId = ownerId
                        AfterGrant = None
                        Limit = 50
                    })

        Expect.isTrue
            ((grants |> required).Items
             |> List.exists (fun grant -> grant.Grant.Role = Role.Owner))
            "Profile erasure leaves owner protection intact"
    }

let internal registrationIsolation fixture =
    task {
        let! owner = bootstrap fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "new-profile-identity" |> required

        let profile: ActorProfileInput =
            {
                DisplayName = "Distinct authored name"
                DigestEmail = None
                Contact = Some "distinct-private-contact"
            }

        let! registered =
            execute
                (owner)
                ((id fixture 311))
                (principal)
                "people.register"
                (ClaimCore.TestSupport.Submissions.profile (profile))

        let receipt = accepted registered
        let! stored = operation fixture (id fixture 311)

        Expect.equal
            stored.Root.Structure.Effects.Length
            1
            "Registration has one accepting person part"

        Expect.equal
            receipt.Position
            stored.Root.Position
            "One root proves the complete registration"

        Expect.equal
            stored.Root.Structure.Task
            (TaskId.ofLiteral "people.register")
            "The task is retained"

        match stored.Changes.Head.Value with
        | PartValue.Person {
                               Principal = Some actual
                               Profile = ProfileValue.Present(Some saved)
                           } ->
            Expect.equal actual principal "Public principal remains exact"

            Expect.equal
                (ActorProfile.authored saved)
                profile
                "The profile compartment owns all private values"
        | _ -> failtest "Complete person registration required"

        Expect.equal
            stored.Root.Structure.Compartments.Length
            1
            "Public identity does not mint a private nonce"

        Expect.isSome
            (stored.Root.Structure.Compartments |> List.exactlyOne |> Compartment.content)
            "The single private profile is salted and available"

        let! directory =
            Service.actors
                owner
                {
                    Role = None
                    Text = None
                    Limit = 50
                    AfterCursor = None
                }
                token

        match directory with
        | Outcome.Succeeded page ->
            let item =
                page.Items
                |> List.find (fun item ->
                    item.Actor.ActorId =
                        ActorId.ofOperationId (CaseFixtures.operationId (id fixture 311)))

            Expect.equal
                item.Actor.DisplayName
                (DisplayName.Named(PartyName.parse profile.DisplayName |> required))
                "Directory references carry the authored name"

            Expect.equal
                (item.Contact |> Option.map ContactText.format)
                profile.Contact
                "Contact is explicit directory data"
        | other -> failtestf "Directory failed: %A" other
    }

let internal atomicWrites fixture =
    task {
        let! owner = bootstrap fixture

        let target =
            PrincipalKey.human "https://issuer.example/" "rolled-back-profile" |> required

        let fail (submission: OperationSubmission) phase =
            fixture.Store.Write
                {
                    CancellationToken = token
                    Work =
                        fun tx ->
                            task {
                                let selected =
                                    ClaimCore.Domain.Tasks.Registry.find submission.Task
                                    |> Option.get

                                let input = Submissions.command submission

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
                                        None

                                let plan =
                                    match prepared |> required with
                                    | PreparedDecision.Ordinary(plan, _) -> plan
                                    | _ -> failtest "Expected an ordinary decision."

                                let record =
                                    RetainedOperation.create
                                        plan.Frame.Header
                                        submission.Task
                                        plan.Frame.Binding
                                        plan.Frame.Inputs
                                        plan.Changes
                                        tx.NewPayloadNonce
                                    |> required

                                do! tx.Append record

                                let rows =
                                    match plan.After with
                                    | AggregateState.Person(Some person) ->
                                        ProjectionRows.person person |> required
                                    | _ -> failtest "Person plan required"

                                if phase = 1 then
                                    do! PartProjections.personIdentity tx.Connection rows token
                                else
                                    do! tx.ProjectPerson rows

                                return
                                    failwith
                                        "injected after actual person append and projection phase"
                            }
                }

        for number, phase in [ 321, 1; 322, 2 ] do
            let! input =
                personSubmission
                    fixture.Store
                    (id fixture number)
                    target
                    (Some 0L)
                    "people.register"
                    (Submissions.profile DomainInputs.actorProfile)

            let! failed = fail input phase

            Expect.equal
                failed
                (Error StoreFailure.Internal: Result<Verdict<unit>, StoreFailure>)
                "Partial and complete person projection both roll back"

            let! existing =
                read fixture (fun tx ->
                    LedgerReads.findOperation tx (CaseFixtures.operationId input.OperationId))

            Expect.equal existing (Ok None) "No root or effect remains"
            let! actor = read fixture (fun tx -> AccessReads.access tx target None)
            Expect.isNone (actor |> required).Actor "No profile-less registration remains"

        let! original = operation fixture (id fixture 1)

        let! erase =
            personSubmission
                fixture.Store
                (id fixture 323)
                fixture.Owner
                None
                "people.eraseProfile"
                Map.empty

        let! failed = fail erase 2

        Expect.equal
            failed
            (Error StoreFailure.Internal: Result<Verdict<unit>, StoreFailure>)
            "Private erasure participates in rollback"

        let! restored = operation fixture (id fixture 1)

        Expect.equal
            restored.Root
            original.Root
            "Rollback restores private bytes, nonce and root metadata"

        let! current = self owner

        Expect.equal
            current.DisplayName
            (DisplayName.Named(PartyName.parse DomainInputs.ownerProfile.DisplayName |> required))
            "Rollback restores the current profile"
    }

let internal signInIdentityChange fixture =
    task {
        let! owner = caseWorker fixture

        let create subject =
            PrincipalKey.human "https://issuer.example/" subject |> required

        let prior = create "old-sub"
        let replacement = create "new-sub"

        let! registered =
            execute
                owner
                (id fixture 80)
                prior
                "people.register"
                (ClaimCore.TestSupport.Submissions.profile
                    ClaimCore.TestSupport.DomainInputs.actorProfile)

        acceptedPeople registered

        let actor = CaseFixtures.actorId (id fixture 80)

        let! granted =
            grant
                owner
                (id fixture 81)
                prior
                {
                    GrantInput.Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted

        let! opened =
            Service.executeSubmission
                owner
                (request
                    fixture
                    82
                    "REASSIGNED-IDENTITY"
                    0L
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values registration
                    )))
                token

        let case = accepted opened |> caseReceipt
        let target = CaseSelector.Id case.CaseId

        let! assigned =
            Service.executeSubmission
                owner
                (ClaimCore.TestSupport.Submissions.create
                    "work.update"
                    (id fixture 83)
                    (Binding.Work(target, Revision.zero))
                    (ClaimCore.TestSupport.Submissions.work
                        {
                            CaseWorkInput.Responsible = WorkValue.Set(ActorId.format actor)
                            Summary = WorkValue.Keep
                        }))
                token

        accepted assigned |> ignore

        let identityValues principal =
            ClaimCore.TestSupport.Submissions.principal principal
            |> Map.remove (ClaimCore.TestSupport.Submissions.input InputTarget.PrincipalKind)

        let! preview =
            task {
                let! command =
                    personSubmission
                        fixture.Store
                        (id fixture 84)
                        fixture.Owner
                        None
                        "people.changeSignInIdentity"
                        (identityValues (create "owner-replacement"))

                return! Service.previewSubmission owner command token
            }

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed review) ->
            Expect.isTrue
                review.ChangesOwnSignInIdentity
                "A self-change is identified for rendering"
        | other -> failtestf "Expected self-change review: %A" other

        let! changed =
            execute
                owner
                (id fixture 85)
                prior
                "people.changeSignInIdentity"
                (identityValues replacement)

        acceptedPeople changed

        let! obsolete = Service.get (core fixture prior) { Target = target } token

        Expect.equal
            obsolete
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "The old principal cannot reach an assigned case"

        let! current = Service.get (core fixture replacement) { Target = target } token

        match current with
        | Outcome.Succeeded case ->
            Expect.isTrue
                case.Mine
                "The replacement identity inherits the actor's existing responsibility"

            Expect.equal
                (case.Work.Responsible |> Option.map _.ActorId)
                (Some actor)
                "The case still points to the original actor"
        | other -> failtestf "The replacement identity lost its case access: %A" other

        let! before = operation fixture (id fixture 80)
        let! after = operation fixture (id fixture 85)

        Expect.equal
            before.Changes.Head.PartId
            after.Changes.Head.PartId
            "Registration and change share one history"

        let! returned =
            execute
                owner
                (id fixture 86)
                replacement
                "people.changeSignInIdentity"
                (identityValues prior)

        acceptedPeople returned
        let! restored = Service.get (core fixture prior) { Target = target } token

        match restored with
        | Outcome.Succeeded case ->
            Expect.isTrue case.Mine "Returning to the earlier key restores its access"
        | other ->
            failtestf "The old identity could not be reused by its original account: %A" other
    }
