namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// One typed interpretation is shared by receipts, history, replay and rebuild.
/// Unavailable private values still require independent accepted-erasure evidence in replay.
[<NoComparison>]
type RetainedOperation =
    private
        {
            RootValue: OperationRoot
            ValuesValue: RetainedOperationValues
        }

    member this.Root = this.RootValue
    member this.Values = this.ValuesValue
    member this.OperationId = this.RootValue.Header.OperationId
    member this.ActorId = this.RootValue.Header.ActorId
    member this.RecordedAt = this.RootValue.Header.RecordedAt
    member this.Position = this.RootValue.Position

module RetainedOperation =
    let private casePayment =
        function
        | [ EffectDescriptor.Case(_, _, _, _, payment) ] -> payment
        | _ -> invalidOp "A case operation has one declared case effect."

    let private afterRevision =
        function
        | [ EffectDescriptor.Case(_, revision, _, _, _) ]
        | [ EffectDescriptor.Work(_, revision) ] -> revision
        | _ -> invalidOp "A versioned operation has one declared primary effect."

    let private profileRequired (scope: AuthorityCommandSkeleton) =
        match scope.Act with
        | AuthorityAct.Register _
        | AuthorityAct.Admit _
        | AuthorityAct.ProvisionOwner _
        | AuthorityAct.SetProfile _ -> true
        | AuthorityAct.EraseProfile
        | AuthorityAct.Grant _
        | AuthorityAct.Revoke _
        | AuthorityAct.Enable
        | AuthorityAct.Disable -> false

    let private matches (header: OperationHeader) command effects values =
        match command, values with
        | CommandSkeleton.Case scope, OperationValues.Case value ->
            let input, after = value.Command, value.Decision.After

            let selected =
                match input.Act with
                | CaseAct.RemovePayment id -> Some id
                | _ -> None

            let payment =
                match input.Act, value.Decision.Payment with
                | CaseAct.RecordPayment payment, Some(PaymentDifference.Recorded entry) ->
                    entry = PaymentEntry.recorded input.OperationId payment
                | CaseAct.RemovePayment selected, Some(PaymentDifference.Removed entry) ->
                    selected = PaymentEntry.id entry
                | (CaseAct.RecordPayment _ | CaseAct.RemovePayment _), _ -> false
                | _, None -> true
                | _ -> false

            input.OperationId = header.OperationId
            && input.ExpectedRevision = scope.ExpectedRevision
            && CaseReference.identity input.Reference = CaseReference.identity scope.Reference
            && CaseAct.kind input.Act = scope.Act
            && selected = scope.SelectedPayment
            && after.CaseId = scope.CaseId
            && after.Reference = scope.Reference
            && after.Revision = afterRevision effects
            && ((after.State = CaseState.Erased) = (scope.Act = CaseActKind.Erase))
            && Result.isOk (Case.ofAccepted after)
            && payment
        | CommandSkeleton.Work(parent, _), OperationValues.Work value ->
            value.After.CaseId = parent && value.After.Revision = afterRevision effects
        | CommandSkeleton.Note(NoteCommandSkeleton.Add parent), OperationValues.Note value ->
            match value.Command with
            | NoteCommand.AddNote(input, text) ->
                input = parent
                && Note.caseId value.After = parent
                && Note.id value.After = NoteId.ofOperationId header.OperationId
                && Note.text value.After = Some text
            | _ -> false
        | CommandSkeleton.Note(NoteCommandSkeleton.Remove(parent, note)), OperationValues.Note value ->
            value.Command = NoteCommand.RemoveNote(parent, note)
            && Note.caseId value.After = parent
            && Note.id value.After = note
            && Note.text value.After = None
        | CommandSkeleton.Reminder scope, OperationValues.Reminder value ->
            let target, expected =
                match value.Command with
                | ReminderCommand.Create _ -> None, None
                | ReminderCommand.Change(_, target, expected, _) -> Some target, Some expected

            ReminderCommand.parent value.Command = scope.CaseId
            && ReminderCommand.kind value.Command = scope.Act
            && target = scope.ReminderId
            && expected = scope.ExpectedRevision
            && ReminderCommand.matchesEffects header.OperationId value.Command value.Effects
        | CommandSkeleton.Authority scope, OperationValues.Authority value ->
            profileRequired scope = value.Profile.IsSome
        | _ -> false

    let private codec (header: OperationHeader) command effects =
        match command with
        | CommandSkeleton.Case scope ->
            OperationContentCodecs.case header scope (afterRevision effects) (casePayment effects)
            |> Codec.map OperationValues.Case (function
                | OperationValues.Case value -> value
                | _ -> invalidOp "Expected case values.")
        | CommandSkeleton.Work(parent, _) ->
            OperationContentCodecs.work parent (afterRevision effects)
            |> Codec.map OperationValues.Work (function
                | OperationValues.Work value -> value
                | _ -> invalidOp "Expected work values.")
        | CommandSkeleton.Note(NoteCommandSkeleton.Add parent) ->
            OperationContentCodecs.note parent header.OperationId
            |> Codec.map OperationValues.Note (function
                | OperationValues.Note value -> value
                | _ -> invalidOp "Expected note values.")
        | CommandSkeleton.Reminder scope ->
            OperationContentCodecs.reminder scope effects
            |> Codec.map OperationValues.Reminder (function
                | OperationValues.Reminder value -> value
                | _ -> invalidOp "Expected reminder values.")
        | CommandSkeleton.Authority _ ->
            OperationContentCodecs.profile
            |> Codec.map
                (fun profile -> OperationValues.Authority { Profile = Some profile })
                (function
                 | OperationValues.Authority { Profile = Some profile } -> profile
                 | _ -> invalidOp "Expected profile values.")
        | CommandSkeleton.Note(NoteCommandSkeleton.Remove _) ->
            invalidOp "Removed notes have no private content."

    let private disclosed =
        function
        | OperationValues.Case value -> RetainedOperationValues.Case(Some value)
        | OperationValues.Work value -> RetainedOperationValues.Work(Some value)
        | OperationValues.Note value -> RetainedOperationValues.Note(Some value)
        | OperationValues.Reminder value -> RetainedOperationValues.Reminder(Some value)
        | OperationValues.Authority value ->
            RetainedOperationValues.Authority(
                value.Profile
                |> Option.map RetainedProfile.Available
                |> Option.defaultValue RetainedProfile.NotRequired
            )

    /// Encode once from the already-decided typed values. The returned receipt values are not decoded again.
    let create header command values newNonce =
        OperationStructure.validateHeader header command
        |> Result.bind (fun () -> OperationStructure.expected header command)
        |> Result.mapError OperationContentProblem.Structure
        |> Result.bind (fun (effects, descriptors) ->
            if not (matches header command effects values) then
                Error OperationContentProblem.Values
            else
                let contents =
                    match descriptors with
                    | [] -> Ok []
                    | [ descriptor ] ->
                        let bytes =
                            (codec header command effects).Write values |> CanonicalWriter.bytes

                        Compartment.seal
                            header.InstallationId
                            header.OperationId
                            descriptor
                            (newNonce ())
                            bytes
                        |> Result.mapError OperationContentProblem.Compartment
                        |> Result.map List.singleton
                    | _ -> Error OperationContentProblem.MissingContent

                contents
                |> Result.bind (fun contents ->
                    OperationRoot.append header command effects contents
                    |> Result.mapError OperationContentProblem.Root
                    |> Result.map (fun root ->
                        {
                            RootValue = root
                            ValuesValue = disclosed values
                        })))

    let private publicValues (root: OperationRoot) =
        match root.Structure.Command with
        | CommandSkeleton.Case scope when scope.Act = CaseActKind.Erase ->
            Case.parse
                {
                    OperationId = OperationId.value root.Header.OperationId
                    CaseReference = CaseReference.format scope.Reference
                    ExpectedRevision = Revision.value scope.ExpectedRevision
                    Act = CaseAct.Bare BareAct.Erase
                }
            |> Result.mapError (fun _ -> OperationContentProblem.Values)
            |> Result.map (fun command ->
                RetainedOperationValues.Case(
                    Some
                        {
                            Command = command
                            Decision =
                                {
                                    After =
                                        {
                                            CaseId = scope.CaseId
                                            Reference = scope.Reference
                                            Revision = afterRevision root.Structure.Effects
                                            State = CaseState.Erased
                                        }
                                    Payment = None
                                }
                        }
                ))
        | CommandSkeleton.Note(NoteCommandSkeleton.Remove(parent, note)) ->
            Ok(
                RetainedOperationValues.Note(
                    Some
                        {
                            Command = NoteCommand.RemoveNote(parent, note)
                            After = Note.ofAccepted note parent None
                        }
                )
            )
        | CommandSkeleton.Authority scope when not (profileRequired scope) ->
            Ok(RetainedOperationValues.Authority RetainedProfile.NotRequired)
        | _ -> Error OperationContentProblem.MissingContent

    let private cleared =
        function
        | CommandSkeleton.Case _ -> RetainedOperationValues.Case None
        | CommandSkeleton.Work _ -> RetainedOperationValues.Work None
        | CommandSkeleton.Note _ -> RetainedOperationValues.Note None
        | CommandSkeleton.Reminder _ -> RetainedOperationValues.Reminder None
        | CommandSkeleton.Authority _ ->
            RetainedOperationValues.Authority RetainedProfile.Unavailable

    /// All private interpretation goes through this boundary. Public-only/erased values are explicit.
    let decode (root: OperationRoot) =
        let command, effects = root.Structure.Command, root.Structure.Effects

        let values =
            match root.Structure.Compartments with
            | [] -> publicValues root
            | [ compartment ] ->
                match Compartment.content compartment with
                | None -> Ok(cleared command)
                | Some(_, bytes) ->
                    let codec = codec root.Header command effects

                    Codec.decode Compartment.MaximumContentBytes codec bytes
                    |> Result.mapError (fun error ->
                        OperationContentProblem.PrivateFormat
                            {
                                Field = error.Path
                                Message = error.Reason
                            })
                    |> Result.bind (fun values ->
                        if not (matches root.Header command effects values) then
                            Error OperationContentProblem.Values
                        elif (codec.Write values |> CanonicalWriter.bytes) <> bytes then
                            Error OperationContentProblem.NonCanonicalPrivateContent
                        else
                            Ok(disclosed values))
            | _ -> Error OperationContentProblem.MissingContent

        values
        |> Result.map (fun values ->
            {
                RootValue = root
                ValuesValue = values
            })
