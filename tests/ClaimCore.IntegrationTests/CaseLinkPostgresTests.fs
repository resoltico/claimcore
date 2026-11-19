module ClaimCore.IntegrationTests.CaseLinkPostgresTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private succeeded =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected a readable case: %A" other

let private links database =
    task {
        let! owner = populated database
        let fixture = database.Fixture

        let run actor number task binding inputs =
            Service.executeSubmission
                actor
                (Submissions.createRaw task (id fixture number) binding inputs)
                token

        let get actor reference =
            task {
                let! result =
                    Service.get
                        actor
                        {
                            Target = CaseSelector.Reference reference
                        }
                        token

                return (succeeded result).RelatedCases
            }

        let related reference =
            Map.ofList
                [
                    Submissions.input InputTarget.LinkTargetReference,
                    SubmissionValue.Text reference
                    Submissions.input InputTarget.LinkNote, SubmissionValue.Text "Same accident"
                ]

        let source = CaseSelector.Reference "synthetic-a"
        let target = CaseSelector.Reference "LINK-TARGET"

        let! opened =
            Service.executeSubmission
                owner
                (request fixture 110 "LINK-TARGET" 0L "case.open" Map.empty)
                token

        accepted opened |> ignore

        let! sourceRecord = Service.get owner { Target = source } token

        let! added =
            run owner 111 "link.add" (OperationBinding.Link(source, None)) (related "LINK-TARGET")

        match (accepted added).Subject with
        | ReceiptSubject.Link value ->
            Expect.equal
                (LinkId.value value.LinkId)
                (id fixture 111)
                "Receipt identifies the created link"
        | _ -> failtest "A link receipt is required"

        let! outgoing = get owner "synthetic-a"
        let! incoming = get owner "LINK-TARGET"

        Expect.equal
            (outgoing.Outgoing
             |> List.choose _.Case
             |> List.map (_.Reference >> CaseReference.format))
            [ "LINK-TARGET" ]
            "Source shows the current target"

        Expect.equal
            (outgoing.Outgoing |> List.choose _.Note |> List.map NoteText.format)
            [ "Same accident" ]
            "Source displays the private note"

        Expect.equal
            (incoming.Incoming
             |> List.choose _.Case
             |> List.map (_.Reference >> CaseReference.format))
            [ "synthetic-a" ]
            "Target shows the current source"

        Expect.equal incoming.IncomingCount 1L "Owner sees one readable incoming link"

        let worker =
            PrincipalKey.human "https://issuer.example/" "target-only-handler"
            |> Result.defaultWith (string >> failtest)

        let! registration =
            personSubmission
                fixture.Store
                (id fixture 112)
                worker
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! registered = Service.executeSubmission owner registration token
        acceptedPeople registered

        let! granted =
            grant
                owner
                (id fixture 113)
                worker
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.CaseId((accepted opened |> caseReceipt).CaseId)
                }
                true

        acceptedPeople granted
        let scoped = core fixture worker
        let! restricted = get scoped "LINK-TARGET"
        Expect.isEmpty restricted.Incoming "The source remains unreadable"
        Expect.equal restricted.IncomingCount 0L "A hidden source does not contribute to the count"

        let! hidden =
            run scoped 114 "link.add" (OperationBinding.Link(target, None)) (related "synthetic-a")

        let! missing =
            run
                scoped
                115
                "link.add"
                (OperationBinding.Link(target, None))
                (related "MISSING-RELATED")

        let refusal =
            ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)

        Expect.equal hidden.Outcome refusal "Linking cannot reveal a hidden case"
        Expect.equal missing.Outcome refusal "Missing cases produce the same refusal"

        let sourceGrant =
            {
                Role = Role.CaseHandler
                Scope = GrantTarget.CaseId((succeeded sourceRecord).Snapshot.CaseId)
            }

        let! grantedSource = grant owner (id fixture 123) worker sourceGrant true
        acceptedPeople grantedSource

        let! scopedSource = get scoped "synthetic-a"
        let! scopedTarget = get scoped "LINK-TARGET"

        Expect.equal
            (scopedSource.Outgoing
             |> List.choose _.Case
             |> List.map (_.Reference >> CaseReference.format))
            [ "LINK-TARGET" ]
            "Scoped access reveals the outgoing link"

        Expect.equal
            (scopedSource.Outgoing |> List.map _.CanRemove)
            [ true ]
            "A scoped case handler can remove a link to a readable target"

        Expect.isTrue scopedSource.CanAdd "A scoped case handler can add links"

        Expect.equal
            (scopedTarget.Incoming
             |> List.choose _.Case
             |> List.map (_.Reference >> CaseReference.format))
            [ "synthetic-a" ]
            "Scoped access reveals the incoming link"

        Expect.equal scopedTarget.IncomingCount 1L "A readable source contributes to the count"

        let! revokedSource = grant owner (id fixture 124) worker sourceGrant false
        acceptedPeople revokedSource

        let! afterRevocation = get scoped "LINK-TARGET"
        Expect.isEmpty afterRevocation.Incoming "Revocation hides the incoming link"
        Expect.equal afterRevocation.IncomingCount 0L "Revocation removes the source from the count"

        let! removed =
            run
                owner
                116
                "link.remove"
                (OperationBinding.Link(source, Some(id fixture 111)))
                Map.empty

        accepted removed |> ignore
        let! hidden = get owner "LINK-TARGET"
        Expect.equal hidden.IncomingCount 0L "Removal hides the link in both directions"

        let! added =
            run owner 117 "link.add" (OperationBinding.Link(source, None)) (related "LINK-TARGET")

        accepted added |> ignore

        let! steward =
            grant
                owner
                (id fixture 118)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople steward

        let erase reference first =
            task {
                let! voided =
                    Service.executeSubmission
                        owner
                        (request
                            fixture
                            first
                            reference
                            1L
                            "case.void"
                            (Map.ofList
                                [
                                    Submissions.input InputTarget.Reason,
                                    SubmissionValue.Text "Synthetic cleanup"
                                ]))
                        token

                accepted voided |> ignore

                let! erased =
                    Service.executeSubmission
                        owner
                        (request fixture (first + 1) reference 2L "case.erase" Map.empty)
                        token

                accepted erased |> ignore
            }

        do! erase "LINK-TARGET" 119
        let! erasedTarget = get owner "synthetic-a"

        Expect.equal
            (erasedTarget.Outgoing |> List.choose _.Case |> List.map _.Lifecycle)
            [ Lifecycle.Erased ]
            "Readable erased targets keep their erased status"

        Expect.equal
            (erasedTarget.Outgoing |> List.map _.CanRemove)
            [ false ]
            "An erased target's link has no removal offer"

        let! _ = verified database
        do! erase "synthetic-a" 121
        let! erasedSource = get owner "LINK-TARGET"
        Expect.equal erasedSource.IncomingCount 0L "Source erasure clears the reverse link"

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testCaseAsync "related-case links use current read access in both directions" (run links)
