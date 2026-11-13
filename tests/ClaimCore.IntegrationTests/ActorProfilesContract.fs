module ClaimCore.TestSupport.ActorProfilesContract

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
    |> Result.defaultWith (fun issue -> failtestf "Unexpected profile fault: %A" issue)

let private read fixture action =
    fixture.Store.Read
        {
            Work = action
            CancellationToken = token
        }

let private operation fixture id =
    task {
        let! result = read fixture (fun tx -> tx.FindOperation(CaseFixtures.operationId id))

        return
            result
            |> required
            |> Option.defaultWith (fun () -> failtest "Accepted metadata is missing")
    }

let private execute owner operation target act =
    Service.execute owner (OperationInput.People(Inputs.peopleRequest operation target act)) token

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
                Contact = Some "private-contact@example.test"
            }

        let! changed =
            execute owner (id fixture 301) fixture.Owner (PeopleAct.SetProfile profile)

        let changed = accepted changed

        Expect.equal
            changed.RecordedBy.DisplayName
            (DisplayName.Named(PartyName.parse profile.DisplayName |> required))
            "Self edit returns the post-state name"

        let! current = self owner
        Expect.equal current changed.RecordedBy "Context uses the same current name"

        let! granted =
            execute
                owner
                (id fixture 302)
                fixture.Owner
                (PeopleAct.Grant
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    })

        accepted granted |> ignore
        let! identityBefore = operation fixture (id fixture 1)
        let! grantBefore = operation fixture (id fixture 302)
        let! profileBefore = operation fixture (id fixture 301)
        let! erased = execute owner (id fixture 303) fixture.Owner PeopleAct.EraseProfile
        let erased = accepted erased

        Expect.equal
            erased.RecordedBy.DisplayName
            DisplayName.Erased
            "Self erasure receipt cannot retain the old name"

        let! after = self owner
        Expect.equal after.DisplayName DisplayName.Erased "No identity-provider subject fallback"
        Expect.equal after.ActorId ownerId "Identity is retained"
        let! identityAfter = operation fixture (id fixture 1)
        let! grantAfter = operation fixture (id fixture 302)
        let! profileAfter = operation fixture (id fixture 301)

        Expect.equal
            identityAfter.Position
            identityBefore.Position
            "Registration proof position is unchanged"

        Expect.equal
            identityAfter.Root.Structure.Command
            identityBefore.Root.Structure.Command
            "Registration identity remains live"

        Expect.equal grantAfter.Root grantBefore.Root "Grant meaning remains live"

        Expect.equal
            profileAfter.Position
            profileBefore.Position
            "Profile proof position is unchanged"

        Expect.isTrue
            (profileAfter.Root.Structure.Compartments
             |> List.forall (Compartment.content >> Option.isNone))
            "Profile bytes and nonce are cleared together"

        let validated =
            LedgerEntries.validateOperation fixture.InstallationId identityAfter |> required

        Expect.isNone
            (LedgerEntries.identityBytesOperation validated)
            "Erased registration profile prevents exact replay"

        let! replay =
            Service.provisionOwner owner (id fixture 1) Inputs.ownerProfile [] token

        Expect.equal
            replay
            (Ok(Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)))
            "Accepted existence must not become exact replay after erasure"

        let! resolved = Service.operationGet owner { OperationId = id fixture 301 } token

        match resolved.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, true)) ->
            Expect.equal receipt.Position profileBefore.Position "Lookup keeps accepted metadata"
        | other -> failtestf "Erasure keeps accepted metadata available: %A" other

        let replacement: ActorProfileInput =
            {
                DisplayName = "New authored profile"
                Contact = None
            }

        let! renamed =
            execute owner (id fixture 304) fixture.Owner (PeopleAct.SetProfile replacement)

        accepted renamed |> ignore

        let! reapplied =
            fixture.Store.Write
                {
                    Work =
                        (fun tx ->
                            task {
                                let! state = tx.PeopleState fixture.Owner
                                let rows = state |> Option.bind PeopleSubject.rows |> Option.get

                                do!
                                    tx.ProjectPeople
                                        { rows with
                                            ProfileErasure =
                                                Some(
                                                    EffectPosition.create
                                                        erased.Position.Sequence
                                                        0
                                                    |> Option.get
                                                )
                                        }

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
            Service.provisionOwner owner (id fixture 1) Inputs.ownerProfile [] token

        Expect.equal
            replay
            (Ok(Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)))
            "A new profile cannot restore old exact identity"

        let! grants =
            read fixture (fun tx ->
                tx.GrantPage
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
                Contact = Some "distinct-private-contact"
            }

        let! registered =
            execute owner (id fixture 311) principal (PeopleAct.Register profile)

        let receipt = accepted registered
        let! stored = operation fixture (id fixture 311)

        Expect.equal
            stored.Root.Structure.Effects.Length
            2
            "Registration has identity and profile effects"

        Expect.equal receipt.Position stored.Position "One root proves the complete registration"

        match stored.Root.Structure.Command, stored.Values with
        | CommandSkeleton.People scope,
          RetainedOperationValues.People(RetainedProfile.Available saved) ->
            Expect.equal scope.Principal principal "Public identity remains exact"

            match scope.Act with
            | PeopleAct.Register ordinal ->
                Expect.equal ordinal 0 "Registration references its profile compartment"
            | _ -> failtest "Expected a registration command"

            Expect.equal
                (ActorProfile.authored saved)
                profile
                "The profile compartment owns all private values"
        | _ -> failtest "Expected complete registration values"

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

        let fail operation principal act after =
            let command =
                People.parse (Inputs.peopleRequest operation principal act) |> required

            fixture.Store.Write
                {
                    Work =
                        (fun tx ->
                            task {
                                let! view =
                                    ExecutionFacts.target
                                        owner
                                        tx
                                        (People.factsNeeded command.Act)
                                        command.OperationId
                                        command.Target
                                        []

                                match
                                    PeopleExecution.execute
                                        owner
                                        command
                                        {
                                            Head = tx.Head

                                            NewPayloadNonce = tx.NewPayloadNonce
                                        }
                                        view
                                with
                                | Verdict.Proceed(operation, rows, _) ->
                                    do! tx.Append operation

                                    let partial =
                                        if after = 1 then
                                            { rows with
                                                Actor = { rows.Actor with Profile = None }
                                                DisplayNameSearch = None
                                                ContactSearch = None
                                            }
                                        else
                                            rows

                                    do! tx.ProjectPeople partial

                                    return failwith "injected after people effect and projection"
                                | other ->
                                    return failtestf "Injection requires admitted work: %A" other
                            })
                    CancellationToken = token
                }

        for operation, after in [ id fixture 321, 1; id fixture 322, 2 ] do
            let! failed = fail operation target (PeopleAct.Register Inputs.actorProfile) after

            Expect.equal
                failed
                (Error StoreFailure.Internal: Result<Verdict<unit>, StoreFailure>)
                "Every partial or complete group rolls back on failure"

            let! existing =
                read fixture (fun tx -> tx.FindOperation(CaseFixtures.operationId operation))

            Expect.equal existing (Ok None) "No accepted root or effect remains"

            let! actor = read fixture (fun tx -> tx.Access { Principal = target; CaseId = None })

            Expect.isNone ((actor |> required).Actor) "No profile-less registration remains"

        let! original = operation fixture (id fixture 1)
        let! failed = fail (id fixture 323) fixture.Owner PeopleAct.EraseProfile 1

        Expect.equal
            failed
            (Error StoreFailure.Internal: Result<Verdict<unit>, StoreFailure>)
            "Erasure projection participates in rollback"

        let! restored = operation fixture (id fixture 1)

        Expect.equal
            restored.Root
            original.Root
            "Rollback restores profile bytes, nonce and proof metadata"

        let! current = self owner

        Expect.equal
            current.DisplayName
            (DisplayName.Named(PartyName.parse Inputs.ownerProfile.DisplayName |> required))
            "Rollback restores current profile projection"
    }
