namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// Retained structural metadata only. Actor disclosure remains an Application responsibility.
module OperationPublicCodecs =
    open Codec
    open WorkRecordCodecs

    let private ordinal = integer 0L (int64 System.Int32.MaxValue) |> map int int64
    let private zero = integer 0L 0L |> map (fun _ -> ()) (fun () -> 0L)
    let private noContent = Codec.nullValue
    let private reference = scalar CaseReference.parse CaseReference.format

    let private principal =
        CanonicalInputs.principal
        |> interpret
            (fun path input ->
                PrincipalKey.ofKind input.Kind input.Issuer input.Subject
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid retained principal."
                    }))
            PrincipalKey.toInput

    let private scope =
        tagged
            "OperationGrantScope"
            [
                unitVariant "INSTALLATION" Scope.Installation
                variant
                    "CASE"
                    Scope.Case
                    (function
                    | Scope.Case id -> Some id
                    | _ -> None)
                    (record id |> field "caseId" id caseId)
            ]

    let private grantFields =
        (record (fun role scope -> ({ Role = role; Scope = scope }: ActorGrant))
        : Fields<ActorGrant, _>)
        |> field "role" _.Role RecordCodecs.role
        |> field "scope" _.Scope scope

    let private grant = grantFields |> build
    let private profile = record id |> field "profileCompartment" id ordinal

    let private peopleAct =
        tagged
            "OperationPeopleAct"
            [
                variant
                    (PeopleActKind.token PeopleActKind.Register)
                    PeopleAct.Register
                    (function
                    | PeopleAct.Register value -> Some value
                    | _ -> None)
                    profile
                variant
                    (PeopleActKind.token PeopleActKind.Admit)
                    PeopleAct.Admit
                    (function
                    | PeopleAct.Admit value -> Some value
                    | _ -> None)
                    ((record (fun profile grants ->
                        {
                            PeopleAdmission.Profile = profile
                            Grants = grants
                        })
                     : Fields<PeopleAdmission<ActorGrant, int>, _>)
                     |> field "profileCompartment" _.Profile ordinal
                     |> field "grants" _.Grants (boundedList 1 PeopleAdmission.MaximumGrants grant))
                variant
                    (PeopleActKind.token PeopleActKind.ProvisionOwner)
                    (fun (profile, roles) -> PeopleAct.ProvisionOwner(profile, roles))
                    (function
                    | PeopleAct.ProvisionOwner(profile, roles) -> Some(profile, roles)
                    | _ -> None)
                    (record (fun profile roles -> profile, roles)
                     |> field "profileCompartment" fst ordinal
                     |> field
                         "roles"
                         snd
                         (boundedList 0 People.initialOwnerRoles.Length RecordCodecs.role))
                variant
                    (PeopleActKind.token PeopleActKind.SetProfile)
                    PeopleAct.SetProfile
                    (function
                    | PeopleAct.SetProfile value -> Some value
                    | _ -> None)
                    profile
                unitVariant (PeopleActKind.token PeopleActKind.EraseProfile) PeopleAct.EraseProfile
                variant
                    (PeopleActKind.token PeopleActKind.Grant)
                    PeopleAct.Grant
                    (function
                    | PeopleAct.Grant value -> Some value
                    | _ -> None)
                    grantFields
                variant
                    (PeopleActKind.token PeopleActKind.Revoke)
                    PeopleAct.Revoke
                    (function
                    | PeopleAct.Revoke value -> Some value
                    | _ -> None)
                    grantFields
                unitVariant (PeopleActKind.token PeopleActKind.Enable) PeopleAct.Enable
                unitVariant (PeopleActKind.token PeopleActKind.Disable) PeopleAct.Disable
            ]

    let private case =
        (record (fun parent reference expected act selected content ->
            let command =
                {
                    CaseCommandSkeleton.CaseId = parent
                    Reference = reference
                    ExpectedRevision = expected
                    Act = act
                    SelectedPayment = selected
                }

            if content = CaseCommandSkeleton.compartment command then
                Ok command
            else
                Error "Invalid case compartment reference.")
        : Fields<CaseCommandSkeleton, _>)
        |> field "caseId" _.CaseId caseId
        |> field "reference" _.Reference reference
        |> field "expectedRevision" _.ExpectedRevision (revision 0L)
        |> field "act" _.Act (VocabularyCodec.create CaseActKind.vocabulary)
        |> field "selectedPayment" _.SelectedPayment (nullable operationId)
        |> field "compartment" CaseCommandSkeleton.compartment (nullable ordinal)
        |> fixedField "kind" "CASE"
        |> buildResult (fun path reason -> { Path = path; Reason = reason })

    let private people =
        (record (fun target principal act ->
            {
                PeopleCommandSkeleton.TargetActorId = target
                Principal = principal
                Act = act
            })
        : Fields<PeopleCommandSkeleton, _>)
        |> field "targetActorId" _.TargetActorId actorId
        |> field "principal" _.Principal principal
        |> field "act" _.Act peopleAct
        |> fixedField "kind" "PEOPLE"
        |> build

    let private work =
        record (fun parent expected () -> parent, expected)
        |> field "caseId" fst caseId
        |> field "expectedRevision" snd (revision 0L)
        |> field "compartment" (fun _ -> ()) zero
        |> fixedField "kind" "WORK"
        |> build

    let private reminder =
        (record (fun parent act target expected () ->
            {
                ReminderCommandSkeleton.CaseId = parent
                Act = act
                ReminderId = target
                ExpectedRevision = expected
            })
        : Fields<ReminderCommandSkeleton, _>)
        |> field "caseId" _.CaseId caseId
        |> field "act" _.Act (VocabularyCodec.create ReminderActKind.vocabulary)
        |> field "reminderId" _.ReminderId (nullable reminderId)
        |> field "expectedRevision" _.ExpectedRevision (nullable (revision 0L))
        |> field "compartment" (fun _ -> ()) zero
        |> fixedField "kind" "REMINDER"
        |> build

    let private note =
        taggedOn
            "act"
            "OperationNoteCommand"
            [
                variant
                    (NoteActKind.token NoteActKind.AddNote)
                    NoteCommandSkeleton.Add
                    (function
                    | NoteCommandSkeleton.Add parent -> Some parent
                    | _ -> None)
                    (record (fun parent () -> parent)
                     |> field "caseId" id caseId
                     |> field "compartment" (fun _ -> ()) zero
                     |> fixedField "kind" "NOTE")
                variant
                    (NoteActKind.token NoteActKind.RemoveNote)
                    (fun (parent, note) -> NoteCommandSkeleton.Remove(parent, note))
                    (function
                    | NoteCommandSkeleton.Remove(parent, note) -> Some(parent, note)
                    | _ -> None)
                    (record (fun parent note () -> parent, note)
                     |> field "caseId" fst caseId
                     |> field "noteId" snd (identifier NoteId.create NoteId.value)
                     |> field "compartment" (fun _ -> ()) noContent
                     |> fixedField "kind" "NOTE")
            ]

    let command =
        choice
            "OperationCommandSkeleton"
            [
                branch
                    CommandSkeleton.Case
                    (function
                    | CommandSkeleton.Case value -> Some value
                    | _ -> None)
                    case
                branch
                    CommandSkeleton.People
                    (function
                    | CommandSkeleton.People value -> Some value
                    | _ -> None)
                    people
                branch
                    CommandSkeleton.Work
                    (function
                    | CommandSkeleton.Work(parent, revision) -> Some(parent, revision)
                    | _ -> None)
                    work
                branch
                    CommandSkeleton.Note
                    (function
                    | CommandSkeleton.Note value -> Some value
                    | _ -> None)
                    note
                branch
                    CommandSkeleton.Reminder
                    (function
                    | CommandSkeleton.Reminder value -> Some value
                    | _ -> None)
                    reminder
            ]

    let private payment =
        tagged
            "OperationPaymentReference"
            [
                variant
                    "RECORDED"
                    PaymentReference.Recorded
                    (function
                    | PaymentReference.Recorded value -> Some value
                    | _ -> None)
                    (record id |> field "operationId" id operationId)
                variant
                    "REMOVED"
                    PaymentReference.Removed
                    (function
                    | PaymentReference.Removed value -> Some value
                    | _ -> None)
                    (record id |> field "operationId" id operationId)
            ]

    let private peopleEffect =
        tagged
            "OperationPeopleEffect"
            [
                variant
                    (PeopleActKind.token PeopleActKind.ProvisionOwner)
                    PeopleEffectDescriptor.ProvisionOwner
                    (function
                    | PeopleEffectDescriptor.ProvisionOwner value -> Some value
                    | _ -> None)
                    (record id |> field "principal" id principal)
                variant
                    (PeopleActKind.token PeopleActKind.Register)
                    PeopleEffectDescriptor.Register
                    (function
                    | PeopleEffectDescriptor.Register value -> Some value
                    | _ -> None)
                    (record id |> field "principal" id principal)
                variant
                    (PeopleActKind.token PeopleActKind.SetProfile)
                    PeopleEffectDescriptor.SetProfile
                    (function
                    | PeopleEffectDescriptor.SetProfile value -> Some value
                    | _ -> None)
                    profile
                unitVariant
                    (PeopleActKind.token PeopleActKind.EraseProfile)
                    PeopleEffectDescriptor.EraseProfile
                variant
                    (PeopleActKind.token PeopleActKind.Grant)
                    PeopleEffectDescriptor.Grant
                    (function
                    | PeopleEffectDescriptor.Grant value -> Some value
                    | _ -> None)
                    grantFields
                variant
                    (PeopleActKind.token PeopleActKind.Revoke)
                    PeopleEffectDescriptor.Revoke
                    (function
                    | PeopleEffectDescriptor.Revoke value -> Some value
                    | _ -> None)
                    grantFields
                unitVariant (PeopleActKind.token PeopleActKind.Enable) PeopleEffectDescriptor.Enable
                unitVariant
                    (PeopleActKind.token PeopleActKind.Disable)
                    PeopleEffectDescriptor.Disable
            ]

    let effect =
        tagged
            "OperationEffectDescriptor"
            [
                variant
                    "CASE"
                    (fun (parent, revision, act, reference, payment) ->
                        EffectDescriptor.Case(parent, revision, act, reference, payment))
                    (function
                    | EffectDescriptor.Case(parent, revision, act, reference, payment) ->
                        Some(parent, revision, act, reference, payment)
                    | _ -> None)
                    (record (fun parent revision act reference payment ->
                        parent, revision, act, reference, payment)
                     |> field "caseId" (fun (a, _, _, _, _) -> a) caseId
                     |> field "revision" (fun (_, a, _, _, _) -> a) (revision 1L)
                     |> field
                         "act"
                         (fun (_, _, a, _, _) -> a)
                         (VocabularyCodec.create CaseActKind.vocabulary)
                     |> field "creationReference" (fun (_, _, _, a, _) -> a) (nullable reference)
                     |> field "payment" (fun (_, _, _, _, a) -> a) (nullable payment))
                variant
                    "PEOPLE"
                    (fun (target, change) -> EffectDescriptor.People(target, change))
                    (function
                    | EffectDescriptor.People(target, change) -> Some(target, change)
                    | _ -> None)
                    (record (fun target change -> target, change)
                     |> field "targetActorId" fst actorId
                     |> field "change" snd peopleEffect)
                variant
                    "WORK"
                    (fun (parent, revision) -> EffectDescriptor.Work(parent, revision))
                    (function
                    | EffectDescriptor.Work(parent, revision) -> Some(parent, revision)
                    | _ -> None)
                    (record (fun parent revision -> parent, revision)
                     |> field "caseId" fst caseId
                     |> field "revision" snd (revision 1L))
                variant
                    "NOTE_ADDED"
                    (fun (parent, note) -> EffectDescriptor.NoteAdded(parent, note))
                    (function
                    | EffectDescriptor.NoteAdded(parent, note) -> Some(parent, note)
                    | _ -> None)
                    (record (fun parent note -> parent, note)
                     |> field "caseId" fst caseId
                     |> field "noteId" snd (identifier NoteId.create NoteId.value))
                variant
                    "NOTE_REMOVED"
                    (fun (parent, note) -> EffectDescriptor.NoteRemoved(parent, note))
                    (function
                    | EffectDescriptor.NoteRemoved(parent, note) -> Some(parent, note)
                    | _ -> None)
                    (record (fun parent note -> parent, note)
                     |> field "caseId" fst caseId
                     |> field "noteId" snd (identifier NoteId.create NoteId.value))
                variant
                    "REMINDER_CREATED"
                    (fun (parent, reminder, revision) ->
                        EffectDescriptor.ReminderCreated(parent, reminder, revision))
                    (function
                    | EffectDescriptor.ReminderCreated(parent, reminder, revision) ->
                        Some(parent, reminder, revision)
                    | _ -> None)
                    (record (fun parent reminder revision -> parent, reminder, revision)
                     |> field "caseId" (fun (a, _, _) -> a) caseId
                     |> field "reminderId" (fun (_, a, _) -> a) reminderId
                     |> field "revision" (fun (_, _, a) -> a) (revision 1L))
                variant
                    "REMINDER_CHANGED"
                    (fun (parent, reminder, revision, act) ->
                        EffectDescriptor.ReminderChanged(parent, reminder, revision, act))
                    (function
                    | EffectDescriptor.ReminderChanged(parent, reminder, revision, act) ->
                        Some(parent, reminder, revision, act)
                    | _ -> None)
                    (record (fun parent reminder revision act -> parent, reminder, revision, act)
                     |> field "caseId" (fun (a, _, _, _) -> a) caseId
                     |> field "reminderId" (fun (_, a, _, _) -> a) reminderId
                     |> field "revision" (fun (_, _, a, _) -> a) (revision 1L)
                     |> field
                         "act"
                         (fun (_, _, _, a) -> a)
                         (VocabularyCodec.create ReminderActKind.vocabulary))
            ]
