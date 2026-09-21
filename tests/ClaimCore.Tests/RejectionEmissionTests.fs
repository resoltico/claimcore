module ClaimCore.Tests.RejectionEmissionTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private token =
    RejectionDiagnostics.describe
    >> RejectionDiagnostics.identifier
    >> RejectionDiagnosticIds.token

let private rejected result =
    match result with
    | Error error -> Rejection.Domain error
    | Ok _ -> failtest "Expected a domain refusal."

let private wait (value: Task<'value>) = value.GetAwaiter().GetResult()

let private create () =
    let store = new CoreStore.Store()
    let recovery = new CoreRecoveryStore.Store()
    recovery.AttachClaimStore(store :> IClaimStore)
    CoreApi.create (store :> IClaimStore) (recovery :> IRecoveryStore) (businessTime today)

let private queryRejection outcome =
    match outcome with
    | QueryOutcome.Rejected rejection -> rejection
    | _ -> failtest "Expected an ordinary query refusal."

let private scalarReasons () =
    let cases =
        [
            "INPUT_TEXT_REQUIRED", { registration with ClaimantName = " " }
            "INPUT_SURROUNDING_WHITESPACE",
            { registration with
                ClaimantName = " secret "
            }
            "INPUT_MALFORMED_UNICODE",
            { registration with
                ClaimantName = String(char 0xD800, 1)
            }
            "INPUT_CONTROL_CHARACTERS",
            { registration with
                ClaimantName = "private\000input"
            }
            "INPUT_TEXT_TOO_LONG",
            { registration with
                ClaimantName = String.replicate 201 "😀"
            }
            "INPUT_DECIMAL_FORMAT",
            { registration with
                ClaimedAmount = "1e3"
            }
            "INPUT_CURRENCY_FORMAT",
            { registration with
                ClaimedCurrency = "eur"
            }
            "INPUT_CALENDAR_DATE_REQUIRED",
            { registration with
                IncidentDate = "2026-02-30"
            }
            "INPUT_DATE_ORDER",
            { registration with
                IncidentNotificationDate = "2026-07-01"
            }
        ]

    for expected, facts in cases do
        let result = Claim.decide today (request 0L (Command.Open facts)) None |> rejected
        Expect.equal (token result) expected "The owning rule supplies a typed cause"

let private privacy () =
    let secret = " DO-NOT-ECHO-CLAIMANT-RECORD "

    let result =
        Claim.decide
            today
            (request
                0L
                (Command.Open
                    { registration with
                        ClaimantName = secret
                    }))
            None
        |> rejected

    let wire = CliWireCodec.caseGet "case.get" (QueryOutcome.Rejected result)

    Expect.isFalse
        (Encoding.UTF8.GetString(wire.Bytes).Contains(secret.Trim()))
        "Submitted values never become diagnostic parameters"

    Expect.equal result.Field (Some "claimantName") "Only a declared field token is exposed"

let private ownedLimits () =
    let result =
        Claim.decide
            today
            (request
                0L
                (Command.Open
                    { registration with
                        ClaimantName = String.replicate 201 "😀"
                    }))
            None
        |> rejected

    let expected = FieldDefinitions.scalar "claimantName" |> ScalarRules.textConstraints

    Expect.equal
        (RejectionDiagnostics.describe result |> RejectionDiagnostics.values)
        [ "maximumCharacters", expected.MaximumCharacters ]
        "Rune limit comes from Domain metadata"

    let amount =
        Claim.decide
            today
            (request
                0L
                (Command.Open
                    { registration with
                        ClaimedAmount = "1e3"
                    }))
            None
        |> rejected

    Expect.equal
        (RejectionDiagnostics.describe amount |> RejectionDiagnostics.values)
        [ "maximumIntegerDigits", 18; "maximumFractionalDigits", 4 ]
        "Two distinct typed decimal limits"

let private queryReasons () =
    let core = create ()

    let limit =
        core.List({ AfterReference = None; Limit = 0 }, CancellationToken.None)
        |> wait
        |> queryRejection

    Expect.equal (token limit) "QUERY_PAGE_LIMIT_RANGE" "Not a generic INVALID_INPUT message"

    Expect.equal
        (RejectionDiagnostics.describe limit |> RejectionDiagnostics.values)
        [ "maximumPageSize", SemanticContract.current.MaximumPageSize ]
        "Core-owned page budget"

    let cursor =
        core.History(
            {
                CaseReference = "UNIT-001"
                AfterCursor = Some "not-a-cursor"
                Limit = 1
                Detail = HistoryDetail.Summary
            },
            CancellationToken.None
        )
        |> wait
        |> queryRejection

    Expect.equal (token cursor) "QUERY_HISTORY_CURSOR_INVALID" "Cursor grammar has its own cause"

    let operation =
        core.ObserveOperation(Guid.Empty, CancellationToken.None)
        |> wait
        |> queryRejection

    Expect.equal (token operation) "COMMAND_OPERATION_ID_REQUIRED" "Empty UUID cause"

let private requestReasons () =
    let command = request 0L (Command.Open registration)

    let empty =
        Claim.validateRequest
            { command with
                OperationId = Guid.Empty
            }
        |> rejected

    let revision =
        Claim.validateRequest { command with ExpectedVersion = -1L } |> rejected

    Expect.equal (token empty) "COMMAND_OPERATION_ID_REQUIRED" "Command ID admission"
    Expect.equal (token revision) "COMMAND_EXPECTED_REVISION_RANGE" "Revision admission"
    Expect.equal revision.Field (Some "expectedRevision") "Public field vocabulary"

    let future =
        { registration with
            IncidentNotificationDate = "2026-10-01"
        }

    let result = Claim.decide today (request 0L (Command.Open future)) None |> rejected

    Expect.equal
        (token result)
        "INPUT_FUTURE_DATE"
        "Installation business date remains authoritative"

let private binderReasons () =
    let draft: CommandDraft =
        {
            OperationId = Guid.Parse("20000000-0000-4000-8000-000000000001")
            CaseReference = "UNIT-001"
            ExpectedVersion = 0L
            Command = DraftCommand.Flat(CommandKind.Open, [ "undeclared-private-input", "secret" ])
        }

    let fields = Drafts.bind draft |> rejected

    Expect.equal
        (token fields)
        "INPUT_EXACT_FIELDS_REQUIRED"
        "No caller field names become identity"

    let grouped =
        Drafts.bind
            { draft with
                Command = DraftCommand.Flat(CommandKind.CorrectCase, [])
            }
        |> rejected

    Expect.equal
        (token grouped)
        "COMMAND_GROUPED_CORRECTION_REQUIRED"
        "Separate grouped-input cause"

    let registration =
        Drafts.bind
            { draft with
                Command =
                    DraftCommand.Correction(
                        CorrectionDraftAction.Clear,
                        CorrectionDraftAction.Keep,
                        CorrectionDraftAction.Keep
                    )
            }
        |> rejected

    Expect.equal
        (token registration)
        "CORRECTION_REGISTRATION_CLEAR_FORBIDDEN"
        "Not a generic replacement message"

let private stateReasons () =
    let claim = opened ()
    let before = Claim.view claim
    let decisionRequired = apply (Command.RecordPayment "2026-08-20") claim |> rejected
    Expect.equal (token decisionRequired) "CASE_DECISION_REQUIRED" "State rule identity"
    Expect.equal (Claim.view claim) before "A rejection does not mutate accepted facts"
    let closed = apply Command.Close claim |> accepted

    let rejectedAmendment =
        apply (Command.AmendRegistration registration) closed |> rejected

    Expect.equal (token rejectedAmendment) "CASE_CLOSED" "State guard remains authoritative"

    let noChanges =
        apply
            (Command.CorrectCase
                {
                    Registration = RegistrationCorrection.Keep
                    Decision = DecisionCorrection.Keep
                    Payment = PaymentCorrection.Keep
                })
            claim
        |> rejected

    Expect.equal (token noChanges) "CORRECTION_NO_CHANGES" "Factual no-op is a specific cause"

let tests =
    testList
        "rejection diagnostic emission"
        [
            testCase
                "domain validation emits specific scalar reasons rather than English"
                scalarReasons
            testCase "authored private values are not echoed into diagnostics" privacy
            testCase "parameter values come from owned scalar constraints" ownedLimits
            testCase "query admission preserves page cursor and operation causes" queryReasons
            testCase
                "request and business-date admission retain distinct typed causes"
                requestReasons
            testCase "form binding retains exact-field and correction causes" binderReasons
            testCase "state refusals keep typed reasons and accepted data unchanged" stateReasons
        ]
