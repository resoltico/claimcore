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

    let private authorityAct =
        tagged
            "OperationAuthorityAct"
            [
                variant
                    (AuthorityKind.token AuthorityKind.Register)
                    AuthorityAct.Register
                    (function
                    | AuthorityAct.Register value -> Some value
                    | _ -> None)
                    profile
                variant
                    (AuthorityKind.token AuthorityKind.Admit)
                    AuthorityAct.Admit
                    (function
                    | AuthorityAct.Admit value -> Some value
                    | _ -> None)
                    ((record (fun profile grants ->
                        {
                            AuthorityAdmission.Profile = profile
                            Grants = grants
                        })
                     : Fields<AuthorityAdmission<ActorGrant, int>, _>)
                     |> field "profileCompartment" _.Profile ordinal
                     |> field
                         "grants"
                         _.Grants
                         (boundedList 1 AuthorityAdmission.MaximumGrants grant))
                variant
                    (AuthorityKind.token AuthorityKind.ProvisionOwner)
                    (fun (profile, roles) -> AuthorityAct.ProvisionOwner(profile, roles))
                    (function
                    | AuthorityAct.ProvisionOwner(profile, roles) -> Some(profile, roles)
                    | _ -> None)
                    (record (fun profile roles -> profile, roles)
                     |> field "profileCompartment" fst ordinal
                     |> field
                         "roles"
                         snd
                         (boundedList 0 Authority.initialOwnerRoles.Length RecordCodecs.role))
                variant
                    (AuthorityKind.token AuthorityKind.SetProfile)
                    AuthorityAct.SetProfile
                    (function
                    | AuthorityAct.SetProfile value -> Some value
                    | _ -> None)
                    profile
                unitVariant
                    (AuthorityKind.token AuthorityKind.EraseProfile)
                    AuthorityAct.EraseProfile
                variant
                    (AuthorityKind.token AuthorityKind.Grant)
                    AuthorityAct.Grant
                    (function
                    | AuthorityAct.Grant value -> Some value
                    | _ -> None)
                    grantFields
                variant
                    (AuthorityKind.token AuthorityKind.Revoke)
                    AuthorityAct.Revoke
                    (function
                    | AuthorityAct.Revoke value -> Some value
                    | _ -> None)
                    grantFields
                unitVariant (AuthorityKind.token AuthorityKind.Enable) AuthorityAct.Enable
                unitVariant (AuthorityKind.token AuthorityKind.Disable) AuthorityAct.Disable
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

    let private authority =
        (record (fun target principal act ->
            {
                AuthorityCommandSkeleton.TargetActorId = target
                Principal = principal
                Act = act
            })
        : Fields<AuthorityCommandSkeleton, _>)
        |> field "targetActorId" _.TargetActorId actorId
        |> field "principal" _.Principal principal
        |> field "act" _.Act authorityAct
        |> fixedField "kind" "AUTHORITY"
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
                    CommandSkeleton.Authority
                    (function
                    | CommandSkeleton.Authority value -> Some value
                    | _ -> None)
                    authority
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

    let private authorityEffect =
        tagged
            "OperationAuthorityEffect"
            [
                variant
                    (AuthorityKind.token AuthorityKind.ProvisionOwner)
                    AuthorityEffectDescriptor.ProvisionOwner
                    (function
                    | AuthorityEffectDescriptor.ProvisionOwner value -> Some value
                    | _ -> None)
                    (record id |> field "principal" id principal)
                variant
                    (AuthorityKind.token AuthorityKind.Register)
                    AuthorityEffectDescriptor.Register
                    (function
                    | AuthorityEffectDescriptor.Register value -> Some value
                    | _ -> None)
                    (record id |> field "principal" id principal)
                variant
                    (AuthorityKind.token AuthorityKind.SetProfile)
                    AuthorityEffectDescriptor.SetProfile
                    (function
                    | AuthorityEffectDescriptor.SetProfile value -> Some value
                    | _ -> None)
                    profile
                unitVariant
                    (AuthorityKind.token AuthorityKind.EraseProfile)
                    AuthorityEffectDescriptor.EraseProfile
                variant
                    (AuthorityKind.token AuthorityKind.Grant)
                    AuthorityEffectDescriptor.Grant
                    (function
                    | AuthorityEffectDescriptor.Grant value -> Some value
                    | _ -> None)
                    grantFields
                variant
                    (AuthorityKind.token AuthorityKind.Revoke)
                    AuthorityEffectDescriptor.Revoke
                    (function
                    | AuthorityEffectDescriptor.Revoke value -> Some value
                    | _ -> None)
                    grantFields
                unitVariant
                    (AuthorityKind.token AuthorityKind.Enable)
                    AuthorityEffectDescriptor.Enable
                unitVariant
                    (AuthorityKind.token AuthorityKind.Disable)
                    AuthorityEffectDescriptor.Disable
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
                    "AUTHORITY"
                    (fun (target, change) -> EffectDescriptor.Authority(target, change))
                    (function
                    | EffectDescriptor.Authority(target, change) -> Some(target, change)
                    | _ -> None)
                    (record (fun target change -> target, change)
                     |> field "targetActorId" fst actorId
                     |> field "change" snd authorityEffect)
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
