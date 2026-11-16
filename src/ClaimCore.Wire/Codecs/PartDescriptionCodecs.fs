namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Json

/// The same authoritative part differences serve reviews and retained history.
module PartDescriptionCodecs =
    open Codec
    open ResponseScalars

    let private change name value =
        (record (fun before after ->
            {
                ValueChange.Before = before
                After = after
            })
        : Fields<ValueChange<_>, _>)
        |> field "before" _.Before (nullable value)
        |> field "after" _.After (nullable value)
        |> build
        |> named name

    let private scope =
        tagged
            "AcceptedGrantScope"
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

    let grant =
        (record (fun v0 v1 -> { ActorGrant.Role = v0; Scope = v1 }): Fields<ActorGrant, _>)
        |> field "role" _.Role (VocabularyCodec.create Role.vocabulary)
        |> field "scope" _.Scope (scope)
        |> build
        |> named "ActorGrant"

    let assignment =
        (record (fun v0 v1 v2 ->
            {
                GrantAssignmentChange.Grant = v0
                Before = v1
                After = v2
            })
        : Fields<GrantAssignmentChange, _>)
        |> field "grant" _.Grant (grant)
        |> field "before" _.Before (boolean)
        |> field "after" _.After (boolean)
        |> build
        |> named "GrantAssignmentChange"

    let references =
        (record (fun v0 v1 ->
            {
                ChangeReferences.Actors = v0
                Cases = v1
            })
        : Fields<ChangeReferences, _>)
        |> field "actors" _.Actors (list 100 ActorCodecs.actorRef)
        |> field
            "cases"
            _.Cases
            (list
                100
                ((record (fun id reference ->
                    {
                        CaseReferenceItem.CaseId = id
                        Reference = reference
                    })
                 : Fields<CaseReferenceItem, _>)
                 |> field "caseId" _.CaseId caseId
                 |> field "caseReference" _.Reference caseReference
                 |> build
                 |> named "CaseReferenceItem"))
        |> build
        |> named "ChangeReferences"

    let totals =
        (record (fun v0 v1 v2 -> PaymentTotals.create v0 v1 v2): Fields<PaymentTotals, _>)
        |> field "payable" _.Payable (money)
        |> field "paid" _.Paid (money)
        |> field "remaining" _.Remaining (money)
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "PaymentTotals"

    let workValues =
        (record (fun v0 v1 ->
            {
                CaseWork.Responsible = v0
                Summary = v1
            })
        : Fields<CaseWork, _>)
        |> field "responsible" _.Responsible (nullable actorId)
        |> field "summary" _.Summary (nullable handlingSummary)
        |> build
        |> named "CaseWork"

    let plan =
        (record (fun v0 v1 -> { ReminderPlan.Action = v0; Due = v1 }): Fields<ReminderPlan, _>)
        |> field "action" _.Action (reminderAction)
        |> field "due" _.Due (calendarDate)
        |> build
        |> named "ReminderPlan"

    let person =
        (record (fun v0 v1 v2 v3 v4 v5 v6 ->
            {
                PersonPartDescription.Profiles = v0
                Principal = v1
                Enabled = v2
                ProfilePresent = v3
                DisplayName = v4
                Contact = v5
                Grants = v6
            })
        : Fields<PersonPartDescription, _>)
        |> field "profiles" _.Profiles (change "ProfileValues" ActorCodecs.profile)
        |> field "principal" _.Principal (nullable (change "PrincipalChange" PrincipalCodecs.key))
        |> field "enabled" _.Enabled (nullable (change "EnabledChange" boolean))
        |> field
            "profilePresent"
            _.ProfilePresent
            (nullable (change "ProfilePresenceChange" boolean))
        |> field "displayName" _.DisplayName (nullable (change "DisplayNameChange" partyName))
        |> field
            "contact"
            _.Contact
            (nullable (
                change
                    "ProfileContactChange"
                    (text
                     |> interpret
                         (fun path raw ->
                             ContactText.parse raw
                             |> Result.mapError (fun _ ->
                                 {
                                     Path = path
                                     Reason = "Invalid contact."
                                 }))
                         ContactText.format)
            ))
        |> field "grants" _.Grants (list PeopleAdmission.MaximumGrants assignment)
        |> build
        |> named "PersonPartDescription"

    let caseDescription =
        (record (fun v0 v1 v2 v3 v4 ->
            {
                CasePartDescription.Values = v0
                Lifecycle = v1
                Fields = v2
                Money = v3
                Disposition = v4
            })
        : Fields<CasePartDescription, _>)
        |> field "values" _.Values (change "CaseValues" ResponseCodecs.caseFields)
        |> field
            "lifecycle"
            _.Lifecycle
            (nullable (change "CaseLifecycleChange" (VocabularyCodec.create Lifecycle.vocabulary)))
        |> field
            "fields"
            _.Fields
            (list CaseFields.declarations.Length TimelineCodecs.fieldDifference)
        |> field
            "money"
            _.Money
            (list CaseFields.declarations.Length TimelineCodecs.moneyDifference)
        |> field "disposition" _.Disposition (nullable ResponseCodecs.disposition)
        |> build
        |> named "CasePartDescription"

    let payment =
        (record (fun v0 v1 v2 ->
            {
                PaymentPartDescription.Totals = v0
                Entry = v1
                Removed = v2
            })
        : Fields<PaymentPartDescription, _>)
        |> field "totals" _.Totals (change "PaymentTotalValues" totals)
        |> field "entry" _.Entry (nullable ClaimCore.RecordFormat.FinancialCodecs.entry)
        |> field "removed" _.Removed (nullable (change "PaymentRemovedChange" boolean))
        |> build
        |> named "PaymentPartDescription"

    let work =
        (record (fun v0 v1 v2 ->
            {
                WorkPartDescription.Values = v0
                Responsible = v1
                Summary = v2
            })
        : Fields<WorkPartDescription, _>)
        |> field "values" _.Values (change "WorkValues" workValues)
        |> field "responsible" _.Responsible (nullable (change "ResponsibleActorChange" actorId))
        |> field "summary" _.Summary (nullable (change "SummaryChange" handlingSummary))
        |> build
        |> named "WorkPartDescription"

    let reminder =
        (record (fun v0 v1 v2 v3 ->
            {
                ReminderPartDescription.Plans = v0
                Action = v1
                Due = v2
                State = v3
            })
        : Fields<ReminderPartDescription, _>)
        |> field "plans" _.Plans (change "ReminderPlans" plan)
        |> field "action" _.Action (nullable (change "ReminderActionValues" reminderAction))
        |> field "due" _.Due (nullable (change "ReminderDueValues" calendarDate))
        |> field
            "state"
            _.State
            (nullable (change "ReminderStates" (VocabularyCodec.create ReminderState.vocabulary)))
        |> build
        |> named "ReminderPartDescription"

    let note =
        (record (fun v0 v1 ->
            {
                NotePartDescription.Text = v0
                Removed = v1
            })
        : Fields<NotePartDescription, _>)
        |> field "text" _.Text (nullable (change "NoteTextChange" noteText))
        |> field "removed" _.Removed (nullable (change "NoteRemovedChange" boolean))
        |> build
        |> named "NotePartDescription"

    let description =
        tagged
            "PartDescription"
            [
                variant
                    "CASE"
                    PartDescription.Case
                    (function
                    | PartDescription.Case value -> Some value
                    | _ -> None)
                    (record id |> field "changes" id caseDescription)
                variant
                    "PAYMENT"
                    PartDescription.Payment
                    (function
                    | PartDescription.Payment value -> Some value
                    | _ -> None)
                    (record id |> field "changes" id payment)
                variant
                    "WORK"
                    PartDescription.Work
                    (function
                    | PartDescription.Work value -> Some value
                    | _ -> None)
                    (record id |> field "changes" id work)
                variant
                    "REMINDER"
                    PartDescription.Reminder
                    (function
                    | PartDescription.Reminder value -> Some value
                    | _ -> None)
                    (record id |> field "changes" id reminder)
                variant
                    "NOTE"
                    PartDescription.Note
                    (function
                    | PartDescription.Note value -> Some value
                    | _ -> None)
                    (record id |> field "changes" id note)
                variant
                    "PERSON"
                    PartDescription.Person
                    (function
                    | PartDescription.Person value -> Some value
                    | _ -> None)
                    (record id |> field "changes" id person)
            ]

    let part =
        (record (fun v0 v1 v2 ->
            {
                DescribedPart.PartId = v0
                Revision = v1
                Description = v2
            })
        : Fields<DescribedPart, _>)
        |> field "partId" _.PartId (uuid)
        |> field "revision" _.Revision (positiveRevision)
        |> field "description" _.Description (description)
        |> build
        |> named "DescribedPart"
