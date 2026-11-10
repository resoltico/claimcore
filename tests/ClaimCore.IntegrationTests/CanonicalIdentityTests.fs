module ClaimCore.IntegrationTests.CanonicalIdentityTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private values amount claimant =
    Inputs.registrationWith
        [ BusinessField.ClaimedAmount, amount; BusinessField.ClaimantName, claimant ]
        registration

let private opening fixture number reference amount claimant =
    request fixture number reference 0L (AuthoredCaseAct.Open(values amount claimant))

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
                true,
                amount,
                { amount with
                    Act = AuthoredCaseAct.Open(values "10.5" "Claimant")
                }
                true,
                reference,
                { reference with
                    CaseReference = "canonical-reference"
                }
                false,
                differentAmount,
                { differentAmount with
                    Act = AuthoredCaseAct.Open(values "11" "Claimant")
                }
                false,
                differentName,
                { differentName with
                    Act = AuthoredCaseAct.Open(values "10.50" "Another claimant")
                }
            ]

        for same, left, right in cases do
            let! first = Service.execute actor (Inputs.caseOperation left) token
            let receipt = accepted first
            let! before = verified database
            let! replay = Service.execute actor (Inputs.caseOperation right) token

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

            let restored =
                match authored.Values with
                | RetainedOperationValues.Case(Some values) -> values.Command
                | _ -> failtest "Available retained case command required"

            Expect.equal
                restored.Authored
                { left with
                    Act = AuthoredCaseAct.Open(values "10.5" "Claimant")
                }
                "Retained typed values preserve authored reference and canonical decimal meaning"
    }

let private authorityPairs (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let input = opening database.Fixture 9310 "Canonical-Reference" "10.50" "Claimant"
        let! opened = Service.execute actor (Inputs.caseOperation input) token
        let parent = accepted opened |> caseReceipt

        let target =
            PrincipalKey.human "https://issuer.example/" "canonical-target"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            Service.execute
                actor
                (OperationInput.Authority(
                    authorityRequest
                        (id database.Fixture 901)
                        target
                        (AuthorityAct.Register Inputs.actorProfile)
                ))
                token

        acceptedAuthority registered

        let grant reference =
            {
                Role = Role.CaseHandler
                Scope = GrantTarget.CaseReference reference
            }

        for number, act, effect in
            [
                9320, AuthorityAct.Grant, AuthorityKind.Grant
                9321, AuthorityAct.Revoke, AuthorityKind.Revoke
            ] do
            let left =
                authorityRequest
                    (id database.Fixture number)
                    target
                    (act (grant "Canonical-Reference"))

            let right =
                { left with
                    Act = act (grant "canonical-reference")
                }

            let! first = Service.execute actor (OperationInput.Authority left) token
            let receipt = accepted first
            let! before = verified database
            let! replay = Service.execute actor (OperationInput.Authority right) token

            expectReceipt
                (replayReceipt replay)
                receipt
                "Reference-bearing grant/revoke inputs compare folded case references"

            let! after = verified database
            Expect.equal after before "Authority replay appends nothing"
            let! authored = retainedRequest database receipt

            let restored =
                authored.Root.Structure.Effects
                |> List.exactlyOne
                |> function
                    | EffectDescriptor.Authority(_, descriptor) -> descriptor
                    | _ -> failtest "Retained authority effect required"

            let expected =
                {
                    Role = Role.CaseHandler
                    Scope = Scope.Case parent.CaseId
                }

            Expect.equal
                restored
                (if effect = AuthorityKind.Grant then
                     AuthorityEffectDescriptor.Grant expected
                 else
                     AuthorityEffectDescriptor.Revoke expected)
                "The retained authority command uses the resolved case identity"
    }

let private disclosureAndErasure (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let left = opening database.Fixture 9340 "Canonical-private" "10.50" "Claimant"

        let right =
            { left with
                Act = AuthoredCaseAct.Open(values "10.5" "Claimant")
            }

        let unequal =
            { left with
                Act = AuthoredCaseAct.Open(values "11" "Claimant")
            }

        let! first =
            (Service.execute actor (ClaimCore.TestSupport.Inputs.caseOperation left) (token))

        accepted first |> ignore

        let outsider =
            PrincipalKey.human "https://issuer.example/" "canonical-outsider"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                actor
                (OperationInput.Authority(
                    authorityRequest
                        (id database.Fixture 920)
                        outsider
                        (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                ))
                (token))

        acceptedAuthority registered
        let other = core database.Fixture outsider

        let! hiddenEqual =
            (Service.execute other (ClaimCore.TestSupport.Inputs.caseOperation right) (token))

        let! hiddenDifferent =
            (Service.execute other (ClaimCore.TestSupport.Inputs.caseOperation unequal) (token))

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

        acceptedAuthority stewardship

        let! erased =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request
                        database.Fixture
                        922
                        left.CaseReference
                        1L
                        (AuthoredCaseAct.Bare BareAct.Erase)
                ))
                (token))

        accepted erased |> ignore
        let! before = verified database

        let! retry =
            (Service.execute actor (ClaimCore.TestSupport.Inputs.caseOperation right) (token))

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
                "equivalent amounts and references replay while different content conflicts"
                (run casePairs)
            testCaseAsync
                "authority grant and revoke replay with respelled case references"
                (run authorityPairs)
            testCaseAsync
                "comparison stays opaque to unauthorized actors and erased content cannot replay"
                (run disclosureAndErasure)
        ]
