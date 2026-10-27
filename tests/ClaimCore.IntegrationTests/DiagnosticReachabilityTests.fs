module ClaimCore.IntegrationTests.DiagnosticReachabilityTests

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

let private ct = CancellationToken.None

let private decision: DecisionInput =
    {
        PaymentDecisionDate = "2026-08-15"
        PayableAmount = "750.00"
        PayableCurrency = "EUR"
    }

let private keep: AuthoredCorrection =
    {
        Registration = Amendment.Keep
        Decision = Change.Keep
        Payment = Change.Keep
    }

let private correction value = (AuthoredCaseAct.Correct value)

let private payment = (AuthoredCaseAct.RecordPayment { PaymentDate = "2026-08-20" })

let private decided = (AuthoredCaseAct.Decide decision)
let private closed = AuthoredCaseAct.Close
let private voided = (AuthoredCaseAct.Void "Synthetic reason")

let private baseline =
    lazy
        (let path =
            System.IO.Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "tests/fixtures/refusal-baseline.json"
            )

         use document = JsonDocument.Parse(System.IO.File.ReadAllBytes path)

         document.RootElement.GetProperty("observations").EnumerateArray()
         |> Seq.map (fun row ->
             (row.GetProperty("code").GetString() |> nonNull,
              row.GetProperty("target").GetString() |> Option.ofObj),
             (row.GetProperty("action").GetString() |> nonNull))
         |> Map.ofSeq)

let private baselinePolicy code (reason: Refusal) =
    match Map.tryFind (code, reason.Field) baseline.Value with
    | None when code = "OPERATION_RESOLVED" ->
        Expect.equal
            reason.Action
            RecommendedAction.ReadCurrent
            "Resolve requires a new operation after reading current state"

        Expect.equal reason.Field (Some "operationId") "The resolved identity is targeted"
    | Some action ->
        Expect.equal (string reason.Action) action "Captured baseline action is preserved."
    | None ->
        Expect.contains
            [ Some "principal.issuer"; Some "principal.subject" ]
            reason.Field
            "Only principal targets extend the captured baseline."

        Expect.contains
            [ "TEXT_REQUIRED"; "PRINCIPAL_FORMAT" ]
            code
            "Only principal admission extends the captured baseline."

        Expect.equal
            reason.Action
            RecommendedAction.CorrectInput
            "Principal admission is repairable input."

let private diagnostic reason =
    let identifier = DiagnosticCodecs.code reason

    let reply: ReadReply<CaseReadModel> = ReadReply.Rejected reason
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

    baselinePolicy identifier reason
    identifier, reason.Field

let private refused (reply: ExecuteReply) =
    match reply.Outcome with
    | ExecuteResult.Rejected reason -> diagnostic reason
    | _ -> failtest "The request must produce a public refusal."

let private queryRefused =
    function
    | ReadReply.Rejected reason -> diagnostic reason
    | _ -> failtest "The query must produce a public refusal."

let private execute actor pending =
    (Service.execute (actor: ServiceContext) (OperationInput.Case pending) (ct))

let private opening values =
    { openRequest 10 "REACH-1" with
        Act = (AuthoredCaseAct.Open values)
    }

let private textRequests () =
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

let private registrationRequests () =
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

let private controlRequests () =
    [
        openRequest 10 "bad reference"
        { openRequest 10 "REACH-1" with
            OperationId = Guid.Empty
        }
        { openRequest 10 "REACH-1" with
            ExpectedRevision = -1L
        }
    ]

let private progressRequests () =
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

let private scalarRequests () =
    textRequests ()
    @ registrationRequests ()
    @ controlRequests ()
    @ progressRequests ()

let private scalarEvidence () =
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

let private stateScenarios () =
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

let private stateEvidence (setup, final) =
    task {
        let! _, fixture = fixture ()

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture

        let! granted =
            grant actor (id 3) owner Role.DataSteward GrantTarget.Installation true

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

let private queryEvidence actor =
    task {
        let! limit =
            (Service.list (actor: ServiceContext) ({ AfterCursor = None; Limit = 0 }) (ct))

        let! cursor = (Service.list actor ({ AfterCursor = Some "bad"; Limit = 1 }) (ct))

        let! missing = (Service.get actor ({ CaseReference = "MISSING" }) (ct))
        return [ queryRefused limit; queryRefused cursor; queryRefused missing ]
    }

let private authorityEvidence actor =
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
            grant actor (id 34) service Role.DataSteward GrantTarget.Installation true

        return [ refused registered; refused unknown; refused last; refused human ]
    }

