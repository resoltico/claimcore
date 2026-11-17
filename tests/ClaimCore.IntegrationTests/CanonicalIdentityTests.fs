module ClaimCore.IntegrationTests.CanonicalIdentityTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private values amount claimant =
    DomainInputs.registrationWith
        [ BusinessField.ClaimedAmount, amount; BusinessField.ClaimantName, claimant ]
        registration

let private opening fixture number reference amount claimant =
    request
        (fixture)
        (number)
        (reference)
        (0L)
        "case.open"
        (ClaimCore.TestSupport.Submissions.fields (
            RegistrationInput.values ((values amount claimant))
        ))

let private retainedRequest database (receipt: OperationReceipt) =
    task {
        let! stored = row database receipt.Position.Sequence

        return stored
    }

let private casePairs (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let amount = opening database.Fixture 9300 "Canonical-amount" "10.50" "Claimant"

        let reference =
            opening database.Fixture 9301 "Canonical-Reference" "10.50" "Claimant"

        let differentAmount =
            opening database.Fixture 9302 "Canonical-different-amount" "10.50" "Claimant"

        let differentName =
            opening database.Fixture 9303 "Canonical-different-name" "10.50" "Claimant"

        let cases =
            [
                true, amount, opening database.Fixture 9300 "Canonical-amount" "10.5" "Claimant"
                true,
                reference,
                opening database.Fixture 9301 "canonical-reference" "10.50" "Claimant"
                false,
                differentAmount,
                opening database.Fixture 9302 "Canonical-different-amount" "11" "Claimant"
                false,
                differentName,
                opening database.Fixture 9303 "Canonical-different-name" "10.50" "Another claimant"
            ]

        for same, left, right in cases do
            let! first = Service.executeSubmission actor ((left)) token
            let receipt = accepted first
            let! before = verified database
            let! replay = Service.executeSubmission actor ((right)) token

            if same then
                expectReceipt (replayReceipt replay) receipt "Equivalent amounts/references replay"
            else
                Expect.equal
                    replay.Outcome
                    (ExecuteResult.Rejected Refusal.IdempotencyConflict)
                    "Different amounts/names conflict"

            let! after = verified database
            Expect.equal after before "Replay and conflicts append nothing"
            let! authored = retainedRequest database receipt

            let expected = Submissions.command left

            Expect.equal
                authored.Inputs
                (Some expected.Inputs)
                "Retained inputs preserve authored reference and canonical decimal meaning"

    }

let private peoplePairs (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let input = opening database.Fixture 9310 "Canonical-Reference" "10.50" "Claimant"
        let! opened = Service.executeSubmission actor ((input)) token
        let parent = accepted opened |> caseReceipt

        let target =
            PrincipalKey.human "https://issuer.example/" "canonical-target"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 901))
                        (target)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (DomainInputs.actorProfile))

                return! Service.executeSubmission (actor) submission (token)
            })

        acceptedPeople registered

        let grant reference =
            {
                Role = Role.CaseHandler
                Scope = GrantTarget.CaseReference reference
            }

        for number, taskName, active in [ 9320, "people.grant", true; 9321, "people.revoke", false ] do
            let! left =
                personSubmission
                    database.Fixture.Store
                    (id database.Fixture number)
                    target
                    None
                    taskName
                    (Submissions.grant (grant "Canonical-Reference"))

            let right =
                Submissions.createRaw
                    taskName
                    left.OperationId
                    left.Envelope.Binding
                    (Submissions.grant (grant "canonical-reference"))

            let! first = Service.executeSubmission actor ((left)) token
            let receipt = accepted first
            let! before = verified database
            let! replay = Service.executeSubmission actor ((right)) token

            expectReceipt
                (replayReceipt replay)
                receipt
                "Reference-bearing grant/revoke inputs compare folded case references"

            let! after = verified database
            Expect.equal after before "People replay appends nothing"
            let! authored = retainedRequest database receipt

            let expected =
                {
                    Role = Role.CaseHandler
                    Scope = Scope.Case parent.CaseId
                }

            match authored.Changes |> List.exactlyOne |> _.Value with
            | PartValue.Person person ->
                Expect.contains
                    person.Grants
                    { Grant = expected; Active = active }
                    "The accepted assignment uses the resolved case identity"
            | _ -> failtest "Retained person part required"

    }

