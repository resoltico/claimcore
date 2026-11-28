module ClaimCore.IntegrationTests.CaseLinkPostgresTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Storage
open ClaimCore.Application.Verification
open ClaimCore.RecordFormat
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
                            Target = caseTarget fixture reference
                        }
                        token

                return (succeeded result).RelatedCases
            }

        let caseId name =
            match caseTarget fixture name with
            | CaseSelector.Id id -> id
            | CaseSelector.Reference _ -> CaseId.create (id fixture 199) |> Option.get

        let related name =
            Map.ofList
                [
                    Submissions.input InputTarget.LinkTargetId,
                    SubmissionValue.Text(CaseId.format (caseId name))
                    Submissions.input InputTarget.LinkNote, SubmissionValue.Text "Same accident"
                ]

        let source = caseTarget fixture "synthetic-a"

        let! opened =
            Service.executeSubmission
                owner
                (request fixture 110 "LINK-TARGET" 0L "case.open" Map.empty)
                token

        accepted opened |> ignore
        let target = caseTarget fixture "LINK-TARGET"

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
            (outgoing.Outgoing |> List.choose _.Case |> List.map _.CaseId)
            [ caseId "LINK-TARGET" ]
            "Source shows the current target"

        Expect.equal
            (outgoing.Outgoing |> List.choose _.Note |> List.map NoteText.format)
            [ "Same accident" ]
            "Source displays the private note"

        Expect.equal
            (incoming.Incoming |> List.choose _.Case |> List.map _.CaseId)
            [ caseId "synthetic-a" ]
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
            (scopedSource.Outgoing |> List.choose _.Case |> List.map _.CaseId)
            [ caseId "LINK-TARGET" ]
            "Scoped access reveals the outgoing link"

        Expect.equal
            (scopedSource.Outgoing |> List.map _.CanRemove)
            [ true ]
            "A scoped case handler can remove a link to a readable target"

        Expect.isTrue scopedSource.CanAdd "A scoped case handler can add links"

        Expect.equal
            (scopedTarget.Incoming |> List.choose _.Case |> List.map _.CaseId)
            [ caseId "synthetic-a" ]
            "Scoped access reveals the incoming link"

        Expect.equal scopedTarget.IncomingCount 1L "A readable source contributes to the count"

        let! revokedSource = grant owner (id fixture 124) worker sourceGrant false
        acceptedPeople revokedSource

        let! afterRevocation = get scoped "LINK-TARGET"
        Expect.isEmpty afterRevocation.Incoming "Revocation hides the incoming link"
        Expect.equal afterRevocation.IncomingCount 0L "Revocation removes the source from the count"

        let targetGrant =
            {
                Role = Role.CaseHandler
                Scope = GrantTarget.CaseId((accepted opened |> caseReceipt).CaseId)
            }

        let! sourceBack = grant owner (id fixture 125) worker sourceGrant true
        acceptedPeople sourceBack
        let! lostTarget = grant owner (id fixture 126) worker targetGrant false
        acceptedPeople lostTarget

        let! blind = get scoped "synthetic-a"
        let link = blind.Outgoing |> List.exactlyOne
        Expect.isNone link.Case "Blind removal cannot recover the target"
        Expect.isNone link.Note "Blind removal cannot recover a note"
        Expect.isTrue link.CanRemove "A source handler can detach a blind relationship"

        let! removed =
            run
                scoped
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

        Expect.isEmpty erasedTarget.Outgoing "Target erasure closes the outgoing relationship"

        let! retained =
            fixture.Store.Read
                {
                    CancellationToken = token
                    Work =
                        fun tx ->
                            task {
                                let! first =
                                    OperationStorage.find
                                        tx.Connection
                                        tx.Cipher
                                        (OperationId.create (id fixture 111) |> Option.get)
                                        token

                                let! second =
                                    OperationStorage.find
                                        tx.Connection
                                        tx.Cipher
                                        (OperationId.create (id fixture 117) |> Option.get)
                                        token

                                return [ Option.get first; Option.get second ]
                            }
                }

        for record in
            retained
            |> Result.defaultWith (fun _ -> failtest "Cannot read retained operations") do
            Expect.isNone record.Inputs "Both active and withdrawn link inputs clear"
            let compartment = record.Root.Structure.Compartments |> List.exactlyOne
            Expect.isTrue compartment.RelationshipCleared "The proof keeps a cleared shape"

            Expect.isNone
                (Compartment.content compartment)
                "Target and note clear with the compartment"

        let! _ = verified database

        let! other =
            Service.executeSubmission
                owner
                (request fixture 127 "LINK-OTHER" 0L "case.open" Map.empty)
                token

        accepted other |> ignore

        let! third =
            run owner 128 "link.add" (OperationBinding.Link(source, None)) (related "LINK-OTHER")

        accepted third |> ignore
        let! beforeSourceErasure = get owner "LINK-OTHER"

        Expect.equal
            beforeSourceErasure.IncomingCount
            1L
            "A second live case has an incoming relationship"

        do! erase "synthetic-a" 121
        let! erasedSource = get owner "LINK-OTHER"

        Expect.equal
            erasedSource.IncomingCount
            0L
            "Source erasure clears the live reverse relationship"

        let! retainedSource =
            fixture.Store.Read
                {
                    CancellationToken = token
                    Work =
                        fun tx ->
                            OperationStorage.find
                                tx.Connection
                                tx.Cipher
                                (OperationId.create (id fixture 128) |> Option.get)
                                token
                }

        let record =
            retainedSource
            |> Result.defaultWith (fun _ -> failtest "Cannot read the retained source operation")
            |> Option.get

        Expect.isNone record.Inputs "Source erasure also clears the accepted link input"

        Expect.isTrue
            (record.Root.Structure.Compartments |> List.exactlyOne |> _.RelationshipCleared)
            "Source erasure leaves the same cleared shape"

        let! _ = verified database
        use! connection = database.OwnerSource.OpenConnectionAsync(token)

        let! coverage =
            DatabaseSession.rows
                connection
                "SELECT cleared_count,(SELECT count(*) FROM claimcore.ledger_link_dependencies) FROM claimcore.ledger_link_coverage ORDER BY operation_sequence"
                [||]
                token

        Expect.equal
            (coverage
             |> List.map (fun row ->
                 DatabaseSession.value<int64> 0 row, DatabaseSession.value<int64> 1 row))
            [ 2L, 0L; 1L, 0L ]
            "Target erasure covers both private additions; source erasure covers its remaining link"

        let mutable refused = false

        try
            let! _ =
                DatabaseSession.execute
                    connection
                    "UPDATE claimcore.ledger_link_coverage SET cleared_count=0"
                    [||]
                    token

            ()
        with :? Npgsql.PostgresException as error ->
            refused <- error.SqlState = "CC003"

        Expect.isTrue refused "The owner cannot rewrite accepted relationship coverage"
        do! enterMaintenance database
        let! expected = checkpoint database
        do! sql database "DELETE FROM claimcore.case_links" [||]

        let! rebuilt =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                expected
                (MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk rebuilt "Rebuild reconstructs structural evidence after either endpoint erasure"
        let! restored = get owner "LINK-OTHER"
        Expect.equal restored.IncomingCount 0L "No reverse relationship is resurrected"
        let! _ = verified database
        return ()
    }

