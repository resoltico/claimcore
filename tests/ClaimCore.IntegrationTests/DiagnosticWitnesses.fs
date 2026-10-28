module ClaimCore.IntegrationTests.DiagnosticWitnesses

open ClaimCore.Protocol

open ClaimCore.TestSupport

open ClaimCore.Json

open System
open System.Threading
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures


let internal ct = CancellationToken.None

let internal decision: DecisionInput =
    {
        PaymentDecisionDate = "2026-08-15"
        PayableAmount = "750.00"
        PayableCurrency = "EUR"
    }

let internal keep: AuthoredCorrection =
    {
        Registration = Amendment.Keep
        Decision = Change.Keep
        Payment = Change.Keep
    }

let internal correction value = (AuthoredCaseAct.Correct value)

let internal payment =
    (AuthoredCaseAct.RecordPayment { PaymentDate = "2026-08-20" })

let internal decided = (AuthoredCaseAct.Decide decision)
let internal closed = AuthoredCaseAct.Close
let internal voided = (AuthoredCaseAct.Void "Synthetic reason")

let internal baseline =
    lazy
        (let path =
            System.IO.Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "tests/fixtures/refusal-baseline.json"
            )

         use document = JsonDocument.Parse(System.IO.File.ReadAllBytes path)

         document.RootElement.GetProperty("observations").EnumerateArray()
         |> Seq.collect (fun row ->
             let code = row.GetProperty("code").GetString() |> nonNull
             let action = row.GetProperty("action").GetString() |> nonNull

             if code = "INVALID_INPUT" then
                 seq {
                     yield (code, None), action

                     for issue in row.GetProperty("issues").EnumerateArray() do
                         yield
                             (issue.GetProperty("code").GetString() |> nonNull,
                              Some(issue.GetProperty("field").GetString() |> nonNull)),
                             action
                 }
             else
                 Seq.singleton (
                     (code, row.GetProperty("target").GetString() |> Option.ofObj),
                     action
                 ))
         |> Map.ofSeq)

let internal baselinePolicy code target action =
    match Map.tryFind (code, target) baseline.Value with
    | Some expected -> Expect.equal (string action) expected "Captured repair action is preserved."
    | None ->
        failtestf "Request diagnostic is absent from the independent baseline: %s %A" code target

let internal diagnostic reason =
    let identifier = DiagnosticCodecs.code reason

    let reply: Outcome<CaseReadModel> = Outcome.Rejected reason
    let encoded = Codec.encode Api.caseGet.Output reply

    Expect.equal
        (Codec.decode Limits.MaximumResponseBytes Api.caseGet.Output encoded)
        (Ok reply)
        "Every request-produced refusal crosses the typed Wire boundary"

    use document = JsonDocument.Parse(encoded.AsMemory())

    let token =
        document.RootElement
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("code")
            .GetString()

    Expect.equal
        token
        (identifier)
        "The public diagnostic token agrees with the request-producing Application refusal"

    match reason with
    | Refusal.InvalidInput issues ->
        baselinePolicy "INVALID_INPUT" None RecommendedAction.CorrectInput

        ("INVALID_INPUT", None)
        :: (InputIssues.toList issues
            |> List.map (fun issue ->
                let code, _ = DiagnosticCodecs.describeIssue issue
                let target = Some(InputTarget.token issue.Target)
                baselinePolicy code target RecommendedAction.CorrectInput
                code, target))
    | reason ->
        let target =
            match reason with
            | Refusal.InvalidCursor -> Some(InputTarget.token InputTarget.Cursor)
            | Refusal.OperationResolved -> Some(InputTarget.token InputTarget.OperationId)
            | _ -> None

        baselinePolicy identifier target reason.Action
        [ identifier, target ]

let internal refused (reply: ExecuteReply) =
    match reply.Outcome with
    | ExecuteResult.Rejected reason -> diagnostic reason
    | _ -> failtest "The request must produce a public refusal."

let internal queryRefused =
    function
    | Outcome.Rejected reason -> diagnostic reason
    | _ -> failtest "The query must produce a public refusal."

