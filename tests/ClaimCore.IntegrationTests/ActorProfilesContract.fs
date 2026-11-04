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
    Service.execute
        owner
        (OperationInput.Authority(Inputs.authorityRequest operation target act))
        token

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
        let ownerId = CaseFixtures.actorId (id 1)

        let profile: ActorProfileInput =
            {
                DisplayName = "Authored profile name"
                Contact = Some "private-contact@example.test"
            }

        let! changed = execute owner (id 301) fixture.Owner (AuthorityAct.SetProfile profile)
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
                (id 302)
                fixture.Owner
                (AuthorityAct.Grant
                    {
                        Role = Role.Auditor
                        Scope = GrantTarget.Installation
                    })

        accepted granted |> ignore
        let! identityBefore = operation fixture (id 1)
        let! grantBefore = operation fixture (id 302)
        let! profileBefore = operation fixture (id 301)
        let! erased = execute owner (id 303) fixture.Owner AuthorityAct.EraseProfile
        let erased = accepted erased

        Expect.equal
            erased.RecordedBy.DisplayName
            DisplayName.Erased
            "Self erasure receipt cannot retain the old name"

        let! after = self owner
        Expect.equal after.DisplayName DisplayName.Erased "No identity-provider subject fallback"
        Expect.equal after.ActorId ownerId "Identity is retained"
        let! identityAfter = operation fixture (id 1)
        let! grantAfter = operation fixture (id 302)
        let! profileAfter = operation fixture (id 301)

        Expect.equal
            identityAfter.Position
            identityBefore.Position
            "Registration proof position is unchanged"

        Expect.equal
            identityAfter.Root.Payload
            identityBefore.Root.Payload
            "Registration identity remains live"

        Expect.equal grantAfter.Root.Payload grantBefore.Root.Payload "Grant meaning remains live"

        Expect.equal
            profileAfter.Position
            profileBefore.Position
            "Profile proof position is unchanged"

        Expect.isNone profileAfter.Root.Payload "Only profile bytes are cleared"
        Expect.isNone profileAfter.Root.PayloadNonce "Profile nonce is cleared with its bytes"

        let validated =
            LedgerEntries.validateOperation fixture.InstallationId identityAfter |> required

        Expect.isNone
            (LedgerEntries.identityBytesOperation validated)
            "Erased registration profile prevents exact replay"

        let! replay = Service.provisionOwner owner (id 1) Inputs.ownerProfile token

        Expect.equal
            replay
            (Ok(Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)))
            "Accepted existence must not become exact replay after erasure"

        let! resolved = Service.resolve owner { OperationId = id 301 } token

        match resolved.Outcome with
        | Outcome.Succeeded(Resolved.Accepted receipt) ->
            Expect.equal
                receipt.Position
                profileBefore.Position
                "Settlement keeps accepted metadata"
        | other -> failtestf "Erasure must never fence accepted metadata as NOT_ACCEPTED: %A" other

        let replacement: ActorProfileInput =
            {
                DisplayName = "New authored profile"
                Contact = None
            }

        let! renamed =
            execute owner (id 304) fixture.Owner (AuthorityAct.SetProfile replacement)

        accepted renamed |> ignore

        let! reapplied =
            fixture.Store.Write
                {
                    Work =
                        (fun tx ->
                            task {
                                do!
                                    tx.ProjectAuthority(
                                        AuthorityProjection.EraseProfile(
                                            ownerId,
                                            erased.Position.Sequence
                                        )
                                    )

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

        let! fresh = operation fixture (id 304)
        Expect.isSome fresh.Root.Payload "Later profile payload survives"
        let! replay = Service.provisionOwner owner (id 1) Inputs.ownerProfile token

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
            ((grants |> required).Items |> List.exists (fun grant -> grant.Role = Role.Owner))
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

        let! registered = execute owner (id 311) principal (AuthorityAct.Register profile)
        let receipt = accepted registered
        let! stored = operation fixture (id 311)
        Expect.equal stored.Entries.Length 2 "Registration has identity plus profile effects"
        Expect.equal receipt.Position stored.Position "The receipt proves the final effect"

        let requests =
            stored.Entries
            |> List.map (
                LedgerEntries.payload
                >> required
                >> _.Request
                >> AuthorityRecord.decode 65536
                >> required
            )

        match requests with
        | [ {
                Effect = AuthorityEffect.Register identity
            }
            {
                Effect = AuthorityEffect.SetProfile saved
            } ] ->
            Expect.equal
                identity
                (PrincipalKey.toInput principal)
                "Identity effect contains only the admitted principal"

            Expect.equal
                (ActorProfile.authored saved)
                profile
                "The separate profile effect owns all profile data"
        | other -> failtestf "Wrong effect grammar: %A" other

        let nonces = stored.Entries |> List.map _.PayloadNonce
        Expect.equal (List.distinct nonces).Length 2 "Physical effects use separate salts"

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
                    item.Actor.ActorId = ActorId.ofOperationId (CaseFixtures.operationId (id 311)))

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
                Authority.parse (Inputs.authorityRequest operation principal act) |> required

            fixture.Store.Write
                {
                    Work =
                        (fun tx ->
                            task {
                                let! view =
                                    ExecutionFacts.target
                                        owner
                                        tx
                                        (Authority.factsNeeded command.Act)
                                        command.OperationId
                                        command.Target
                                        []

                                match
                                    AuthorityExecution.execute
                                        owner
                                        command
                                        {
                                            Head = tx.Head
                                            Fenced = false
                                            NewPayloadNonce = tx.NewPayloadNonce
                                        }
                                        view
                                with
                                | Verdict.Proceed(entries, projections, _) ->
                                    for entry, projection in
                                        List.zip entries projections |> List.take after do
                                        do! tx.Append entry
                                        do! tx.ProjectAuthority projection

                                    return
                                        failwith "injected after authority effect and projection"
                                | other ->
                                    return failtestf "Injection requires admitted work: %A" other
                            })
                    CancellationToken = token
                }

        for operation, after in [ id 321, 1; id 322, 2 ] do
            let! failed = fail operation target (AuthorityAct.Register Inputs.actorProfile) after

            Expect.equal
                failed
                (Error StoreFailure.Internal: Result<Verdict<unit>, StoreFailure>)
                "Every partial or complete group rolls back on failure"

            let! existing =
                read fixture (fun tx -> tx.FindOperation(CaseFixtures.operationId operation))

            Expect.equal existing (Ok None) "No accepted root or effect remains"

            let! actor =
                read fixture (fun tx -> tx.Authority { Principal = target; CaseId = None })

            Expect.isNone ((actor |> required).Actor) "No profile-less registration remains"

        let! original = operation fixture (id 1)
        let! failed = fail (id 323) fixture.Owner AuthorityAct.EraseProfile 1

        Expect.equal
            failed
            (Error StoreFailure.Internal: Result<Verdict<unit>, StoreFailure>)
            "Erasure projection participates in rollback"

        let! restored = operation fixture (id 1)

        Expect.equal
            restored.Entries
            original.Entries
            "Rollback restores profile bytes, nonce and proof metadata"

        let! current = self owner

        Expect.equal
            current.DisplayName
            (DisplayName.Named(PartyName.parse Inputs.ownerProfile.DisplayName |> required))
            "Rollback restores current profile projection"
    }