let private serialErasure (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = caseWorker fixture

        let! steward =
            grant
                owner
                (id fixture 140)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople steward

        let openCase number name =
            task {
                let! opened =
                    Service.executeSubmission
                        owner
                        (request fixture number name 0L "case.open" Map.empty)
                        token

                return (accepted opened |> caseReceipt).CaseId
            }

        let race first second =
            task {
                let entered =
                    System.Threading.Tasks.TaskCompletionSource(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                    )

                let resume =
                    System.Threading.Tasks.TaskCompletionSource(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                    )

                let barrier =
                    owner
                    |> Service.mapStore (fun store ->
                        { new IStore with
                            member _.Context query = store.Context query
                            member _.Read query = store.Read query

                            member _.RecordDisclosure(event, ct) =
                                store.RecordDisclosure(event, ct)

                            member _.Write operation =
                                store.Write
                                    { operation with
                                        Work =
                                            fun tx ->
                                                task {
                                                    entered.TrySetResult() |> ignore
                                                    do! resume.Task.WaitAsync(tx.CancellationToken)
                                                    return! operation.Work tx
                                                }
                                    }
                        })

                let one = Service.executeSubmission barrier first token

                try
                    do! entered.Task.WaitAsync(System.TimeSpan.FromSeconds 10.)
                    let two = Service.executeSubmission owner second token
                    resume.TrySetResult() |> ignore
                    let! first = one
                    let! second = two
                    return first, second
                finally
                    resume.TrySetResult() |> ignore
            }

        for index, order in [ 150, "add first"; 155, "erase first"; 160, "unlink first" ] do
            let sourceName = $"SOURCE-{index}"
            let targetName = $"TARGET-{index}"
            let! source = openCase index sourceName
            let! destination = openCase (index + 1) targetName
            let selector = CaseSelector.Id source

            let add =
                Submissions.createRaw
                    "link.add"
                    (id fixture (index + 2))
                    (OperationBinding.Link(selector, None))
                    (Map.ofList
                        [
                            Submissions.input InputTarget.LinkTargetId,
                            SubmissionValue.Text(CaseId.format destination)
                            Submissions.input InputTarget.LinkNote,
                            SubmissionValue.Text "Synthetic race relationship"
                        ])

            let review number =
                task {
                    let target = CaseSelector.Id destination
                    let! current = Service.get owner { Target = target } token
                    let current = succeeded current

                    return
                        Submissions.createRaw
                            "case.erase"
                            (id fixture number)
                            (OperationBinding.Case(
                                target,
                                Revision.value current.Snapshot.Revision,
                                None,
                                current.ErasureReviewToken
                            ))
                            Map.empty
                }

            let reviewMustBeStale (stale: OperationSubmission) (reply: ExecuteReply) =
                task {
                    Expect.equal
                        reply.Outcome
                        (ExecuteResult.Rejected(Refusal.Case CaseRefusal.FamilyChanged))
                        "A relationship change refuses the frozen review"

                    let! current = Service.get owner { Target = CaseSelector.Id destination } token

                    Expect.equal
                        (succeeded current).Snapshot.Revision
                        Revision.first
                        "Case revision stays fixed"

                    let! missing =
                        Service.operationGet owner { OperationId = stale.OperationId } token

                    match missing.Outcome with
                    | Outcome.Succeeded(OperationLookupResult.Pending _ | OperationLookupResult.NotAccepted) ->
                        ()
                    | other -> failtestf "Refused erasure was recorded: %A" other
                }

            match order with
            | "add first" ->
                let! erase = review (index + 3)
                let! added, refused = race add erase
                accepted added |> ignore
                do! reviewMustBeStale erase refused

                let! visible = Service.get owner { Target = selector } token

                Expect.equal
                    ((succeeded visible).RelatedCases.Outgoing |> List.map _.Note)
                    [ NoteText.parse "Synthetic race relationship" |> Result.toOption ]
                    "Stale erasure leaves the winning relationship note intact"

                let! fresh = review (index + 4)
                let! erased = Service.executeSubmission owner fresh token
                let! retried = Service.executeSubmission owner fresh token

                match retried.Outcome with
                | ExecuteResult.AlreadyAccepted receipt ->
                    Expect.equal
                        receipt.OperationId
                        (accepted erased).OperationId
                        "Exact retry settles after clearing"
                | other -> failtestf "Expected accepted retry: %A" other
            | "erase first" ->
                let! erase = review (index + 3)
                let! erased, refused = race erase add
                accepted erased |> ignore

                Expect.equal
                    refused.Outcome
                    (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                    "An erased target is unavailable to a late add"
            | _ ->
                let! visible = Service.executeSubmission owner add token
                accepted visible |> ignore

                let remove =
                    Submissions.createRaw
                        "link.remove"
                        (id fixture (index + 4))
                        (OperationBinding.Link(selector, Some add.OperationId))
                        Map.empty

                let! erase = review (index + 3)
                let! removed, refused = race remove erase
                accepted removed |> ignore
                do! reviewMustBeStale erase refused

                let! fresh = review (index + 6)
                let! erased = Service.executeSubmission owner fresh token
                accepted erased |> ignore

            let! source = Service.get owner { Target = selector } token

            Expect.isEmpty
                (succeeded source).RelatedCases.Outgoing
                "Every settled order clears the live relation"

        let! _ = verified database
        return ()
    }

let private scopedErasureReview (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = caseWorker fixture

        let requester =
            PrincipalKey.human "https://issuer.example/" "scoped-erasure-review"
            |> Result.defaultWith (string >> failtest)

        let! registration =
            personSubmission
                fixture.Store
                (id fixture 300)
                requester
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! registered = Service.executeSubmission owner registration token
        acceptedPeople registered

        let openCase number name =
            task {
                let! result =
                    Service.executeSubmission
                        owner
                        (request fixture number name 0L "case.open" Map.empty)
                        token

                return (accepted result |> caseReceipt).CaseId
            }

        let! source = openCase 301 "HIDDEN-SOURCE"
        let! target = openCase 302 "SCOPED-TARGET"
        let! other = openCase 303 "OTHER-TARGET"

        let grantRole number person role scope supplied =
            task {
                let! result =
                    grant owner (id fixture number) person { Role = role; Scope = scope } supplied

                acceptedPeople result
            }

        do! grantRole 304 requester Role.DataSteward (GrantTarget.CaseId target) true
        do! grantRole 305 requester Role.DataSteward (GrantTarget.CaseId other) true
        do! grantRole 306 fixture.Owner Role.DataSteward GrantTarget.Installation true
        let steward = core fixture requester

        let read actor caseId =
            task {
                let! result = Service.get actor { Target = CaseSelector.Id caseId } token
                return succeeded result
            }

        let command number caseId reviewed =
            Submissions.createRaw
                "case.erase"
                (id fixture number)
                (OperationBinding.Case(CaseSelector.Id caseId, 1L, None, Some reviewed))
                Map.empty

        let stale actor submission =
            task {
                let! refusal = Service.executeSubmission actor submission token

                Expect.equal
                    refusal.Outcome
                    (ExecuteResult.Rejected(Refusal.Case CaseRefusal.FamilyChanged))
                    "A stale or unusable token has the same generic refusal"
            }

        let! before = read steward target
        let! secondRead = read steward target
        let review = before.ErasureReviewToken.Value

        Expect.notEqual
            review
            secondRead.ErasureReviewToken.Value
            "Unchanged scope has independently randomized tokens"

        do! stale owner (command 307 target review)
        do! stale steward (command 308 other review)
        do! stale steward (command 309 target "malformed-review")
        let altered = (if review[0] = 'A' then "B" else "A") + review[1..]
        do! stale steward (command 310 target altered)

        let! linked =
            Service.executeSubmission
                owner
                (Submissions.createRaw
                    "link.add"
                    (id fixture 311)
                    (OperationBinding.Link(CaseSelector.Id source, None))
                    (Map.ofList
                        [
                            Submissions.input InputTarget.LinkTargetId,
                            SubmissionValue.Text(CaseId.format target)
                            Submissions.input InputTarget.LinkNote,
                            SubmissionValue.Text "Hidden synthetic relationship"
                        ]))
                token

        accepted linked |> ignore

        let! after = read steward target

        Expect.equal
            after.Snapshot.Revision
            before.Snapshot.Revision
            "Inbound link leaves the target revision unchanged"

        Expect.equal
            after.FamilySequence
            before.FamilySequence
            "Inbound link leaves the target family sequence unchanged"

        Expect.isEmpty after.RelatedCases.Incoming "Scoped steward cannot read the source"
        do! stale steward (command 312 target review)

        let! sourceBefore = read owner source

        Expect.hasLength
            sourceBefore.RelatedCases.Outgoing
            1
            "Stale erasure preserves the winning link"

        Expect.equal
            (sourceBefore.RelatedCases.Outgoing.Head.Note |> Option.map NoteText.format)
            (Some "Hidden synthetic relationship")
            "Stale erasure preserves its private note"

        let! peerErased =
            Service.executeSubmission
                owner
                (command 313 source sourceBefore.ErasureReviewToken.Value)
                token

        accepted peerErased |> ignore
        do! stale steward (command 314 target after.ErasureReviewToken.Value)

        let! fresh = read steward target
        let reviewed = command 315 target fresh.ErasureReviewToken.Value
        do! grantRole 316 requester Role.DataSteward (GrantTarget.CaseId target) false
        let! revoked = Service.executeSubmission steward reviewed token

        match revoked.Outcome with
        | ExecuteResult.Rejected(Refusal.Access _) -> ()
        | other -> failtestf "Expected revoked authority to refuse: %A" other

        do! grantRole 317 requester Role.DataSteward (GrantTarget.CaseId target) true
        let! erased = Service.executeSubmission steward reviewed token
        let original = accepted erased
        let! foreignRetry = Service.executeSubmission owner reviewed token

        Expect.equal
            foreignRetry.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Exact settlement still belongs to the original erasure requester"

        let! beforeRotation = read steward other
        let oldReview = command 318 other beforeRotation.ErasureReviewToken.Value

        do! enterMaintenance database

        let handle =
            MaintenanceHandle.create 1L (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
            |> Option.get

        use administration =
            new ClaimCore.Application.Administration.Administration(
                PostgresFixture.ownerConnectionString database,
                "test",
                database.MasterKey
            )

        let! _ = administration.RotateEncryptionGeneration(handle, token)

        let! _ =
            RegisterMaintenanceStore.change
                database.OwnerSource
                database.MasterKey
                (MaintenanceChange.Resume handle)
                token

        let! reopened =
            Runtime.openConnection
                (PostgresFixture.runtimeConnectionString database)
                database.MasterKey
                token

        use reopened =
            reopened
            |> Result.defaultWith (fun error -> failtestf "Reopen after rotation: %A" error)

        let currentSteward = Service.mapStore (fun _ -> Runtime.store reopened) steward
        do! stale currentSteward oldReview

        let! exactRetry = Service.executeSubmission currentSteward reviewed token

        match exactRetry.Outcome with
        | ExecuteResult.AlreadyAccepted receipt ->
            Expect.equal
                receipt.OperationId
                original.OperationId
                "Accepted retry survives generation rotation"
        | outcome -> failtestf "Expected the original acceptance: %A" outcome

        let! changed =
            Service.executeSubmission
                currentSteward
                (command 315 target secondRead.ErasureReviewToken.Value)
                token

        Expect.equal
            changed.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "A different token cannot settle the same operation"

        let! _ = verified database
        return ()
    }

let private boundedGroups (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = caseWorker fixture
        let hub = "FIELD-LINK-HUB"
        let limb index = $"FIELD-LINK-{index}"

        let openCase number reference =
            task {
                let! result =
                    Service.executeSubmission
                        owner
                        (request fixture number reference 0L "case.open" Map.empty)
                        token

                accepted result |> ignore
            }

        let caseId name =
            match caseTarget fixture name with
            | CaseSelector.Id id -> id
            | CaseSelector.Reference _ -> failtest "Case fixture is missing"

        let link number source target =
            task {
                let! result =
                    Service.executeSubmission
                        owner
                        (Submissions.createRaw
                            "link.add"
                            (id fixture number)
                            (OperationBinding.Link(caseTarget fixture source, None))
                            (Map.ofList
                                [
                                    Submissions.input InputTarget.LinkTargetId,
                                    SubmissionValue.Text(CaseId.format (caseId target))
                                ]))
                        token

                accepted result |> ignore
            }

        do! openCase 100 hub

        for index in 1..21 do
            do! openCase (100 + index) (limb index)
            do! link (140 + index) (limb index) hub

            if index <= 20 then
                do! link (180 + index) hub (limb index)

        let read reader =
            Service.get reader { Target = caseTarget fixture hub } token

        let! record = read owner
        let record = succeeded record

        let incoming (record: CaseReadModel) =
            record.RelatedCases.Incoming |> List.choose _.Case |> List.map _.CaseId

        Expect.equal
            (incoming record)
            ([ 21..-1..2 ] |> List.map (limb >> caseId))
            "The 20 newest sources are shown"

        Expect.equal
            record.RelatedCases.IncomingCount
            21L
            "One readable source is outside the display bound"

        let view = ClaimCore.Presentation.CaseScreen.view record

        let tables =
            view.Regions
            |> List.choose (fun region ->
                match region.Role, region.Content with
                | ClaimCore.Presentation.RegionRole.RelatedCases,
                  ClaimCore.Presentation.RegionContent.Table table -> Some table
                | _ -> None)

        let table = List.exactlyOne tables
        Expect.equal table.Rows.Length 40 "Both directions fit one bounded table"

        Expect.equal
            (table.Groups |> List.map (fun group -> group.Count, group.RowKeys.Length))
            [ 20L, 20; 21L, 20 ]
            "Groups carry independent totals and displayed row membership"

        let principal =
            PrincipalKey.human "https://issuer.example/" "field-scoped-link-reader"
            |> Result.defaultWith (string >> failtest)

        let! input =
            personSubmission
                fixture.Store
                (id fixture 220)
                principal
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! created = Service.executeSubmission owner input token
        acceptedPeople created

        let grantAt number target operation =
            task {
                let! changed =
                    grant
                        owner
                        (id fixture number)
                        principal
                        {
                            Role = Role.CaseReader
                            Scope = GrantTarget.CaseId(caseId target)
                        }
                        operation

                acceptedPeople changed
            }

        do! grantAt 221 hub true
        do! grantAt 222 (limb 20) true
        do! grantAt 223 (limb 21) true
        let reader = core fixture principal
        let! before = read reader
        let before = succeeded before

        Expect.equal
            (incoming before, before.RelatedCases.IncomingCount)
            ([ caseId (limb 21); caseId (limb 20) ], 2L)
            "Selection uses current scoped access"

        do! grantAt 224 (limb 21) false
        let! after = read reader
        let after = succeeded after

        Expect.equal
            (incoming after, after.RelatedCases.IncomingCount)
            ([ caseId (limb 20) ], 1L)
            "Revocation tightens both count and visible membership"

        let! granted =
            grant
                owner
                (id fixture 225)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted

        let assertStale (reviewed: OperationSubmission) =
            task {
                let! result = Service.executeSubmission owner reviewed token

                Expect.equal
                    result.Outcome
                    (ExecuteResult.Rejected(Refusal.Case CaseRefusal.FamilyChanged))
                    "Changes outside the displayed selection invalidate the review"
            }

        let beforeDetach = request fixture 226 hub 1L "case.erase" Map.empty

        let! detached =
            Service.executeSubmission
                owner
                (Submissions.createRaw
                    "link.remove"
                    (id fixture 227)
                    (OperationBinding.Link(caseTarget fixture (limb 1), Some(id fixture 141)))
                    Map.empty)
                token

        accepted detached |> ignore
        do! assertStale beforeDetach

        let beforeReplacement = request fixture 228 hub 1L "case.erase" Map.empty

        let! peerErased =
            Service.executeSubmission
                owner
                (request fixture 229 (limb 21) 1L "case.erase" Map.empty)
                token

        accepted peerErased |> ignore
        do! openCase 230 (limb 22)
        do! link 231 (limb 22) hub
        do! assertStale beforeReplacement

        let! current = read owner

        Expect.equal
            (succeeded current).Snapshot.Revision
            record.Snapshot.Revision
            "Replacing an incoming dependency leaves the hub revision unchanged"

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testList
        "related-case links"
        [
            testCaseAsync "use current read access in both directions" (run links)
            testCaseAsync
                "scope token protects hidden inbound relationships and current authority"
                (run scopedErasureReview)
            testCaseAsync "group both directions at the display bound" (run boundedGroups)
            testCaseAsync
                "serialize both add orders and unlink against target erasure"
                (run serialErasure)
        ]