let internal execute actor pending =
    (Service.execute (actor: ServiceContext) (OperationInput.Case pending) (ct))

let internal opening values =
    { openRequest 10 "REACH-1" with
        Act = (AuthoredCaseAct.Open values)
    }

let internal textRequests () =
    [
        opening { registration with ClaimantName = "" }
        opening
            { registration with
                ClaimantName = String(char 0xD800, 1)
            }
        opening
            { registration with
                ClaimantName = " Synthetic"
            }
        opening
            { registration with
                ClaimantName = String('x', 201)
            }
        opening
            { registration with
                ClaimantName = "Synthetic\nParty"
            }
        opening
            { registration with
                ClaimantName = "Synthetic​Party"
            }
        opening { registration with InsurerName = "" }
    ]

let internal registrationRequests () =
    [
        opening
            { registration with
                IncidentDate = "not-a-date"
            }
        opening
            { registration with
                IncidentNotificationDate = "not-a-date"
            }
        opening
            { registration with
                IncidentDate = "2026-08-05"
            }
        opening
            { registration with
                IncidentDate = "2027-01-01"
                IncidentNotificationDate = "2027-01-02"
            }
        opening
            { registration with
                IncidentCountry = "Latvia"
            }
        opening
            { registration with
                ClaimedAmount = "-1"
            }
        opening
            { registration with
                ClaimedCurrency = "Euro"
            }
    ]

let internal controlRequests () =
    [
        openRequest 10 "bad reference"
        { openRequest 10 "REACH-1" with
            OperationId = Guid.Empty
        }
        { openRequest 10 "REACH-1" with
            ExpectedRevision = -1L
        }
    ]

let internal progressRequests () =
    [
        request
            10
            "REACH-1"
            1L
            (AuthoredCaseAct.Decide
                { decision with
                    PaymentDecisionDate = "bad"
                })
        request 10 "REACH-1" 1L (AuthoredCaseAct.Decide { decision with PayableAmount = "bad" })
        request
            10
            "REACH-1"
            1L
            (AuthoredCaseAct.Decide
                { decision with
                    PayableCurrency = "bad"
                })
        request 10 "REACH-1" 1L (AuthoredCaseAct.RecordPayment { PaymentDate = "bad" })
        actRequest 10 "REACH-1" 1L ((AuthoredCaseAct.Void ""))
    ]

let internal scalarRequests () =
    textRequests ()
    @ registrationRequests ()
    @ controlRequests ()
    @ progressRequests ()

let internal scalarEvidence () =
    task {
        let! _, fixture = fixture ()

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture
        let results = ResizeArray<_>()

        for index, request in scalarRequests () |> List.indexed do
            // Distinct witnesses reserve distinct operation identities, including known business refusals.
            let pending =
                if request.OperationId = Guid.Empty then
                    request
                else
                    { request with
                        OperationId = id (100 + index)
                    }

            let! outcome = execute actor pending
            results.Add(refused outcome)

        return List.ofSeq results
    }

let internal stateScenarios () =
    [
        [], (AuthoredCaseAct.Open registration)
        [], AuthoredCaseAct.Reopen
        [ closed ], AuthoredCaseAct.Close
        [ closed ], decided
        [ decided ], (AuthoredCaseAct.AmendRegistration registration)
        [ decided ],
        correction
            { keep with
                Registration = Amendment.Replace registration
            }
        [], correction { keep with Decision = Change.Clear }
        [], payment
        [], AuthoredCaseAct.ClearPayment
        [ decided; payment ], payment
        [ decided; payment ], AuthoredCaseAct.WithdrawDecision
        [ (AuthoredCaseAct.Decide { decision with PayableAmount = "0" }) ], payment
        [ voided ], closed
        [ voided ], voided
        [], (AuthoredCaseAct.Reinstate "Synthetic reason")
        [ AuthoredCaseAct.Erase ], closed
        [ decided; payment ], correction { keep with Decision = Change.Clear }
        [ decided; payment ],
        correction
            { keep with
                Decision = Change.Replace decision
            }
    ]

