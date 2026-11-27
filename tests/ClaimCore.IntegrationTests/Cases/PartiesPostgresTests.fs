module ClaimCore.IntegrationTests.PartiesPostgresTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected read, got %A" other

let private timeline owner target =
    Service.timeline
        owner
        {
            Target = target
            Filter = None
            AfterCursor = None
            Limit = 20
        }
        token

let private integration (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let! owner = populated database

        for operation, role in
            [ 668, Role.PaymentRecorder; 669, Role.DataSteward; 670, Role.Auditor ] do
            let! grant =
                grant
                    owner
                    (id fixture operation)
                    fixture.Owner
                    {
                        Role = role
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople grant

        let target = CaseSelector.Id(CaseFixtures.caseId (id fixture 671))

        let opening =
            Submissions.createRaw
                "case.open"
                (id fixture 671)
                (OperationBinding.CaseNew 0L)
                (Map.ofList
                    [
                        TaskCollectionId.input TaskCollectionId.Parties,
                        SubmissionValue.Rows
                            [
                                for name, role, details in
                                    [
                                        "Anna Liepa", "CLAIMANT", "+371 20000000"
                                        "Example Insurance AS", "INSURER", "First office"
                                        "Jānis Bērziņš", "CLAIMANT", "Second office"
                                    ] do
                                    yield
                                        Map.ofList
                                            [
                                                Submissions.input InputTarget.PartyRole,
                                                SubmissionValue.Text role
                                                Submissions.input InputTarget.PartyName,
                                                SubmissionValue.Text name
                                                Submissions.input InputTarget.PartyDetails,
                                                SubmissionValue.Text details
                                            ]
                            ]
                    ])

        let! first = Service.executeSubmission owner opening token
        accepted first |> ignore
        let! repeated = Service.executeSubmission owner opening token

        Expect.isTrue
            (match repeated.Outcome with
             | ExecuteResult.AlreadyAccepted _ -> true
             | _ -> false)
            "Repeating the operation returns its accepted receipt"

        let! subject = Service.get owner { Target = target } token
        let subject = read subject

        Expect.equal
            (subject.Parties
             |> List.map (fun party -> party.Content.Name |> Option.map PartyName.format))
            [ Some "Anna Liepa"; Some "Example Insurance AS"; Some "Jānis Bērziņš" ]
            "Replaying the operation creates no duplicate rows and retains creation order"

        let party = List.head subject.Parties

        let execute operation task revision values =
            Service.executeSubmission
                owner
                (Submissions.createRaw
                    task
                    (id fixture operation)
                    (OperationBinding.Party(target, Some(PartyId.value party.PartyId, revision)))
                    values)
                token

        let! edited =
            execute
                672
                "party.update"
                1L
                (Map.ofList
                    [
                        Submissions.input InputTarget.PartyDetails,
                        SubmissionValue.Text "+371 20000000\nSecond floor"
                    ])

        accepted edited |> ignore

        let revisedParty task operation revision =
            Submissions.createRaw
                task
                (id fixture operation)
                (OperationBinding.Party(target, Some(PartyId.value party.PartyId, revision)))
                Map.empty

        let! currentReview =
            Service.previewSubmission owner (revisedParty "party.remove" 685 2L) token

        match currentReview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | other -> failtestf "Expected current party review: %A" other

        let checkStale =
            function
            | Refusal.RevisionConflict stale ->
                Expect.equal (Revision.value stale.ActualRevision) 2L "The actual party revision"

                Expect.equal
                    stale.Changes
                    (Some ConflictChanges.Withheld)
                    "Party history has no revision range"

                Expect.equal
                    stale.ReadCaseId
                    (Some subject.Snapshot.CaseId)
                    "The stable authorized case ID"
            | other -> failtestf "Expected stale party conflict: %A" other

        let! staleReview =
            Service.previewSubmission owner (revisedParty "party.remove" 685 1L) token

        match staleReview.Outcome with
        | Outcome.Rejected refusal -> checkStale refusal
        | other -> failtestf "Expected stale party review: %A" other

        let! staleWrite =
            Service.executeSubmission owner (revisedParty "party.remove" 685 1L) token

        match staleWrite.Outcome with
        | ExecuteResult.Rejected refusal -> checkStale refusal
        | other -> failtestf "Expected stale party execution: %A" other

        let! history = timeline owner target

        let existed =
            (read history).Items
            |> List.exists (fun entry ->
                match entry.Changes with
                | TimelineChanges.Disclosed changes ->
                    changes.Items
                    |> List.exists (function
                        | TimelineChange.Party(id, change) when id = party.PartyId ->
                            (change.Before
                             |> Option.bind _.Details
                             |> Option.map PartyDetails.format)
                                =
                                Some "+371 20000000"
                            && (change.After
                                |> Option.bind _.Details
                                |> Option.map PartyDetails.format)
                                =
                                Some "+371 20000000\nSecond floor"
                        | _ -> false)
                | _ -> false)

        Expect.isTrue existed "Full history carries the old and new contact details"

        let query =
            {
                CaseListRequest.Filter =
                    { CaseListFilter.empty with
                        Text = Some "20000000"
                    }
                Page = { AfterCursor = None; Limit = 20 }
            }

        let! page = Service.list owner query token
        let page = read page

        Expect.equal
            (page.Items |> List.map (fun entry -> CaseReference.format entry.Reference))
            [ CaseReference.format subject.Snapshot.Reference ]
            "Search finds a contact detail"

        let csv =
            ClaimCore.Wire.Export.CaseCsv.csvRows page.Basis page.Items
            |> Encoding.UTF8.GetString

        Expect.stringContains
            csv
            "Anna Liepa; Jānis Bērziņš"
            "CSV joins current claimants in creation order"

        let payment fields =
            Map.ofList
                [
                    Submissions.field BusinessField.PaymentDate, SubmissionValue.Text "2026-08-20"
                    Submissions.field BusinessField.PaidAmount, SubmissionValue.Text "25"
                    Submissions.field BusinessField.PaidCurrency, SubmissionValue.Text "EUR"
                    Submissions.input InputTarget.PaymentPayee,
                    SubmissionValue.Text(PartyId.format party.PartyId)
                ]
            |> Map.fold
                (fun next key value ->
                    if Map.containsKey key next then
                        next
                    else
                        Map.add key value next)
                fields

        let changePayment operation task selection inputs =
            Service.executeSubmission
                owner
                (Submissions.createRaw task (id fixture operation) selection inputs)
                token

        let! otherOpened =
            Service.executeSubmission
                owner
                (request fixture 679 "PARTY-OTHER" 0L "case.open" Map.empty)
                token

        let other = CaseSelector.Id((accepted otherOpened |> caseReceipt).CaseId)

        let! foreignPayee =
            changePayment
                680
                "case.recordPayment"
                (OperationBinding.Case(other, 1L, None, None))
                (payment Map.empty)

        match foreignPayee.Outcome with
        | ExecuteResult.Rejected(Refusal.InvalidInput issues) ->
            Expect.equal
                (InputIssues.toList issues |> List.map _.Violation)
                [ Violation.PayeeUnavailable ]
                "A party on another case cannot be a payee"
        | other -> failtestf "Expected a cross-case payee refusal: %A" other

        let! paymentReceipt =
            changePayment
                676
                "case.recordPayment"
                (OperationBinding.Case(target, 1L, None, None))
                (payment Map.empty)

        accepted paymentReceipt |> ignore

        let! removed = execute 673 "party.remove" 2L Map.empty
        accepted removed |> ignore
        let! current = Service.get owner { Target = target } token

        Expect.equal
            ((read current).Parties |> List.map _.PartyId)
            (subject.Parties |> List.tail |> List.map _.PartyId)
            "Removal hides only the targeted party"

        let lookup cursor =
            Service.parties
                owner
                {
                    Target = target
                    AfterCursor = cursor
                    Limit = 1
                }
                token

        let! firstChoice = lookup None
        let firstChoice = read firstChoice
        Expect.hasLength firstChoice.Items 1 "The lookup honors the requested page size"
        let! secondChoice = lookup firstChoice.NextCursor
        let secondChoice = read secondChoice
        Expect.isNone secondChoice.NextCursor "Final page has no continuation"

        Expect.equal
            (firstChoice.Items @ secondChoice.Items
             |> List.map (fun choice -> choice.PartyId, choice.Name, choice.Role)
             |> Set.ofList)
            (subject.Parties
             |> List.tail
             |> List.map (fun party -> party.PartyId, party.Content.Name, party.Content.Role)
             |> Set.ofList)
            "Payee choices contain just the remaining parties and present their names and roles"

        let currentPayment = read current
        let removedPayee = currentPayment.PaymentPayees |> List.exactlyOne
        Expect.isTrue removedPayee.Removed "Existing payments retain the removed party marker"

        Expect.equal
            (removedPayee.Name |> Option.map PartyName.format)
            (Some "Anna Liepa")
            "The last recorded name stays available"

        let! rejected =
            changePayment
                677
                "case.recordPayment"
                (OperationBinding.Case(
                    target,
                    Revision.value currentPayment.Snapshot.Revision,
                    None,
                    None
                ))
                (payment Map.empty)

        Expect.equal
            (match rejected.Outcome with
             | ExecuteResult.Rejected(Refusal.InvalidInput issues) ->
                 InputIssues.toList issues |> List.map _.Violation
             | other -> failtestf "Expected payee refusal: %A" other)
            [ Violation.PayeeUnavailable ]
            "New entries cannot use a removed payee"

        let payee = subject.Parties[1].PartyId

        let replacement =
            Map.ofList
                [
                    Submissions.field BusinessField.PaidCurrency, SubmissionValue.Text "USD"
                    Submissions.input InputTarget.PaymentDirection, SubmissionValue.Text "RETURNED"
                    Submissions.input InputTarget.PaymentReference,
                    SubmissionValue.Text "BANK-2026-1"
                    Submissions.input InputTarget.PaymentPayee,
                    SubmissionValue.Text(PartyId.format payee)
                ]

        let! corrected =
            changePayment
                678
                "case.correctPayment"
                (OperationBinding.Case(
                    target,
                    Revision.value currentPayment.Snapshot.Revision,
                    Some(id fixture 676),
                    None
                ))
                (payment replacement)

        accepted corrected |> ignore

        let! correctedCase = Service.get owner { Target = target } token
        let currentEntry = (read correctedCase).LatestPayments |> List.exactlyOne

        Expect.equal
            (PaymentEntry.direction currentEntry)
            PaymentDirection.Returned
            "Direction corrected"

        Expect.equal (PaymentEntry.money currentEntry |> Money.currency) "USD" "Currency corrected"

        Expect.equal
            (PaymentEntry.reference currentEntry |> Option.map Identifier.format)
            (Some "BANK-2026-1")
            "Evidence reference corrected"

        Expect.equal
            (PaymentEntry.payee currentEntry)
            (Some payee)
            "Payee corrected in the same operation"

        let afterRemoval = read correctedCase

        let retainedQuery =
            { query with
                Filter =
                    { query.Filter with
                        Text = Some "Second office"
                    }
            }

        let! beforeErasure = Service.list owner retainedQuery token

        Expect.equal
            (read beforeErasure).Items.Length
            1
            "The remaining party is indexed before erasure"

        let erase =
            Submissions.createRaw
                "case.erase"
                (id fixture 674)
                (OperationBinding.Case(
                    target,
                    Revision.value afterRemoval.Snapshot.Revision,
                    None,
                    Some(Sequence.value afterRemoval.FamilySequence)
                ))
                Map.empty

        let! erased = Service.executeSubmission owner erase token
        accepted erased |> ignore
        let! afterErasure = Service.list owner retainedQuery token
        Expect.isEmpty (read afterErasure).Items "Case erasure clears the current search hit"
        let! erasedCase = Service.get owner { Target = target } token
        Expect.isEmpty (read erasedCase).Parties "Erased case has no current parties"

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testCaseAsync
        "PostgreSQL case parties replay, update, search, export and remove"
        (run integration)
