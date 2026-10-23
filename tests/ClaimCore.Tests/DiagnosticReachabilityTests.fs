module ClaimCore.Tests.DiagnosticReachabilityTests

open ClaimCore.Contract

open System
open System.Threading
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.Tests.ApplicationFixtures

let private ct = CancellationToken.None

let private decision: Decision =
    {
        PaymentDecisionDate = "2026-08-15"
        PayableAmount = "750.00"
        PayableCurrency = "EUR"
    }

let private keep: Correction =
    {
        Registration = RegistrationGroup.Keep
        Decision = DecisionGroup.Keep
        Payment = PaymentGroup.Keep
    }

let private correction value = (CaseAct.CorrectCase value)

let private payment = (CaseAct.RecordPayment { PaymentDate = "2026-08-20" })

let private decided = (CaseAct.Decide decision)
let private closed = CaseAct.Close
let private voided = (CaseAct.Void "Synthetic reason")

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
    | Some action ->
        Expect.equal (string reason.Action) action "Captured baseline action is preserved."
    | None ->
        Expect.contains
            [ Some "principal.issuer"; Some "principal.subject" ]
            reason.Field
            "Only principal targets extend the captured baseline."

        Expect.contains
            [ "INPUT_TEXT_REQUIRED"; "INPUT_PRINCIPAL_FORMAT" ]
            code
            "Only principal admission extends the captured baseline."

        Expect.equal
            reason.Action
            RecommendedAction.CorrectInput
            "Principal admission is repairable input."

let private diagnostic reason =
    let identifier = (Refusals.row reason).Code

    let reply = OutcomeMapping.caseGet (ReadReply.Rejected reason)
    let encoded = ClaimCore.Contract.Protocol.Replies.encode reply

    Expect.equal
        (ClaimCore.Contract.Protocol.Replies.decode
            ClaimCore.Contract.Protocol.Endpoint.CaseGet
            encoded)
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
    (actor: IActorClaimsCore).Execute(OperationInput.Case pending, ct)

let private opening values =
    { openRequest 10 "REACH-1" with
        Act = (CaseAct.Open values)
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
                ClaimantName = "Synthetic\u200bParty"
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
            (CaseAct.Decide
                { decision with
                    PaymentDecisionDate = "bad"
                })
        request 10 "REACH-1" 1L (CaseAct.Decide { decision with PayableAmount = "bad" })
        request
            10
            "REACH-1"
            1L
            (CaseAct.Decide
                { decision with
                    PayableCurrency = "bad"
                })
        request 10 "REACH-1" 1L (CaseAct.RecordPayment { PaymentDate = "bad" })
        actRequest 10 "REACH-1" 1L ((CaseAct.Void ""))
    ]

let private scalarRequests () =
    textRequests ()
    @ registrationRequests ()
    @ controlRequests ()
    @ progressRequests ()

let private scalarEvidence () =
    task {
        let store, fixture = fixture ()
        use _owned = store
        let! actor = caseWorker fixture
        let results = ResizeArray<_>()

        for pending in scalarRequests () do
            let! outcome = execute actor pending
            results.Add(refused outcome)

        return List.ofSeq results
    }

let private stateScenarios () =
    [
        [], (CaseAct.Open registration)
        [], CaseAct.Reopen
        [ closed ], CaseAct.Close
        [ closed ], decided
        [ decided ], (CaseAct.AmendRegistration registration)
        [ decided ],
        correction
            { keep with
                Registration = RegistrationGroup.Replace registration
            }
        [],
        correction
            { keep with
                Decision = DecisionGroup.Clear
            }
        [], payment
        [], CaseAct.ClearPayment
        [ decided; payment ], payment
        [ decided; payment ], CaseAct.WithdrawDecision
        [ (CaseAct.Decide { decision with PayableAmount = "0" }) ], payment
        [ voided ], closed
        [ voided ], voided
        [], (CaseAct.Reinstate "Synthetic reason")
        [ CaseAct.Erase ], closed
        [ decided; payment ],
        correction
            { keep with
                Decision = DecisionGroup.Clear
            }
        [ decided; payment ],
        correction
            { keep with
                Decision = DecisionGroup.Replace decision
            }
    ]

let private stateEvidence (setup, final) =
    task {
        let store, fixture = fixture ()
        use _owned = store
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
        let! limit = (actor: IActorClaimsCore).List({ AfterCursor = None; Limit = 0 }, ct)
        let! cursor = actor.List({ AfterCursor = Some "bad"; Limit = 1 }, ct)
        let! missing = actor.Get({ CaseReference = "MISSING" }, ct)
        return [ queryRefused limit; queryRefused cursor; queryRefused missing ]
    }