let internal stateEvidence (setup, final) =
    task {
        let! _, fixture = fixture ()

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture

        let! granted =
            grant
                actor
                (id 3)
                owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted
        let! opening = execute actor (openRequest 10 "REACH-1")
        accepted opening |> ignore

        for index, act in List.indexed setup do
            let! outcome =
                execute actor (actRequest (11 + index) "REACH-1" (int64 (index + 1)) act)

            accepted outcome |> ignore

        let! outcome =
            execute actor (actRequest 50 "REACH-1" (int64 (setup.Length + 1)) final)

        return refused outcome
    }

let internal queryEvidence actor =
    task {
        let! limit =
            (Service.list (actor: ServiceContext) ({ AfterCursor = None; Limit = 0 }) (ct))

        let! cursor = (Service.list actor ({ AfterCursor = Some "bad"; Limit = 1 }) (ct))

        let! missing = (Service.get actor ({ CaseReference = "MISSING" }) (ct))
        return [ queryRefused limit; queryRefused cursor; queryRefused missing ]
    }

let internal authorityEvidence actor =
    task {
        let service =
            PrincipalKey.service "https://issuer.example/" "synthetic"
            |> Result.defaultWith (string >> failtest)

        let send n target act =
            (Service.execute
                (actor: ServiceContext)
                (OperationInput.Authority(authorityRequest (id n) target act))
                (ct))

        let! registered = send 30 owner AuthorityAct.Register
        let! unknown = send 31 service (AuthorityAct.Disable)
        let! last = send 32 owner (AuthorityAct.Disable)
        let! admitted = send 33 service AuthorityAct.Register
        acceptedAuthority admitted

        let! human =
            grant
                actor
                (id 34)
                service
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        return [ refused registered; refused unknown; refused last; refused human ]
    }

let internal identityEvidence actor =
    task {
        let pending = openRequest 10 "REACH-1"
        let! opening = execute actor pending
        accepted opening |> ignore
        let! conflict = execute actor { pending with ExpectedRevision = 1L }
        let! revision = execute actor (request 11 "REACH-1" 0L AuthoredCaseAct.Close)

        let reader =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            (Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 35) reader AuthorityAct.Register))
                (ct))

        acceptedAuthority registered

        let! granted =
            grant
                actor
                (id 36)
                reader
                {
                    Role = Role.CaseReader
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted
        return [ refused conflict; refused revision ], reader
    }

let internal principalEvidence (actor: ServiceContext) =
    task {
        let evidence = ResizeArray<_>()

        for index, (issuer, subject) in
            List.indexed
                [
                    "", "valid"
                    "https://issuer.example/", ""
                    "invalid", "valid"
                    "https://issuer.example/", String('x', 513)
                ] do
            let request: ClaimCore.Domain.AuthorityRequest =
                {
                    OperationId = id (60 + index)
                    Target =
                        {
                            Kind = PrincipalKind.Human
                            Issuer = issuer
                            Subject = subject
                        }
                    Act = ClaimCore.Domain.AuthorityAct.Register
                }

            let! outcome = (Service.execute actor (OperationInput.Authority request) (ct))

            evidence.Add(refused outcome)

        return List.ofSeq evidence
    }

let internal boundaryEvidence () =
    task {
        let! _, fixture = fixture ()

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture
        let! identity, reader = identityEvidence actor

        let! denied =
            execute (core fixture reader) (request 37 "REACH-1" 1L AuthoredCaseAct.Close)

        let! queries = queryEvidence actor
        let! authority = authorityEvidence actor
        let! principals = principalEvidence actor

        let! resolved = (Service.resolve actor ({ OperationId = id 42 }) (ct))

        Expect.equal resolved (Outcome.Succeeded Resolved.NotAccepted) "Resolve installs a fence"
        let! fenced = execute actor (request 42 "REACH-1" 1L AuthoredCaseAct.Close)
        return identity @ [ refused denied; refused fenced ] @ queries @ authority @ principals
    }
