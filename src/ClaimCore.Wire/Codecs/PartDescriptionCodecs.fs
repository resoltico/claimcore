namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Json

/// The same authoritative part differences serve reviews and retained history.
module PartDescriptionCodecs =
    open Codec
    open ResponseScalars

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
                (ResponseCodecs.caseRef
                 |> map
                     (fun value ->
                         {
                             CaseReferenceItem.CaseId = value.CaseId
                             Reference = value.CaseReference
                         })
                     (fun value ->
                         {
                             CaseRef.CaseId = value.CaseId
                             CaseReference = value.Reference
                         })))
        |> build
        |> named "ChangeReferences"

    let totals = PaymentCodecs.totals

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
        (record (fun action due kind ->
            {
                ReminderPlan.Action = action
                Due = due
                Kind = kind
            })
        : Fields<ReminderPlan, _>)
        |> field "action" _.Action (reminderAction)
        |> field "due" _.Due calendarDate
        |> field "kind" _.Kind (VocabularyCodec.create ReminderKind.vocabulary)
        |> build
        |> named "ReminderPlan"

    let person =
        (record (fun v0 v1 v2 v3 v4 v5 email v6 ->
            {
                PersonPartDescription.Profiles = v0
                Principal = v1
                Enabled = v2
                ProfilePresent = v3
                DisplayName = v4
                Contact = v5
                DigestEmail = email
                Grants = v6
            })
        : Fields<PersonPartDescription, _>)
        |> field
            "profiles"
            _.Profiles
            (ValueChangeCodecs.change "ProfileValues" ActorCodecs.profile)
        |> field
            "principal"
            _.Principal
            (nullable (ValueChangeCodecs.change "PrincipalChange" PrincipalCodecs.key))
        |> field "enabled" _.Enabled (nullable (ValueChangeCodecs.change "EnabledChange" boolean))
        |> field
            "profilePresent"
            _.ProfilePresent
            (nullable (ValueChangeCodecs.change "ProfilePresenceChange" boolean))
        |> field
            "displayName"
            _.DisplayName
            (nullable (ValueChangeCodecs.change "DisplayNameChange" partyName))
        |> field
            "contact"
            _.Contact
            (nullable (
                ValueChangeCodecs.change
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
        |> optionalField
            "digestEmail"
            _.DigestEmail
            (ValueChangeCodecs.change "DigestEmailChange" ActorCodecs.emailAddress)
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
        |> field "values" _.Values (ValueChangeCodecs.change "CaseValues" ResponseCodecs.caseFields)
        |> field
            "lifecycle"
            _.Lifecycle
            (nullable (
                ValueChangeCodecs.change
                    "CaseLifecycleChange"
                    (VocabularyCodec.create Lifecycle.vocabulary)
            ))
        |> field
            "fields"
            _.Fields
            (list CaseFields.declarations.Length TimelineCodecs.fieldDifference)
        |> field
            "money"
            _.Money
            (list CaseFields.declarations.Length TimelineCodecs.moneyDifference)
        |> field "disposition" _.Disposition (nullable CaseStateCodecs.disposition)
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
        |> field "totals" _.Totals (ValueChangeCodecs.change "PaymentTotalValues" totals)
        |> field "entry" _.Entry (nullable ClaimCore.RecordFormat.FinancialCodecs.entry)
        |> field
            "removed"
            _.Removed
            (nullable (ValueChangeCodecs.change "PaymentRemovedChange" boolean))
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
        |> field "values" _.Values (ValueChangeCodecs.change "WorkValues" workValues)
        |> field
            "responsible"
            _.Responsible
            (nullable (ValueChangeCodecs.change "ResponsibleActorChange" actorId))
        |> field
            "summary"
            _.Summary
            (nullable (ValueChangeCodecs.change "HandlingSummaryChange" handlingSummary))
        |> build
        |> named "WorkPartDescription"

    let reminder =
        (record (fun plans action due kind doneOn state ->
            {
                ReminderPartDescription.Plans = plans
                Action = action
                Due = due
                Kind = kind
                DoneOn = doneOn
                State = state
            })
        : Fields<ReminderPartDescription, _>)
        |> field "plans" _.Plans (ValueChangeCodecs.change "ReminderPlans" plan)
        |> field
            "action"
            _.Action
            (nullable (ValueChangeCodecs.change "ReminderActionChange" reminderAction))
        |> field "due" _.Due (nullable (ValueChangeCodecs.change "ReminderDueChange" calendarDate))
        |> field
            "kind"
            _.Kind
            (nullable (
                ValueChangeCodecs.change
                    "ReminderKindChange"
                    (VocabularyCodec.create ReminderKind.vocabulary)
            ))
        |> field
            "doneOn"
            _.DoneOn
            (nullable (ValueChangeCodecs.change "ReminderDoneOnChange" calendarDate))
        |> field
            "state"
            _.State
            (nullable (
                ValueChangeCodecs.change
                    "ReminderStateChange"
                    (VocabularyCodec.create ReminderState.vocabulary)
            ))
        |> build
        |> named "ReminderPartDescription"

    let relatedCaseContent =
        (record (fun target note ->
            {
                RelatedCaseContent.Target = target
                Note = note
            })
        : Fields<RelatedCaseContent, _>)
        |> field "target" _.Target caseId
        |> field "note" _.Note (nullable noteText)
        |> build
        |> named "RelatedCaseContent"

    let private attachment =
        (record (fun withdrawn display ->
            {
                AttachmentPartDescription.Withdrawn = withdrawn
                Display = display
            })
        : Fields<AttachmentPartDescription, _>)
        |> field "withdrawn" _.Withdrawn boolean
        |> field "display" _.Display (nullable AttachmentCodecs.display)
        |> build
        |> named "AttachmentPartDescription"

    let note =
        (record (fun v0 v1 ->
            {
                NotePartDescription.Text = v0
                Removed = v1
            })
        : Fields<NotePartDescription, _>)
        |> field "text" _.Text (nullable (ValueChangeCodecs.change "NoteTextChange" noteText))
        |> field
            "removed"
            _.Removed
            (nullable (ValueChangeCodecs.change "NoteRemovedChange" boolean))
        |> build
        |> named "NotePartDescription"

    let party =
        (record (fun content removed ->
            {
                PartyPartDescription.Content = content
                Removed = removed
            })
        : Fields<PartyPartDescription, _>)
        |> field "content" _.Content (ValueChangeCodecs.change "PartyContentChange" partyContent)
        |> field
            "removed"
            _.Removed
            (nullable (ValueChangeCodecs.change "PartyRemovedChange" boolean))
        |> build
        |> named "PartyPartDescription"

    let opening =
        let case =
            (record (fun fields money lifecycle ->
                {
                    OpeningCaseDescription.Fields = fields
                    Money = money
                    Lifecycle = lifecycle
                })
            : Fields<OpeningCaseDescription, _>)
            |> field
                "fields"
                _.Fields
                (list CaseFields.declarations.Length TimelineCodecs.fieldDifference)
            |> field
                "money"
                _.Money
                (list CaseFields.declarations.Length TimelineCodecs.moneyDifference)
            |> field "lifecycle" _.Lifecycle (VocabularyCodec.create Lifecycle.vocabulary)
            |> build
            |> named "OpeningCaseDescription"

        (record (fun case parties ->
            {
                OpeningReview.Case = case
                Parties = parties
            })
        : Fields<OpeningReview, _>)
        |> field "case" _.Case case
        |> field "parties" _.Parties (list PartyContent.MaximumCurrent party)
        |> build
        |> named "OpeningReview"

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
                    "ATTACHMENT"
                    PartDescription.Attachment
                    (function
                    | PartDescription.Attachment value -> Some value
                    | _ -> None)
                    (record id |> field "changes" id attachment)
                variant
                    "NOTE"
                    PartDescription.Note
                    (function
                    | PartDescription.Note value -> Some value
                    | _ -> None)
                    (record id |> field "changes" id note)
                variant
                    "PARTY"
                    PartDescription.Party
                    (function
                    | PartDescription.Party value -> Some value
                    | _ -> None)
                    (record id |> field "changes" id party)
                variant
                    "LINK"
                    PartDescription.Link
                    (function
                    | PartDescription.Link value -> Some value
                    | _ -> None)
                    (record id
                     |> field
                         "changes"
                         id
                         (ValueChangeCodecs.change "RelatedCaseChange" relatedCaseContent))
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
