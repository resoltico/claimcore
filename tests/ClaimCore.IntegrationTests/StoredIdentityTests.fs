module ClaimCore.IntegrationTests.StoredIdentityTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private corrupt (path: string list) (value: LedgerRow) =
    let bytes =
        value.Entry.Payload
        |> Option.defaultWith (fun () -> failtest "An authority payload is retained")

    let frame =
        LedgerEntry.decodePayload 262144 bytes
        |> Result.defaultWith (fun _ -> failtest "The initial frame is valid")

    let root = JsonNode.Parse(frame.Request) |> nonNull
    let owners, memberName = path |> List.take (path.Length - 1), List.last path

    let target =
        owners |> List.fold (fun (node: JsonNode) name -> node[name] |> nonNull) root

    target[memberName] <- JsonValue.Create "UNDECLARED"

    let request =
        Encoding.UTF8.GetBytes(root.ToJsonString())
        |> StrictJson.read 65536
        |> Result.map CanonicalWriter.bytes
        |> Result.defaultWith (fun _ ->
            failtest "The damaged request remains strict canonical JSON")

    match AuthorityRecord.decode 65536 request with
    | Error problem ->
        Expect.equal
            problem.Field
            ("$." + String.concat "." path)
            "The storage decoder owns the enum failure"
    | Ok _ -> failtest "An undeclared stored enum must not escape record decoding"

    payload request frame.Snapshot value

let private qualify path (database: PostgresFixture.Database) =
    task {
        let! actor = bootstrap database.Fixture
        let operationId = id database.Fixture 2

        let! executed =
            grant
                actor
                operationId
                database.Fixture.Owner
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                true

        let receipt = accepted executed

        let observedOperation =
            if path = [ "act"; "principal"; "kind" ] then
                id database.Fixture 1
            else
                operationId

        let! before = Service.operationGet actor { OperationId = observedOperation } token

        match before.Outcome with
        | Outcome.Succeeded _ -> ()
        | _ -> failtest "The exact receipt is readable before its stored enum is damaged"

        let sequence =
            if path = [ "act"; "principal"; "kind" ] then
                // The first provisioning effect owns identity; the following profile effect does not.
                CaseFixtures.sequence 1L
            else
                receipt.Position.Sequence

        do! rewrite database sequence (corrupt path)

        let! after = Service.operationGet actor { OperationId = observedOperation } token

        Expect.equal
            after.Outcome
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "Stored corruption is never a user-input refusal"
    }

[<Tests>]
let tests =
    testList
        "stored identity enums"
        [
            testCaseAsync
                "a damaged stored principal kind is an integrity fault"
                (run (qualify [ "act"; "principal"; "kind" ]))
            testCaseAsync
                "a damaged stored role is an integrity fault"
                (run (qualify [ "act"; "role" ]))
        ]
