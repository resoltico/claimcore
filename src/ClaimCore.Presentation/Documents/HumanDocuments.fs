namespace ClaimCore.Presentation

open System.Text.Json
open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type HumanDisclosure =
    | Current
    | Partial
    | Full
    | Summary
    | Withheld
    | Erased
    | None

[<RequireQualifiedAccess>]
type ObservationBasis =
    | Snapshot of WorkDateBasis
    | Unavailable

[<RequireQualifiedAccess>]
type ConfirmationTarget =
    | CaseReference of CaseReference
    | Operation of System.Guid
    | Principal of confirmationText: string

module ConfirmationTarget =
    // A quoted JSON spelling contains at most six characters per UTF-16 code unit plus quotes.
    let maximumPrincipalTextCharacters =
        6 * PrincipalKey.MaximumHumanSubjectCharacters + 2

    let principal (subject: string) =
        // A leading quote distinguishes the quoted form from every verbatim spelling.
        let needsQuoting =
            subject.StartsWith('"') || not (TextDisplay.warning subject).CodePoints.IsEmpty

        if needsQuoting then
            let quote = string '"'
            ConfirmationTarget.Principal(quote + JsonEncodedText.Encode(subject).ToString() + quote)
        else
            ConfirmationTarget.Principal subject

    let text =
        function
        | ConfirmationTarget.CaseReference reference -> CaseReference.format reference
        | ConfirmationTarget.Operation id -> id.ToString("D")
        | ConfirmationTarget.Principal text -> text

    let requiresExactMatch consequence target =
        match target with
        | ConfirmationTarget.Principal _ -> true
        | _ -> consequence = Consequence.Irreversible

type RequesterSubject =
    {
        State: RequesterKind
        InstallationId: System.Guid option
        ActorId: ActorId option
    }

[<RequireQualifiedAccess>]
type DocumentSubject =
    | Case of CaseId * revision: Revision * workRevision: Revision
    | Person of ActorId
    | Requester of RequesterSubject
    | List
    | Review of ConfirmationTarget * Consequence
    | Receipt of OperationId
    | Operation of System.Guid

type AttachmentObservation = { CaseId: CaseId; Sequence: Sequence }

type HumanDocument =
    {
        Subject: DocumentSubject
        Screen: Screen
        Actions: Affordance list
        Links: NamedLink list
        NextCursor: string option
        Basis: ObservationBasis
        Disclosure: HumanDisclosure
        History: HistoryAccess option
        AttachmentObservation: AttachmentObservation option
    }

type HumanReview =
    {
        Document: HumanDocument
        Presentation: ReviewPresentation
    }

type HumanInputIssue =
    {
        Input: TaskInputId
        Row: TaskRowTarget option
        Message: PresentedMessage
    }

[<RequireQualifiedAccess>]
type HumanRejection =
    | Input of HumanAdvice * HumanInputIssue list
    | Conflict of HumanAdvice * actualRevision: Revision * HumanDocument
    | Refused of HumanAdvice

[<RequireQualifiedAccess>]
type HumanReadOutcome =
    | Succeeded of HumanDocument
    | Rejected of HumanRejection
    | Failed of CoreFault

type HumanReadReply = { Outcome: HumanReadOutcome }

/// The transport envelope owns correlation; each success has a single HUMAN document.
type HumanOperationReply<'outcome> =
    {
        OperationId: System.Guid
        Outcome: 'outcome
    }

[<RequireQualifiedAccess>]
type HumanPreviewResult =
    | Reviewed of HumanReview
    | AlreadyAccepted of HumanDocument
    | Rejected of HumanRejection
    | Failed of CoreFault

[<RequireQualifiedAccess>]
type HumanExecuteResult =
    | Accepted of HumanDocument
    | AlreadyAccepted of HumanDocument
    | Rejected of HumanRejection
    | Failed of CoreFault
    | OutcomeUnknown

[<RequireQualifiedAccess>]
type HumanLookupResult =
    | Accepted of HumanDocument * byRequester: bool
    | Pending of finalAt: System.DateTimeOffset * HumanDocument
    | NotAccepted of HumanDocument
    | Rejected of HumanRejection
    | Failed of CoreFault

module HumanDocument =
    let create
        subject
        screen
        actions
        links
        cursor
        basis
        disclosure
        (history: HistoryAccess option)
        =
        if not (List.forall Affordance.valid actions) then
            invalidArg (nameof actions) "A document requires valid declared input controls."

        match subject with
        | DocumentSubject.Requester identity when identity.State = RequesterKind.Active ->
            if
                identity.InstallationId.IsNone
                || identity.ActorId.IsNone
                || basis = ObservationBasis.Unavailable
            then
                invalidArg
                    (nameof subject)
                    "An active requester requires its authenticated identity and observation basis."
        | _ -> ()

        let controls = actions @ Screen.affordances screen

        if
            List.contains
                disclosure
                [ HumanDisclosure.Withheld; HumanDisclosure.Erased; HumanDisclosure.None ]
            && not controls.IsEmpty
        then
            invalidArg (nameof disclosure) "Undisclosed resources cannot retain authoring controls."

        if
            history.IsSome
            && (match subject with
                | DocumentSubject.Case _ -> false
                | _ -> true)
        then
            invalidArg (nameof history) "History access belongs only to a case document."

        {
            Subject = subject
            Screen = screen
            Actions = actions
            Links = links
            NextCursor = cursor
            Basis = basis
            Disclosure = disclosure
            History = history
            AttachmentObservation = None
        }

    let observeAttachments caseId sequence document =
        { document with
            AttachmentObservation = Some { CaseId = caseId; Sequence = sequence }
        }

    let empty disclosure =
        create
            DocumentSubject.List
            (Screen.create [])
            []
            []
            None
            ObservationBasis.Unavailable
            disclosure
            None

    let internal snapshot basis screen subject actions links cursor disclosure history =
        create
            subject
            screen
            actions
            links
            cursor
            (ObservationBasis.Snapshot basis)
            disclosure
            history

    let internal listPage basis screen cursor =
        snapshot basis screen DocumentSubject.List [] [] cursor HumanDisclosure.Current None

module HumanReadReply =
    let classify (reply: HumanReadReply) =
        match reply.Outcome with
        | HumanReadOutcome.Succeeded _ -> ReplyKind.Succeeded
        | HumanReadOutcome.Rejected _ -> ReplyKind.Rejected
        | HumanReadOutcome.Failed _ -> ReplyKind.Failed

/// Operational uncertainty has no ledger identity or automatic retry meaning.
[<RequireQualifiedAccess>]
type HumanSessionTerminationResult =
    | Succeeded of self: bool * HumanDocument
    | Rejected of HumanRejection
    | Failed of CoreFault
    | OutcomeUnknown of HumanDocument
