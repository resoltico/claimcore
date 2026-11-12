namespace ClaimCore.Domain

module CaseWorkDecision =
    let apply expected (update: ValidCaseWorkUpdate) (before: CaseWorkSnapshot) =
        let next = CaseWork.apply update before.Work

        if before.Revision <> expected || Revision.value before.Revision = Revision.Maximum then
            Error(Refusal.revisionConflict before.Revision)
        elif next = before.Work then
            Error(Refusal.Case CaseRefusal.WorkNoChanges)
        else
            let summary =
                match update.Summary with
                | WorkValue.Set value when Some value <> before.Work.Summary ->
                    HandlingSummary.admit value
                    |> Result.map ignore
                    |> Result.mapError (fun violation ->
                        Refusal.invalidInput (
                            InputTarget.Field BusinessField.HandlingSummary,
                            violation
                        ))
                | _ -> Ok()

            summary
            |> Result.map (fun () ->
                { before with
                    Revision = Revision.successor before.Revision
                    Work = next
                })

    let decide (parent: CaseSnapshot) eligible expected update (before: CaseWorkSnapshot) =
        match CaseWorkPolicy.parentRefusal parent with
        | Some refusal -> Error(Refusal.Case refusal)
        | None ->
            let assignment =
                if (CaseWork.apply update before.Work).Responsible <> before.Work.Responsible then
                    CaseWorkPolicy.assignmentRefusal parent
                else
                    None

            if before.CaseId <> parent.CaseId then
                Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            elif before.Revision <> expected then
                Error(Refusal.revisionConflict before.Revision)
            elif assignment.IsSome then
                Error(Refusal.Case assignment.Value)
            elif not eligible then
                Error(Refusal.Case CaseRefusal.AssigneeUnavailable)
            else
                apply expected update before