let private disclosureAndErasure (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let left = opening database.Fixture 9340 "Canonical-private" "10.50" "Claimant"

        let right = opening database.Fixture 9340 "Canonical-private" "10.5" "Claimant"
        let unequal = opening database.Fixture 9340 "Canonical-private" "11" "Claimant"

        let! first = (Service.executeSubmission actor ((left)) (token))

        accepted first |> ignore

        let outsider =
            PrincipalKey.human "https://issuer.example/" "canonical-outsider"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 920))
                        (outsider)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission ((token))
            }))

        acceptedPeople registered
        let other = core database.Fixture outsider

        let! hiddenEqual = (Service.executeSubmission other ((right)) (token))

        let! hiddenDifferent = (Service.executeSubmission other ((unequal)) (token))

        Expect.equal
            hiddenEqual.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Unauthorized equivalent content is opaque"

        Expect.equal
            hiddenDifferent.Outcome
            hiddenEqual.Outcome
            "Unauthorized different content reveals no comparison"

        let! stewardship =
            grant
                actor
                (id database.Fixture 921)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople stewardship

        let! erased =
            (Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (922)
                    ("Canonical-private")
                    (1L)
                    "case.erase"
                    (Map.empty))))
                (token))

        accepted erased |> ignore
        let! before = verified database

        let! retry = (Service.executeSubmission actor ((right)) (token))

        Expect.equal
            retry.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Erased requests remain non-replayable"

        let! after = verified database
        Expect.equal after before "An erased replay attempt appends nothing"
    }

let private submissionMaps (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let decode source =
            ClaimCore.Json.Codec.decode
                ClaimCore.Wire.Limits.MaximumRequestBytes
                ClaimCore.Wire.OperationSubmissionCodecs.submission
                (System.Text.Encoding.UTF8.GetBytes(source: string))
            |> Result.defaultWith (fun error -> failtestf "Submission admission: %A" error)

        let opening = opening database.Fixture 9400 "Map-Replay" "10.50" "Claimant"

        let! opened = Service.executeSubmission actor opening token
        accepted opened |> ignore
        let operation = (id database.Fixture 9401).ToString("D")

        let raw inputs =
            sprintf
                """{"operationId":"%s","task":"case.amendRegistration","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"Map-Replay"},"expectedRevision":1},"inputs":{%s}}"""
                operation
                inputs

        let first =
            raw "\"field:policyNumber\":\"PN-1\",\"field:claimantName\":\"Changed\""
            |> decode

        let! result = Service.executeSubmission actor first token
        let receipt = accepted result
        let! before = verified database

        let reordered =
            raw "\"field:claimantName\":\"Changed\",\"field:policyNumber\":\"PN-1\""
            |> decode

        let! replay = Service.executeSubmission actor reordered token

        expectReceipt
            (replayReceipt replay)
            receipt
            "Map member order does not change accepted identity"

        let! conflict =
            Service.executeSubmission
                actor
                (raw "\"field:claimantName\":\"Changed\",\"field:policyNumber\":\"PN-2\""
                 |> decode)
                token

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Changed map content cannot reuse an accepted operation ID"

        for key in [ "unknown"; "input:noteText" ] do
            let invalid = raw (sprintf "\"%s\":\"foreign\"" key)

            Expect.isError
                (ClaimCore.Json.Codec.decode
                    ClaimCore.Wire.Limits.MaximumRequestBytes
                    ClaimCore.Wire.OperationSubmissionCodecs.submission
                    (System.Text.Encoding.UTF8.GetBytes invalid))
                "Unknown and foreign keys cannot become dispatched writes"

        let! after = verified database
        Expect.equal after before "Replay, conflict and closed-map refusals append nothing"
        let registration = id database.Fixture 9402

        let principal =
            PrincipalKey.human "https://issuer.example/" "selected-person"
            |> Result.defaultWith (string >> failtest)

        let! command =
            personSubmission
                database.Fixture.Store
                registration
                principal
                None
                "people.register"
                (Submissions.profile DomainInputs.actorProfile)

        let! registered = Service.executeSubmission actor ((command)) token

        acceptedPeople registered

        let selected =
            ActorId.ofOperationId (OperationId.create registration |> Option.get)
            |> ActorId.value

        let disable =
            sprintf
                """{"operationId":"%s","task":"people.disable","binding":{"kind":"PEOPLE","actorId":"%s","expectedRevision":1},"inputs":{}}"""
                ((id database.Fixture 9403).ToString("D"))
                (selected.ToString("D"))
            |> decode

        let! disabled = Service.executeSubmission actor disable token
        let receipt = accepted disabled
        let! repeated = Service.executeSubmission actor disable token

        expectReceipt
            (replayReceipt repeated)
            receipt
            "Actor selection resolves the same immutable identity for exact replay"

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testList
        "canonical operation identity"
        [
            testCaseAsync
                "declared submission maps preserve key-order replay conflict and actor selection"
                (run submissionMaps)
            testCaseAsync
                "equivalent amounts and references replay while different content conflicts"
                (run casePairs)
            testCaseAsync
                "people grant and revoke replay with respelled case references"
                (run peoplePairs)
            testCaseAsync
                "comparison stays opaque to unauthorized actors and erased content cannot replay"
                (run disclosureAndErasure)
        ]
