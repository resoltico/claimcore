module ClaimCore.IntegrationTests.StewardshipReasonConsumerTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private required =
    function
    | Ok value -> value
    | Error issue -> failtestf "%A" issue

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "%A" other

let private encoded codec value =
    Codec.encode codec value
    |> StrictJson.read Limits.MaximumResponseBytes
    |> required

let rec private anyString predicate =
    function
    | JsonValue.String value -> predicate value
    | JsonValue.Array values -> List.exists (anyString predicate) values
    | JsonValue.Object values -> List.exists (snd >> anyString predicate) values
    | _ -> false

let private contains expected = anyString ((=) expected)

let private scenario database =
    task {
        let! owner = populated database
        let fixture = database.Fixture

        for number, role in [ 170, Role.DataSteward; 171, Role.Auditor ] do
            let! granted =
                grant
                    owner
                    (id fixture number)
                    fixture.Owner
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople granted

        let principal =
            PrincipalKey.human "https://issuer.example/" "reason-summary-reader" |> required

        let! registration =
            personSubmission
                fixture.Store
                (id fixture 172)
                principal
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! registered = Service.executeSubmission owner registration token
        acceptedPeople registered

        let! granted =
            grant
                owner
                (id fixture 173)
                principal
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted
        let reader = core fixture principal
        let target = CaseSelector.Reference populatedReference

        let reasons =
            [ "Void A 1.0000 العربية <tag>"; "Reinstate B 2.0000 日本語"; "Void C 3.0000 Ā" ]

        let failures = ResizeArray<string>()

        let check label expected wire =
            if
                not (contains expected wire)
                || (reasons |> List.exists (fun other -> other <> expected && contains other wire))
            then
                failures.Add label

        let hidden label wire =
            Expect.isFalse
                (reasons
                 |> List.exists (fun reason ->
                     anyString (fun text -> text.Contains(reason, StringComparison.Ordinal)) wire))
                label

        let receipt label expected reply =
            check
                (label + " DATA")
                expected
                (encoded Api.operationExecute.Output (RepresentationReply.Data reply))

            check
                (label + " HUMAN")
                expected
                (encoded
                    Api.operationExecute.Output
                    (RepresentationReply.Human(HumanReplies.execute reply)))

        let execute number revision taskName reason =
            task {
                let values =
                    match reason with
                    | Some reason ->
                        Map.ofList
                            [ Submissions.input InputTarget.Reason, SubmissionValue.Text reason ]
                    | None -> Submissions.fields [ BusinessField.PolicyNumber, "Later claimant" ]

                let! reply =
                    Service.executeSubmission
                        owner
                        (request fixture number "synthetic-a" revision taskName values)
                        token

                accepted reply |> ignore
                return reply
            }

        let! voidA = execute 180 1L "case.void" (Some reasons[0])
        receipt "accepted void A" reasons[0] voidA
        let! current = Service.get owner { Target = target } token

        Expect.isTrue
            (contains reasons[0] (encoded Api.caseGet.Output (RepresentationReply.Data current)))
            "Positive current-case DATA control retains exact stored void reason"

        Expect.isTrue
            (contains
                reasons[0]
                (encoded
                    Api.caseGet.Output
                    (RepresentationReply.Human(HumanReplies.read HumanCaseDocument.ofCore current))))
            "Positive current-case HUMAN control"

        let! reinstateB = execute 181 2L "case.reinstate" (Some reasons[1])
        receipt "accepted reinstate B" reasons[1] reinstateB
        let! amended = execute 182 3L "case.amendRegistration" None

        hidden
            "Subsequent DATA update receipt has no preceding reason"
            (encoded Api.operationExecute.Output (RepresentationReply.Data amended))

        hidden
            "Subsequent update receipt does not borrow a preceding reason"
            (encoded
                Api.operationExecute.Output
                (RepresentationReply.Human(HumanReplies.execute amended)))

        let! voidC = execute 183 4L "case.void" (Some reasons[2])
        receipt "accepted later void C" reasons[2] voidC

        let! timeline =
            Service.timeline
                owner
                {
                    Target = target
                    Filter = None
                    AfterCursor = None
                    Limit = 20
                }
                token

        let page = read timeline
        Expect.equal page.Disclosure HistoryDisclosure.Full "Actual full historical disclosure"

        for number, reason in [ 180, reasons[0]; 181, reasons[1]; 183, reasons[2] ] do
            let! lookup = Service.operationGet owner { OperationId = id fixture number } token

            check
                (sprintf "historical lookup %d DATA" number)
                reason
                (encoded Api.operationGet.Output (RepresentationReply.Data lookup))

            check
                (sprintf "historical lookup %d HUMAN" number)
                reason
                (encoded
                    Api.operationGet.Output
                    (RepresentationReply.Human(HumanReplies.lookup lookup)))

            let item =
                page.Items
                |> List.find (fun item -> OperationId.value item.OperationId = id fixture number)

            match item.Changes with
            | TimelineChanges.Disclosed changes ->
                let rows =
                    changes.Items
                    |> List.collect ClaimCore.Wire.Export.TimelineCsv.scalarDifferences

                Expect.contains
                    rows
                    ("reason", "", reason)
                    "CSV keeps the accepted historical reason verbatim"
            | other -> failtestf "Expected full changes: %A" other

            let isolated = Outcome.Succeeded { page with Items = [ item ] }

            check
                (sprintf "historical timeline %d DATA" number)
                reason
                (encoded Api.caseTimeline.Output (RepresentationReply.Data isolated))

            check
                (sprintf "historical timeline %d HUMAN" number)
                reason
                (encoded
                    Api.caseTimeline.Output
                    (RepresentationReply.Human(HumanReplies.read TimelineScreen.document isolated)))

        for number in [ 180; 181 ] do
            let! lookup = Service.operationGet reader { OperationId = id fixture number } token

            match read lookup.Outcome with
            | OperationLookupResult.Accepted(receipt, _) ->
                Expect.equal
                    (caseReceipt receipt).Snapshot
                    SnapshotDisclosure.Withheld
                    "Superseded receipt still needs current full-history permission"
            | other -> failtestf "%A" other

            hidden
                "Withheld DATA receipt"
                (encoded Api.operationGet.Output (RepresentationReply.Data lookup))

            hidden
                "Withheld HUMAN receipt"
                (encoded
                    Api.operationGet.Output
                    (RepresentationReply.Human(HumanReplies.lookup lookup)))

        let! summary =
            Service.timeline
                reader
                {
                    Target = target
                    Filter = None
                    AfterCursor = None
                    Limit = 20
                }
                token

        Expect.equal
            (read summary).Disclosure
            HistoryDisclosure.Summary
            "Summary permission control"

        for number in [ 180; 181; 183 ] do
            Expect.isTrue
                ((read summary).Items
                 |> List.exists (fun item -> OperationId.value item.OperationId = id fixture number))
                "Summary retains the known operation metadata"

        hidden
            "SUMMARY DATA history"
            (encoded Api.caseTimeline.Output (RepresentationReply.Data summary))

        hidden
            "SUMMARY HUMAN history"
            (encoded
                Api.caseTimeline.Output
                (RepresentationReply.Human(HumanReplies.read TimelineScreen.document summary)))

        let! revoked =
            grant
                owner
                (id fixture 184)
                principal
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                false

        acceptedPeople revoked
        let! unavailable = Service.operationGet reader { OperationId = id fixture 180 } token

        hidden
            "Revoked DATA lookup"
            (encoded Api.operationGet.Output (RepresentationReply.Data unavailable))

        hidden
            "Revoked HUMAN lookup"
            (encoded
                Api.operationGet.Output
                (RepresentationReply.Human(HumanReplies.lookup unavailable)))

        let! erased =
            Service.executeSubmission
                owner
                (request fixture 190 "synthetic-a" 5L "case.erase" Map.empty)
                token

        accepted erased |> ignore

        for number in [ 180; 181; 183 ] do
            let! lookup = Service.operationGet owner { OperationId = id fixture number } token

            match read lookup.Outcome with
            | OperationLookupResult.Accepted(receipt, _) ->
                Expect.equal
                    (caseReceipt receipt).Snapshot
                    SnapshotDisclosure.Erased
                    "Erasure control"
            | other -> failtestf "%A" other

            hidden
                "Erased DATA receipt"
                (encoded Api.operationGet.Output (RepresentationReply.Data lookup))

            hidden
                "Erased HUMAN receipt"
                (encoded
                    Api.operationGet.Output
                    (RepresentationReply.Human(HumanReplies.lookup lookup)))

        let! erasedHistory =
            Service.timeline
                owner
                {
                    Target = target
                    Filter = None
                    AfterCursor = None
                    Limit = 20
                }
                token

        Expect.equal
            (read erasedHistory).Disclosure
            HistoryDisclosure.Erased
            "Erased history remains a successful, explicitly erased page"

        for number in [ 180; 181; 183 ] do
            Expect.isTrue
                ((read erasedHistory).Items
                 |> List.exists (fun item -> OperationId.value item.OperationId = id fixture number))
                "Erased history retains accepted operation metadata"

        hidden
            "Erased DATA history"
            (encoded Api.caseTimeline.Output (RepresentationReply.Data erasedHistory))

        hidden
            "Erased HUMAN history"
            (encoded
                Api.caseTimeline.Output
                (RepresentationReply.Human(HumanReplies.read TimelineScreen.document erasedHistory)))

        let! _ = verified database

        Expect.equal
            (List.ofSeq failures)
            []
            "Each accepted historical consumer must retain only its own exact reason"
    }

[<Tests>]
let tests =
    testCaseAsync
        "accepted stewardship reasons reach real receipt and history consumers with masking and erasure"
        (run scenario)