let private identityEvidence actor =
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
            grant actor (id 36) reader Role.CaseReader GrantTarget.Installation true

        acceptedAuthority granted
        return [ refused conflict; refused revision ], reader
    }

let private principalEvidence (actor: ServiceContext) =
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
                            Kind = PrincipalKind.humanToken
                            Issuer = issuer
                            Subject = subject
                        }
                    Act = ClaimCore.Domain.AuthorityAct.Register
                }

            let! outcome = (Service.execute actor (OperationInput.Authority request) (ct))

            evidence.Add(refused outcome)

        for index, role in List.indexed [ ""; "UNDECLARED_ROLE" ] do
            let request: ClaimCore.Domain.AuthorityRequest =
                {
                    OperationId = id (64 + index)
                    Target = PrincipalKey.describe actor.Principal
                    Act =
                        AuthorityAct.Grant
                            {
                                Role = role
                                Scope = GrantTarget.Installation
                            }
                }

            let! outcome = Service.execute actor (OperationInput.Authority request) ct
            evidence.Add(refused outcome)

        return List.ofSeq evidence
    }

let private boundaryEvidence () =
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

        Expect.equal resolved ResolveReply.NotAccepted "Resolve installs a fence"
        let! fenced = execute actor (request 42 "REACH-1" 1L AuthoredCaseAct.Close)
        return identity @ [ refused denied; refused fenced ] @ queries @ authority @ principals
    }

let private completeVocabulary () =
    async {
        let! scalar = scalarEvidence () |> Async.AwaitTask

        let! states =
            stateScenarios () |> List.map stateEvidence |> Task.WhenAll |> Async.AwaitTask

        let! boundaries = boundaryEvidence () |> Async.AwaitTask
        let evidence = scalar @ List.ofArray states @ boundaries
        let identifiers = evidence |> List.map fst |> Set.ofList

        let baselineCodes = baseline.Value.Keys |> Seq.map fst |> Set.ofSeq

        Expect.equal
            (Set.ofList DiagnosticCodecs.codes)
            (baselineCodes |> Set.add "PRINCIPAL_FORMAT" |> Set.add "OPERATION_RESOLVED")
            "The codec preserves captured refusal semantics and includes principal and role admission."

        let produced = Set.ofList evidence

        Expect.isEmpty
            (Set.difference (baseline.Value.Keys |> Set.ofSeq) produced |> Set.toList)
            (sprintf
                "Captured code/target witnesses missing: %A"
                (Set.difference (baseline.Value.Keys |> Set.ofSeq) produced))

        let expected = DiagnosticCodecs.codes |> Set.ofList

        Expect.isEmpty
            (Set.difference expected identifiers |> Set.toList)
            (sprintf
                "Public diagnostics without a request witness: %A"
                (Set.difference expected identifiers |> Set.toList))

        Expect.isEmpty
            (Set.difference identifiers expected |> Set.toList)
            "Requests never emit a diagnostic outside the public vocabulary"

        let fields = evidence |> List.choose snd |> Set.ofList

        let expectedFields =
            (InputTarget.all |> List.map snd) @ [ "limit"; "cursor" ] |> Set.ofList

        Expect.equal
            fields
            expectedFields
            "Every public field token has an Application request witness"
    }

let private missingCasePrivacy () =
    async {
        let! _, fixture = fixture () |> Async.AwaitTask

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! actor = caseWorker fixture |> Async.AwaitTask

        let acts =
            stateScenarios ()
            |> List.collect (fun (setup, final) -> final :: setup)
            |> List.filter (fun act -> CaseAct.kind act <> CaseActKind.Open)

        let kinds = acts |> List.map CaseAct.kind |> Set.ofList

        let expected =
            CaseActKind.all
            |> List.filter (fun kind -> kind <> CaseActKind.Open)
            |> Set.ofList

        Expect.equal kinds expected "Every non-OPEN operation is exercised against an absent case"

        for index, act in List.indexed acts do
            let! outcome =
                execute actor (actRequest (100 + index) "MISSING" 1L act) |> Async.AwaitTask

            Expect.equal
                (refused outcome |> fst)
                "RESOURCE_UNAVAILABLE"
                "Missing case stays private"

        Expect.isFalse
            (DiagnosticCodecs.codes |> List.contains "CASE_NOT_FOUND")
            "Domain-only missing state has no public diagnostic"
    }

[<Tests>]
let tests =
    testList
        "public refusal reachability"
        [
            testCaseAsync
                "all diagnostics and fields have actual Application request witnesses"
                (completeVocabulary ())
            testCaseAsync "absent case execution preserves resource privacy" (missingCasePrivacy ())
        ]
