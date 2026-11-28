module ClaimCore.IntegrationTests.SummaryDisclosureTests

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private summaries () =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            let fixture = database.Fixture
            let! owner = caseWorker fixture |> Async.AwaitTask

            let parties =
                [ "Claimant Example", "CLAIMANT"; "Insurer Example", "INSURER" ]
                |> List.map (fun (name, role) ->
                    Map.ofList
                        [
                            Submissions.input InputTarget.PartyName, SubmissionValue.Text name
                            Submissions.input InputTarget.PartyRole, SubmissionValue.Text role
                        ])
                |> SubmissionValue.Rows

            for number, reference in [ 100, "VISIBLE"; 101, "PRIVATE" ] do
                let fields =
                    Submissions.fields (RegistrationInput.values DomainInputs.registration)
                    |> Map.add (TaskCollectionId.input TaskCollectionId.Parties) parties

                let! result =
                    Service.executeSubmission
                        owner
                        (request fixture number reference 0L "case.open" fields)
                        token
                    |> Async.AwaitTask

                accepted result |> ignore

            let! decided =
                Service.executeSubmission
                    owner
                    (((request
                        (database.Fixture)
                        (102)
                        ("VISIBLE")
                        (1L)
                        "case.decide"
                        (ClaimCore.TestSupport.Submissions.fields (
                            DecisionInput.values (DomainInputs.decision)
                        )))))
                    token
                |> Async.AwaitTask

            accepted decided |> ignore

            let reader =
                PrincipalKey.human "https://issuer.example/" "summary-reader"
                |> Result.defaultWith (fun _ -> failtest "Valid principal")

            let! registered =
                (task {
                    let! submission =
                        ClaimCore.TestSupport.StoreFixtures.personSubmission
                            (database.Fixture.Store)
                            ((id database.Fixture 200))
                            (reader)
                            (None)
                            "people.register"
                            (ClaimCore.TestSupport.Submissions.profile (
                                ClaimCore.TestSupport.DomainInputs.actorProfile
                            ))

                    return! Service.executeSubmission (owner) submission (token)
                })
                |> Async.AwaitTask

            acceptedPeople registered

            let! granted =
                grant
                    owner
                    (id database.Fixture 201)
                    reader
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.CaseId(CaseFixtures.caseId (id fixture 100))
                    }
                    true
                |> Async.AwaitTask

            acceptedPeople granted
            let caller = core fixture reader

            let! result =
                Service.list caller (Inputs.caseListPage { AfterCursor = None; Limit = 50 }) token
                |> Async.AwaitTask

            let rows =
                match result with
                | Outcome.Succeeded page -> page.Items
                | _ -> failtest "Scoped reader can list assigned cases"

            Expect.equal
                (rows |> List.map _.CaseId)
                [ CaseFixtures.caseId (id fixture 100) ]
                "Summary rows preserve case-scoped disclosure"

            for row in rows do
                let! current =
                    Service.get
                        caller
                        {
                            Target = CaseSelector.Reference(CaseReference.format row.Reference)
                        }
                        token
                    |> Async.AwaitTask

                let current =
                    match current with
                    | Outcome.Succeeded value -> value
                    | _ -> failtest "Every disclosed row must be readable by the same actor"

                Expect.equal row.Revision current.Snapshot.Revision "Revision disclosure agrees"
                let summary = CaseState.fields row.State |> Option.get
                let fields = CaseState.fields current.Snapshot.State |> Option.get

                Expect.equal
                    row.Headline
                    current.Headline
                    "The headline is already disclosed by case.get"

                for role, name in
                    [ PartyRole.Claimant, "Claimant Example"; PartyRole.Insurer, "Insurer Example" ] do
                    let show = List.map PartyName.format
                    let names = Map.find role summary.Parties

                    let disclosed =
                        current.Parties
                        |> List.choose (fun party ->
                            if party.Content.Role = role then
                                party.Content.Name
                            else
                                None)

                    Expect.equal (show names) [ name ] "The summary shows the recorded role name"

                    Expect.equal
                        names
                        disclosed
                        "Summary and case reads disclose the same current names"

                let measure select =
                    row.Contributions
                    |> List.choose (fun contribution ->
                        select contribution
                        |> Option.map (fun amount -> contribution.Currency, amount))

                let expected money =
                    money
                    |> Option.map (fun value ->
                        Money.currencyValue value, DecimalTotal.ofAmount (Money.amount value))
                    |> Option.toList

                Expect.equal
                    (measure _.Claimed)
                    (expected (Registration.claimed fields.Registration))
                    "Claimed money is already disclosed"

                Expect.equal
                    (measure _.Payable)
                    (expected (fields.Decision |> Option.map Decision.payable))
                    "Payable money is already disclosed"

                let! history =
                    Service.timeline
                        caller
                        {
                            Target = CaseSelector.Reference(CaseReference.format row.Reference)
                            AfterCursor = None
                            Limit = 50
                            Filter = Some TimelineFilter.Facts
                        }
                        token
                    |> Async.AwaitTask

                let entries =
                    match history with
                    | Outcome.Succeeded value -> value.Items
                    | _ -> failtest "Reader has summary history"

                let entry = List.head entries

                Expect.equal
                    row.ChangedAt
                    entry.RecordedAt
                    "Last change is the latest accepted fact entry"

                Expect.equal
                    entry.Changes
                    TimelineChanges.Withheld
                    "A summary reader receives metadata without historical facts"
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testCaseAsync
        "case summaries preserve scoped business disclosure and ledger change time"
        (summaries ())
