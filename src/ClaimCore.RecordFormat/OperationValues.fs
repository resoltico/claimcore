namespace ClaimCore.RecordFormat

open ClaimCore.Domain

[<NoComparison>]
type CaseOperationValues =
    {
        Command: CaseCommand
        Decision: CaseDecision
    }

type WorkOperationValues =
    {
        Update: ValidCaseWorkUpdate
        After: CaseWorkSnapshot
    }

type NoteOperationValues = { Command: NoteCommand; After: Note }

type ReminderOperationValues =
    {
        Command: ReminderCommand
        Effects: ReminderEffects
    }

/// An absent profile in a newly decided value is valid only for a public-only people act.
type PeopleOperationValues = { Profile: ActorProfile option }

[<RequireQualifiedAccess; NoComparison>]
type OperationValues =
    | Case of CaseOperationValues
    | Work of WorkOperationValues
    | Note of NoteOperationValues
    | Reminder of ReminderOperationValues
    | People of PeopleOperationValues

[<RequireQualifiedAccess>]
type RetainedProfile =
    | NotRequired
    | Available of ActorProfile
    | Unavailable

/// People public effects remain interpretable when only its profile compartment is erased.
[<RequireQualifiedAccess; NoComparison>]
type RetainedOperationValues =
    | Case of CaseOperationValues option
    | Work of WorkOperationValues option
    | Note of NoteOperationValues option
    | Reminder of ReminderOperationValues option
    | People of RetainedProfile

[<RequireQualifiedAccess>]
type OperationContentProblem =
    | Root of OperationRootProblem
    | Structure of OperationStructureProblem
    | Values
    | Compartment of CompartmentProblem
    | PrivateFormat of DecodeProblem
    | NonCanonicalPrivateContent
    | MissingContent
