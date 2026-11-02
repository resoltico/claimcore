module internal ClaimCore.AcceptanceTests.CliCaseWork

open System
open Expecto
open CliCalls

let qualify (fixture: PublishedFixture.Fixture) =
    let environment = interactiveEnvironment fixture "synthetic-owner" "valid"
    let reference = "CLI-WORK-" + id ()

    let context =
        human fixture "synthetic-owner" "context" {| |} 0 |> require "SUCCEEDED"

    let actor = context.GetProperty("actorId").GetString() |> nonNull

    let execute verbName revision flags =
        verb
            fixture
            environment
            ([
                "case"
                verbName
                reference
                "--operation-id"
                id ()
                "--expected-revision"
                string revision
             ]
             @ flags)
            0
        |> require "ACCEPTED"

    let opened =
        verb
            fixture
            environment
            [
                "case"
                "open"
                reference
                "--operation-id"
                id ()
                "--incident-date"
                "2026-09-01"
                "--incident-notification-date"
                "2026-09-02"
                "--incident-country"
                "LV"
                "--claimant-name"
                "CLI work claimant"
                "--insurer-name"
                "CLI work insurer"
                "--claimed-amount"
                "100"
                "--claimed-currency"
                "EUR"
            ]
            0
        |> require "ACCEPTED"

    Expect.equal
        (opened.GetProperty("subject").GetProperty("revision").GetInt64())
        1L
        "Creation is complete"

    let candidates =
        verb fixture environment [ "case"; "assignees"; reference; "--limit"; "50" ] 0
        |> require "SUCCEEDED"

    Expect.isTrue
        (candidates.GetProperty("items").EnumerateArray()
         |> Seq.exists (fun item -> item.GetProperty("actorId").GetString() = actor))
        "Current eligible human is discoverable by exact identity"

    let planned = "2027-12-31"

    execute
        "update-work"
        1
        [
            "--responsible-actor-action"
            "set"
            "--responsible-actor"
            actor
            "--follow-up-date-action"
            "set"
            "--follow-up-date"
            planned
        ]
    |> ignore

    execute "amend-registration" 2 [ "--claimant-name"; "CLI selected fact" ]
    |> ignore

    execute
        "decide"
        3
        [
            "--payment-decision-date"
            "2026-09-03"
            "--payable-amount"
            "20"
            "--payable-currency"
            "EUR"
        ]
    |> ignore

    execute "record-payment" 4 [ "--payment-date"; "2026-09-04" ] |> ignore

    execute
        "correct"
        5
        [
            "--registration"
            "keep"
            "--decision"
            "update"
            "--payable-amount"
            "21"
            "--payable-currency"
            "EUR"
            "--payment"
            "reaffirm"
        ]
    |> ignore

    let current =
        human
            fixture
            "synthetic-owner"
            "case.get"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
            |}
            0
        |> require "SUCCEEDED"

    let fields = current.GetProperty("state").GetProperty("fields")

    Expect.equal
        (current.GetProperty("revision").GetInt64())
        6L
        "Each explicit change accepts one revision"

    Expect.equal
        (fields.GetProperty("registration").GetProperty("claimantName").GetString())
        "CLI selected fact"
        "Selected fact changes"

    Expect.equal
        (fields.GetProperty("registration").GetProperty("insurerName").GetString())
        "CLI work insurer"
        "Omitted fact survives"

    Expect.equal
        (fields.GetProperty("payment").GetProperty("paymentDate").GetString())
        "2026-09-04"
        "Authorized reaffirmation keeps the recorded payment"

    Expect.equal
        (fields.GetProperty("decision").GetProperty("payableAmount").GetString())
        "21"
        "Complete money assertion changes"

    Expect.equal
        (current.GetProperty("work").GetProperty("followUpDate").GetString())
        planned
        "Future planned work survives business edits"

    Expect.isTrue (current.GetProperty("mine").GetBoolean()) "Responsibility is factual"