let private authorityEvidence actor =
    task {
        let service =
            PrincipalKey.service "https://issuer.example/" "synthetic"
            |> Result.defaultWith failtest

        let send n target act =
            (actor: IActorClaimsCore)
                .Execute(OperationInput.Authority(authorityRequest (id n) target act), ct)

        let! registered = send 30 owner AdmittedAuthorityAct.RegisterActor
        let! unknown = send 31 service (AdmittedAuthorityAct.SetEnabled false)
        let! last = send 32 owner (AdmittedAuthorityAct.SetEnabled false)
        let! admitted = send 33 service AdmittedAuthorityAct.RegisterActor
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
        let! revision = execute actor (request 11 "REACH-1" 0L CaseAct.Close)

        let reader =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith failtest

        let! registered =
            actor.Execute(
                OperationInput.Authority(
                    authorityRequest (id 35) reader AdmittedAuthorityAct.RegisterActor
                ),
                ct
            )

        acceptedAuthority registered

        let! granted =
            grant actor (id 36) reader Role.CaseReader GrantTarget.Installation true

        acceptedAuthority granted
        return [ refused conflict; refused revision ], reader
    }

let private principalEvidence (actor: IActorClaimsCore) =
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
            let request: ClaimCore.Contract.AuthorityRequest =
                {
                    OperationId = id (60 + index)
                    Target =
                        {
                            Kind = PrincipalKinds.Human
                            Issuer = issuer
                            Subject = subject
                        }
                    Act = ClaimCore.Contract.AuthorityAct.RegisterActor
                }

            let! outcome = actor.Execute(OperationInput.Authority request, ct)
            evidence.Add(refused outcome)

        return List.ofSeq evidence
    }

let private boundaryEvidence () =
    task {
        let store, fixture = fixture ()
        use _owned = store
        let! actor = caseWorker fixture
        let! identity, reader = identityEvidence actor

        let! denied = execute (core fixture reader) (request 37 "REACH-1" 1L CaseAct.Close)

        let! queries = queryEvidence actor
        let! authority = authorityEvidence actor
        let! principals = principalEvidence actor
        let original = store.Cases.Head.Snapshot

        store.ReplaceCaseSnapshot(
            "REACH-1",
            { original with
                Revision = Revision.Maximum
            }
        )

        let! revision = execute actor (request 40 "REACH-1" Revision.Maximum CaseAct.Close)

        store.ReplaceCaseSnapshot("REACH-1", original)

        store.HeadOverride <-
            Some
                {
                    Sequence = Revision.Maximum
                    EntryHash = Array.zeroCreate 32
                }

        let! ledger = execute actor (request 41 "REACH-1" 1L CaseAct.Close)

        return
            identity
            @ [ refused denied; refused revision; refused ledger ]
            @ queries
            @ authority
            @ principals
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
            (Set.ofList Refusals.codes)
            (Set.add "INPUT_PRINCIPAL_FORMAT" baselineCodes)
            "The code table preserves every captured refusal with the adopted principal extension."

        let produced = Set.ofList evidence

        Expect.isEmpty
            (Set.difference (baseline.Value.Keys |> Set.ofSeq) produced |> Set.toList)
            "Every captured code and target still has a producing request."

        let expected = Refusals.codes |> Set.ofList

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
            (InputTargets.all |> List.map snd) @ [ "limit"; "cursor" ] |> Set.ofList

        Expect.equal
            fields
            expectedFields
            "Every public field token has an Application request witness"
    }

let private missingCasePrivacy () =
    async {
        let store, fixture = fixture ()
        use _owned = store
        let! actor = caseWorker fixture |> Async.AwaitTask

        let acts =
            stateScenarios ()
            |> List.collect (fun (setup, final) -> final :: setup)
            |> List.filter (fun act ->
                CaseActs.kind act <> CaseActKind.Business BusinessActKind.Open)

        let kinds = acts |> List.map CaseActs.kind |> Set.ofList

        let expected =
            CommandDefinitions.all
            |> List.map _.Kind
            |> List.filter (fun kind -> kind <> CaseActKind.Business BusinessActKind.Open)
            |> Set.ofList

        Expect.equal kinds expected "Every non-OPEN operation is exercised against an absent case"

        for index, act in List.indexed acts do
            let! outcome =
                execute actor (actRequest (100 + index) "MISSING" 1L act) |> Async.AwaitTask

            Expect.equal
                (refused outcome |> fst)
                "ACCESS_RESOURCE_UNAVAILABLE"
                "Missing case stays private"

        Expect.isFalse
            (Refusals.codes |> List.contains "CASE_NOT_FOUND")
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
