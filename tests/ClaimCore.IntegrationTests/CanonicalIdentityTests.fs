module ClaimCore.IntegrationTests.CanonicalIdentityTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private vectors () =
    JsonDocument.Parse(
        File.ReadAllBytes(
            Path.Combine(RepositoryRoot.find (), "tests/fixtures/record-format-vectors.json")
        )
    )

let private encoded (vector: JsonElement) (side: string) =
    vector.GetProperty(side).GetProperty("canonicalHex").GetString()
    |> nonNull
    |> Convert.FromHexString

let private caseInput vector side =
    RequestRecord.decode Limits.MaximumRequestBytes (encoded vector side)
    |> Result.defaultWith (fun _ -> failtest "The independent request vector must decode")
    |> _.Authored

let private authorityInput vector side =
    let record =
        AuthorityRecord.decode Limits.MaximumRequestBytes (encoded vector side)
        |> Result.defaultWith (fun _ -> failtest "The independent authority vector must decode")

    AuthorityRequests.fromRecord record
    |> Result.defaultWith (fun _ -> failtest "The independent authority vector must admit")

let private retainedRequest database (receipt: OperationReceipt) =
    task {
        let! stored = row database receipt.Position.Sequence

        let bytes =
            stored.Entry.Payload
            |> Option.defaultWith (fun () -> failtest "Expected retained payload")

        return
            LedgerEntry.decodePayload LedgerPayload.MaximumBytes bytes
            |> Result.defaultWith (fun _ -> failtest "Expected a framed payload")
            |> _.Request
    }

let private casePairs (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        use document = vectors ()

        let cases =
            document.RootElement.GetProperty("canonicalIdentity").EnumerateArray()
            |> Seq.filter (fun value -> value.GetProperty("subject").GetString() = "CASE")
            |> Seq.choose (fun vector ->
                let left = caseInput vector "left"

                match left.Act with
                | AuthoredCaseAct.Open _ -> Some(vector, left, caseInput vector "right")
                | _ -> None)
            |> Seq.toList

        Expect.equal cases.Length 4 "Equivalent amounts/references and distinct amounts/names"

        for vector, left, right in cases do
            let! first = (Service.execute actor (OperationInput.Case left) (token))

            let receipt = accepted first
            let! before = verified database

            let! replay = (Service.execute actor (OperationInput.Case right) (token))

            let expected =
                if vector.GetProperty("same").GetBoolean() then
                    ExecuteResult.AlreadyAccepted receipt
                else
                    ExecuteResult.Rejected Refusal.IdempotencyConflict

            Expect.equal replay.Outcome expected "Replay follows independent canonical identity"
            let! after = verified database
            Expect.equal after before "Neither replay nor conflict appends a ledger entry"
            let! authored = retainedRequest database receipt

            Expect.equal
                authored
                (encoded vector "left")
                "Retained requests keep authored values, including 10.50"
    }

let private authorityPairs (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        use document = vectors ()

        let values =
            document.RootElement.GetProperty("canonicalIdentity").EnumerateArray()
            |> Seq.toList

        let opening =
            values
            |> List.find (fun value -> value.GetProperty("name").GetString() = "equal reference")

        let! opened =
            (Service.execute actor (OperationInput.Case(caseInput opening "left")) (token))

        accepted opened |> ignore

        let cases =
            values
            |> List.filter (fun value -> value.GetProperty("subject").GetString() = "AUTHORITY")

        Expect.equal cases.Length 2 "Both reference-bearing authority acts"
        let target = (authorityInput cases.Head "left").Target

        let! registered =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 901) target AuthorityAct.Register))
                (token))

        acceptedAuthority registered

        for vector in cases do
            let left =
                authorityInput vector "left"
                |> AuthorityRequests.toInput
                |> Result.defaultWith (string >> failtest)

            let right =
                authorityInput vector "right"
                |> AuthorityRequests.toInput
                |> Result.defaultWith (string >> failtest)

            let! first = (Service.execute actor (OperationInput.Authority left) (token))

            let receipt = accepted first
            let! before = verified database

            let! replay = (Service.execute actor (OperationInput.Authority right) (token))

            Expect.equal
                replay.Outcome
                (ExecuteResult.AlreadyAccepted receipt)
                "Grant and revoke compare folded references"

            let! after = verified database
            Expect.equal after before "Authority replay appends nothing"
            let! authored = retainedRequest database receipt

            Expect.equal
                authored
                (encoded vector "left")
                "Authority history keeps authored subject spelling"
    }

let private disclosureAndErasure (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        use document = vectors ()

        let values =
            document.RootElement.GetProperty("canonicalIdentity").EnumerateArray()
            |> Seq.toList

        let equal =
            values
            |> List.find (fun value -> value.GetProperty("name").GetString() = "equal amount")

        let different =
            values
            |> List.find (fun value -> value.GetProperty("name").GetString() = "different amount")

        let left, right = caseInput equal "left", caseInput equal "right"

        let unequal =
            { caseInput different "right" with
                OperationId = left.OperationId
                CaseReference = left.CaseReference
            }

        let! first = (Service.execute actor (OperationInput.Case left) (token))

        accepted first |> ignore

        let outsider =
            PrincipalKey.human "https://issuer.example/" "canonical-outsider"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 920) outsider AuthorityAct.Register))
                (token))

        acceptedAuthority registered
        let other = core database.Fixture outsider

        let! hiddenEqual = (Service.execute other (OperationInput.Case right) (token))

        let! hiddenDifferent = (Service.execute other (OperationInput.Case unequal) (token))

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
                (id 921)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority stewardship

        let! erased =
            (Service.execute
                actor
                (OperationInput.Case(
                    request 922 left.CaseReference 1L (AuthoredCaseAct.Bare BareAct.Erase)
                ))
                (token))

        accepted erased |> ignore
        let! before = verified database

        let! retry = (Service.execute actor (OperationInput.Case right) (token))

        Expect.equal
            retry.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Erased requests remain non-replayable"

        let! after = verified database
        Expect.equal after before "An erased replay attempt appends nothing"
    }

[<Tests>]
let tests =
    testList
        "canonical operation identity"
        [
            testCaseAsync
                "case replay and conflicts follow the independent oracle while retaining authored payloads"
                (run casePairs)
            testCaseAsync
                "authority grant and revoke replay with respelled case references"
                (run authorityPairs)
            testCaseAsync
                "comparison stays opaque to unauthorized actors and erased content cannot replay"
                (run disclosureAndErasure)
        ]
