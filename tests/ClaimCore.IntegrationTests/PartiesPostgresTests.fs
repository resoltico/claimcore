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

        for operation, role in [ 669, Role.DataSteward; 670, Role.Auditor ] do
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

        let target = CaseSelector.Reference "PARTY-READ"

        let opening =
            Submissions.createRaw
                "case.open"
                (id fixture 671)
                (OperationBinding.CaseNew 0L)
                (Map.ofList
                    [
                        Submissions.field BusinessField.CaseReference,
                        SubmissionValue.Text "PARTY-READ"
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
            [ "PARTY-READ" ]
            "Search finds a contact detail"

        let csv =
            ClaimCore.Wire.Export.CaseCsv.csvRows page.Basis page.Items
            |> Encoding.UTF8.GetString

        Expect.stringContains
            csv
            "Anna Liepa; Jānis Bērziņš"
            "CSV joins current claimants in creation order"

        let! removed = execute 673 "party.remove" 2L Map.empty
        accepted removed |> ignore
        let! current = Service.get owner { Target = target } token

        Expect.equal
            ((read current).Parties |> List.map _.PartyId)
            (subject.Parties |> List.tail |> List.map _.PartyId)
            "Removal hides only the targeted party"

        let afterRemoval = read current

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
