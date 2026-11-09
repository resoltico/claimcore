namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries
open CaseScreen
open PersonScreen

module ReviewScreen =
    let flatten sections =
        sections |> List.collect (fun (section: Section) -> section.Entries)

    let private paymentChanges (changes: PaymentCollectionDifference) =
        let amount totals entry =
            match totals with
            | Some(totals: PaymentTotals) ->
                Money.create (PaymentEntry.amount entry) (Money.currencyValue totals.Payable)
            | None -> invalidOp "A payment change requires its financial context."

        let changed key label before after =
            {
                PresentedChange.Key = key
                LabelId = label
                Before = before
                After = after
            }

        [
            for change in changes.Entries do
                let entry = PaymentDifference.entry change
                let key = "payment." + OperationId.format (PaymentEntry.id entry)
                let date = PresentedValue.Date(PaymentEntry.date entry)

                match change with
                | PaymentDifference.Recorded _ ->
                    yield changed (key + ".date") "field.paymentDate.label" absent date

                    yield
                        changed
                            (key + ".amount")
                            "field.paidAmount.label"
                            absent
                            (PresentedValue.money (amount changes.After entry))
                | PaymentDifference.Removed _ ->
                    yield changed (key + ".date") "field.paymentDate.label" date absent

                    yield
                        changed
                            (key + ".amount")
                            "field.paidAmount.label"
                            (PresentedValue.money (amount changes.Before entry))
                            absent
            for key, label, select in
                [
                    "paid", "label.paid", (fun (totals: PaymentTotals) -> totals.Paid)
                    "remaining", "label.remaining", (fun totals -> totals.Remaining)
                ] do
                let before = changes.Before |> Option.map select
                let after = changes.After |> Option.map select

                if before <> after then
                    yield
                        changed
                            key
                            label
                            (optional PresentedValue.money before)
                            (optional PresentedValue.money after)
        ]

    let caseReviewPresentation (review: AdvisoryReview) =
        let declarations =
            CaseFields.declarations
            |> List.filter (fun field ->
                field.Section <> Some CaseSection.Work
                && field.Group <> Some RecordGroup.Payment)

        let snapshotValues (snapshot: CaseSnapshot option) =
            declarations
            |> List.map (fun field ->
                field.Field,
                (match snapshot with
                 | Some snapshot when field.Field = BusinessField.CaseReference ->
                     Some(CaseFieldValue.Reference snapshot.Reference)
                 | Some snapshot ->
                     CaseState.fields snapshot.State
                     |> Option.bind (CaseFields.typedValue field.Field)
                 | None -> None))
            |> Map.ofList

        let sides get snapshot =
            review.Changes.Fields
            |> List.fold
                (fun values change -> Map.add change.FieldName (get change) values)
                (snapshotValues snapshot)

        let definitions = fields declarations (fun values -> Some values)
        let before = entries definitions (sides _.Before review.Before)
        let after = entries definitions (sides _.After (Some review.Proposed))

        let changedKeys =
            review.Changes.Fields
            |> List.map (fun change ->
                declarations
                |> List.tryFind (fun field -> field.Pairs = Some change.FieldName)
                |> Option.map _.Field
                |> Option.defaultValue change.FieldName
                |> BusinessField.key)
            |> Set.ofList

        let erased = CaseState.lifecycle review.Proposed.State = Lifecycle.Erased

        let changed =
            List.zip before after
            |> List.choose (fun (before, after) ->
                if Set.contains after.Key changedKeys then
                    Some
                        {
                            PresentedChange.Key = after.Key
                            LabelId = after.LabelId
                            Before = before.Value
                            After =
                                if erased && after.Value = absent then
                                    PresentedValue.Absent "lifecycle.ERASED.label"
                                else
                                    after.Value
                        }
                else
                    None)

        let lifecycle =
            review.Changes.Lifecycle
            |> Option.toList
            |> List.map (fun change ->
                {
                    PresentedChange.Key = "lifecycle"
                    LabelId = "input.lifecycle.label"
                    Before = optional (code Lifecycle.vocabulary) change.Before
                    After = code Lifecycle.vocabulary change.After
                })

        let unchanged =
            if review.Before.IsNone || erased then
                []
            else
                (after |> List.filter (fun entry -> not (Set.contains entry.Key changedKeys)))
                @ (if review.Changes.Lifecycle.IsNone then
                       [
                           value
                               "lifecycle"
                               "input.lifecycle.label"
                               Prominence.Primary
                               (code
                                   Lifecycle.vocabulary
                                   (CaseState.lifecycle review.Proposed.State))
                       ]
                   else
                       [])

        lifecycle
        @ changed
        @ (review.Changes.Payments |> Option.map paymentChanges |> Option.defaultValue []),
        unchanged

    let profileEntries (profile: ActorProfile option) =
        [
            value
                "profileName"
                "field.profile.displayName.label"
                Prominence.Primary
                (optional
                    (fun (profile: ActorProfile) -> PresentedValue.Party profile.DisplayName)
                    profile)
            value
                "profileContact"
                "field.profile.contact.label"
                Prominence.Primary
                (profile
                 |> Option.bind _.Contact
                 |> optional (ContactText.format >> PresentedValue.Text))
        ]

    let authorityStateEntries (state: AuthorityState) =
        [ value "enabled" "label.enabled" Prominence.Primary (boolean state.Enabled) ]
        @ profileEntries state.Profile
        @ (state.Grants
           |> List.collect (fun item ->
               let identity =
                   Role.token item.Role
                   + "."
                   + (match item.Scope with
                      | GrantScope.Installation -> "installation"
                      | GrantScope.Case(id, _) -> CaseId.format id)

               grant item
               |> List.map (fun entry ->
                   { entry with
                       Key = identity + "." + entry.Key
                   })))

    let authorityEffects (effects: AuthorityReviewEffect list) =
        effects
        |> List.map (fun effect ->
            let kind, details =
                match effect with
                | AuthorityReviewEffect.Register principal
                | AuthorityReviewEffect.ProvisionOwner principal ->
                    let kind, issuer, subject = PrincipalKey.parts principal

                    (match effect with
                     | AuthorityReviewEffect.Register _ -> AuthorityKind.Register
                     | _ -> AuthorityKind.ProvisionOwner),
                    [
                        value
                            "principalKind"
                            "field.principal.kind.label"
                            Prominence.Detail
                            (code PrincipalKind.vocabulary kind)
                        value
                            "issuer"
                            "field.principal.issuer.label"
                            Prominence.Detail
                            (PresentedValue.Identifier issuer)
                        value
                            "subject"
                            "field.principal.subject.label"
                            Prominence.Detail
                            (PresentedValue.Identifier subject)
                    ]
                | AuthorityReviewEffect.SetProfile _ -> AuthorityKind.SetProfile, []
                | AuthorityReviewEffect.EraseProfile -> AuthorityKind.EraseProfile, []
                | AuthorityReviewEffect.Grant item -> AuthorityKind.Grant, grant item
                | AuthorityReviewEffect.Revoke item -> AuthorityKind.Revoke, grant item
                | AuthorityReviewEffect.Enable -> AuthorityKind.Enable, []
                | AuthorityReviewEffect.Disable -> AuthorityKind.Disable, []

            section
                "label.effect"
                (value "act" "label.action" Prominence.Primary (code AuthorityKind.vocabulary kind)
                 :: details))

    let sameValue before after =
        match before, after with
        | PresentedValue.Actor before, PresentedValue.Actor after -> before.ActorId = after.ActorId
        | _ -> before = after

    let changes afterMissing before after =
        let previous =
            before |> List.map (fun (entry: Entry) -> entry.Key, entry) |> Map.ofList

        let proposed =
            after |> List.map (fun (entry: Entry) -> entry.Key, entry) |> Map.ofList

        let keys = (after @ before) |> List.map _.Key |> List.distinct

        keys
        |> List.fold
            (fun (changed, unchanged) key ->
                let old = Map.tryFind key previous
                let next = Map.tryFind key proposed
                let definition = next |> Option.orElse old |> Option.get
                let oldValue = old |> Option.map _.Value |> Option.defaultValue absent
                let newValue = next |> Option.map _.Value |> Option.defaultValue afterMissing

                if sameValue oldValue newValue then
                    changed,
                    (if old.IsSome && next.IsSome then
                         definition :: unchanged
                     else
                         unchanged)
                else
                    {
                        PresentedChange.Key = key
                        LabelId = definition.LabelId
                        Before = oldValue
                        After = newValue
                    }
                    :: changed,
                    unchanged)
            ([], [])
        |> fun (changed, unchanged) -> List.rev changed, List.rev unchanged

    let reviewValues =
        function
        | OperationReview.Case review ->
            let changed, unchanged = caseReviewPresentation review

            {
                Changes = changed
                Unchanged = unchanged
                Effects = []
                Basis = review.Context.Basis
            }
        | OperationReview.Work review ->
            let changed, unchanged =
                changes absent (workEntries review.Before) (workEntries review.Proposed)

            {
                Changes = changed
                Unchanged = unchanged
                Effects = []
                Basis = review.Context.Basis
            }
        | OperationReview.Note review ->
            let changed, unchanged =
                changes
                    absent
                    (review.Before |> Option.map NotesScreen.entries |> Option.defaultValue [])
                    (NotesScreen.entries review.Proposed)

            {
                Changes = changed
                Unchanged = unchanged
                Effects = []
                Basis = review.Context.Basis
            }
        | OperationReview.Reminder review ->
            let after =
                match review.Effects with
                | ReminderEffects.Created item
                | ReminderEffects.Changed item -> reminderEntries item
                | ReminderEffects.CompletedAndPlanned(completed, next) ->
                    reminderEntries completed @ reminderEntries next

            let changed, unchanged =
                changes
                    absent
                    (review.Before |> Option.map reminderEntries |> Option.defaultValue [])
                    after

            {
                Changes = changed
                Unchanged = unchanged
                Effects = []
                Basis = review.Context.Basis
            }
        | OperationReview.Authority review ->
            let changed, unchanged =
                changes
                    absent
                    (authorityStateEntries review.Before)
                    (authorityStateEntries review.After)

            {
                Changes = changed
                Unchanged = unchanged
                Effects = authorityEffects review.Effects
                Basis = review.Basis
            }

    let authoredDetails input =
        match input with
        | Some input ->
            let kind =
                match input with
                | OperationInput.Case item -> ActKind.Case(CaseAct.kind item.Act)
                | OperationInput.Authority item -> ActKind.Authority(AuthorityAct.kind item.Act)
                | OperationInput.Note item -> ActKind.Note(AuthoredNoteAct.kind item.Act)
                | OperationInput.Work _ -> ActKind.Work WorkActKind.UpdateWork
                | OperationInput.Reminder item ->
                    ActKind.Reminder(AuthoredReminderAct.kind item.Act)

            let consequence =
                match Consequence.ofAct kind with
                | Consequence.Routine -> []
                | Consequence.Stewardship
                | Consequence.Irreversible ->
                    [ PresentationKeys.consequence kind ]
                    |> List.map (fun key ->
                        section
                            "label.effect"
                            [
                                value
                                    "consequence"
                                    "label.effect"
                                    Prominence.Primary
                                    (PresentedValue.Message key)
                            ])

            let intent =
                match input with
                | OperationInput.Case {
                                          Act = CaseAct.Void reason | CaseAct.Reinstate reason
                                      } ->
                    [
                        section
                            "label.details"
                            [
                                value
                                    "reason"
                                    "field.reason.label"
                                    Prominence.Primary
                                    (PresentedValue.Text reason)
                            ]
                    ]
                | _ -> []

            consequence @ intent
        | None -> []

    let operationReview input review =
        let described = reviewValues review

        { described with
            Effects = authoredDetails input @ described.Effects
        }
