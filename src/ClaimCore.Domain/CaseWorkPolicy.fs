namespace ClaimCore.Domain

/// Use actual transaction facts for admission; read offers are only an observation of this policy.
module CaseWorkPolicy =
    let parentRefusal (snapshot: CaseSnapshot) =
        match snapshot.State with
        | CaseState.Erased -> Some(CaseRefusal.NotOffered CaseHeadline.Erased)
        | CaseState.Voided _ -> Some(CaseRefusal.NotOffered CaseHeadline.Voided)
        | CaseState.Active _ -> None

    let assignmentRefusal (snapshot: CaseSnapshot) =
        parentRefusal snapshot
        |> Option.orElseWith (fun () ->
            if
                CaseState.fields snapshot.State
                |> Option.exists (fun fields -> fields.Status = CaseStatus.Opened)
            then
                None
            else
                Some(CaseRefusal.NotOffered CaseHeadline.Closed))

    let authorize principal actor caseId =
        let resource = Scope.Case caseId

        if not (Authority.can principal actor Capability.ReadCase resource) then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        elif not (Authority.can principal actor Capability.ManageCaseWork resource) then
            Error(Refusal.Access(AccessRefusal.NotAuthorized Capability.ManageCaseWork))
        else
            Ok()

    let canEdit principal actor maintenance (snapshot: CaseSnapshot) =
        not maintenance
        && (parentRefusal snapshot).IsNone
        && Result.isOk (authorize principal actor snapshot.CaseId)

    let canAssign principal actor maintenance (snapshot: CaseSnapshot) =
        canEdit principal actor maintenance snapshot
        && (assignmentRefusal snapshot).IsNone
