module ClaimCore.IntegrationTests.HistoricalHttpTests

open ClaimCore.Catalog

open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.IntegrationTests.VerificationSupport

let private httpRequest request =
    // This test authors a wire body from a retained Domain request; production has distinct codecs.
    let node =
        System.Text.Json.Nodes.JsonNode.Parse(RequestRecord.encode request) |> nonNull

    node.AsObject().Remove("format") |> ignore
    System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())

let private outcome (bytes: byte array) =
    use document = JsonDocument.Parse bytes
    document.RootElement.GetProperty("outcome").Clone()

let private caller (database: PostgresFixture.Database) =
    task {
        let! owner = caseWorker database.Fixture

        let principal =
            PrincipalKey.human
                ClaimCore.WebTests.TestServerFixture.oidc.Issuer.AbsoluteUri
                "synthetic-human"
            |> Result.defaultWith (fun _ -> failtest "Synthetic human")

        let! registered =
            owner.ExecuteAuthority(
                authorityRequest (id 20) principal AuthorityAct.RegisterActor,
                token
            )

        acceptedAuthority registered

        let! granted =
            grant owner (id 21) principal Role.CaseHandler GrantTarget.Installation true

        acceptedAuthority granted
        return core database.Fixture
    }

let private readRetained (host: ClaimCore.WebTests.TestServerFixture.Host) csrf name =
    task {
        let! status, response =
            host.Call(
                "case.get",
                Encoding.UTF8.GetBytes "{\"caseReference\":\"historical-1\"}",
                csrf
            )

        Expect.equal status 200 "Retained record passes real HTTP self-validation"

        let fields =
            (outcome response).GetProperty("data").GetProperty("state").GetProperty("fields")

        Expect.equal
            (fields.GetProperty("claimantName").GetString())
            name
            "Retained text reaches the client"

    }

let private retainedHistory (database: PostgresFixture.Database) =
    task {
        let! forActor = caller database
        let name = String.replicate 201 "A"
        do! AcceptedHistoryFixtures.seed database name
        use host = new ClaimCore.WebTests.TestServerFixture.Host(true, forActor = forActor)
        let! status, _ = host.Login()
        Expect.equal status 200 "Authenticated synthetic browser"
        let! csrf = host.Token()

        do! readRetained host csrf name

        let values =
            { registration with
                ClaimantName = name
                InsurerName = "Corrected insurer"
            }

        let reaffirm =
            request 11 "HISTORICAL-1" 1L (BusinessCommand.AmendRegistration values)

        let! status, response = host.Call("case.execute", httpRequest reaffirm, csrf)

        Expect.equal status 200 "Reaffirmation passes intake and response validation"

        Expect.equal
            ((outcome response).GetProperty("tag").GetString())
            "ACCEPTED"
            "Actual locked decision accepts unchanged retained text"

        let changed =
            request
                12
                "HISTORICAL-1"
                2L
                (BusinessCommand.AmendRegistration
                    { values with
                        ClaimantName = name + "B"
                    })

        let! status, response = host.Call("case.execute", httpRequest changed, csrf)

        Expect.equal status 200 "Admission refusal stays a typed outcome"
        let refusal = outcome response
        Expect.equal (refusal.GetProperty("tag").GetString()) "REJECTED" "Changed assertion refused"

        Expect.equal
            (refusal.GetProperty("data").GetProperty("diagnostic").GetProperty("id").GetString())
            "INPUT_TEXT_TOO_LONG"
            "Current policy owns the refusal"

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testCaseAsync
        "HTTP reads and reaffirms retained historical text but refuses changed new policy violations through real PostgreSQL"
        (run retainedHistory)
