module ClaimCore.Tests.ActorPrincipalIdentityTests

open Expecto
open ClaimCore.Application

let private issuer = "https://issuer.example.test/realms/claimcore"

let private human subject =
    PrincipalKey.human issuer subject
    |> Result.defaultWith (fun _ -> failtest "Synthetic human principal rejected")

let tests =
    testList
        "actor and grant authorization"
        [
            testCase "[CC-AUTH-001] principal identities are typed and exact" (fun () ->
                let person = human "same-value"
                let service = PrincipalKey.service issuer "same-value"

                Expect.notEqual
                    (Ok person)
                    service
                    "Human subject and service client cannot collide"

                Expect.notEqual (human "person-1") (human "person-2") "Subjects remain distinct"
                Expect.isError (PrincipalKey.human issuer " ") "Blank subject is refused"
                Expect.isError (PrincipalKey.service issuer " ") "Blank client ID is refused"

                Expect.isError
                    (PrincipalKey.human "http://issuer.example.test" "person")
                    "Issuer must use HTTPS")
        ]
