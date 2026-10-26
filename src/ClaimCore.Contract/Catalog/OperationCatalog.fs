namespace ClaimCore.Contract

open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type TechnicalInput =
    | OperationId
    | ExpectedRevision
    | PrincipalKind
    | Issuer
    | Subject
    | Role
    | Scope
    | Target
    | Act
    | ActorId
    | Cursor
    | Limit
    | Full
    | Reason

module TechnicalInputs =
    let vocabulary =
        Vocabulary.define
            "input"
            [
                TechnicalInput.OperationId, "operationId"
                TechnicalInput.ExpectedRevision, "expectedRevision"
                TechnicalInput.PrincipalKind, "kind"
                TechnicalInput.Issuer, "issuer"
                TechnicalInput.Subject, "subject"
                TechnicalInput.Role, "role"
                TechnicalInput.Scope, "scope"
                TechnicalInput.Target, "target"
                TechnicalInput.Act, "act"
                TechnicalInput.ActorId, "actorId"
                TechnicalInput.Cursor, "cursor"
                TechnicalInput.Limit, "limit"
                TechnicalInput.Full, "full"
                TechnicalInput.Reason, "reason"
            ]

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type CatalogInputName =
    | Field of BusinessField
    | Group of CorrectionGroup
    | Technical of TechnicalInput

[<RequireQualifiedAccess>]
type CatalogArgument =
    | Named
    | Operand
    | Implicit

[<RequireQualifiedAccess>]
type CatalogAtom =
    | Text
    | Integer
    | Choice of string list
    | Switch of off: string * on: string

type CatalogInputSpec =
    {
        Name: CatalogInputName
        Atom: CatalogAtom
        Argument: CatalogArgument
        Required: bool
        Default: BindingValue option
        Prefill: BusinessField option
    }

[<RequireQualifiedAccess>]
type CatalogSelection =
    | ByChoice of CatalogInputName
    | ByPresence of CatalogInputName

[<RequireQualifiedAccess>]
type CatalogShape =
    | Const of BindingValue
    | Input of CatalogInputSpec
    | Object of (string * CatalogShape) list
    | Variants of tag: string * selection: CatalogSelection * branches: (string * CatalogShape) list

type CatalogOperation =
    {
        Endpoint: string
        Act: ActKind option
        Body: CatalogShape
    }

type CatalogDerivedInput =
    {
        Spec: CatalogInputSpec
        Binding: CatalogInput
    }

/// The actor-facing operation catalog. Bootstrap/session and owner administration belong to their hosts.
module OperationCatalog =
    let inputKey =
        function
        | CatalogInputName.Field field -> FieldTable.key field
        | CatalogInputName.Group group -> CorrectionGroup.token group
        | CatalogInputName.Technical input -> TechnicalInputs.token input

    let inputMessage name part =
        match name with
        | CatalogInputName.Field field -> Vocabulary.messageKey FieldTable.vocabulary field part
        | CatalogInputName.Group group ->
            Vocabulary.messageKey CorrectionGroup.vocabulary group part
        | CatalogInputName.Technical input ->
            Vocabulary.messageKey TechnicalInputs.vocabulary input part

    let private technical input = CatalogInputName.Technical input

    let private constant value =
        CatalogShape.Const(BindingValue.Text value)

    let private obj values = CatalogShape.Object values

    let private input name atom argument required defaultValue prefill =
        inputKey name,
        CatalogShape.Input
            {
                Name = name
                Atom = atom
                Argument = argument
                Required = required
                Default = defaultValue
                Prefill = prefill
            }

    let private named name atom =
        input name atom CatalogArgument.Named true None None

    let private operand name =
        input name CatalogAtom.Text CatalogArgument.Operand true None None

    let private fields group prefill =
        FieldTable.rows
        |> List.filter (fun row -> row.Group = group)
        |> List.map (fun row ->
            input
                (CatalogInputName.Field row.Field)
                CatalogAtom.Text
                CatalogArgument.Named
                true
                None
                (if prefill then Some row.Field else None))

    let private identity =
        input
            (technical TechnicalInput.OperationId)
            CatalogAtom.Text
            CatalogArgument.Named
            false
            None
            None

    let private reference =
        input
            (CatalogInputName.Field BusinessField.CaseReference)
            CatalogAtom.Text
            CatalogArgument.Operand
            true
            None
            (Some BusinessField.CaseReference)

    let private expected =
        named (technical TechnicalInput.ExpectedRevision) CatalogAtom.Integer

    let private mutation act body =
        let scope =
            match act with
            | ActKind.Case _ -> OperationScopes.Case
            | ActKind.Authority _ -> OperationScopes.Authority

        {
            Endpoint = Api.operationExecute.Id
            Act = Some act
            Body = obj (("scope", constant scope) :: identity :: body)
        }

    let private caseEntry kind payload =
        let act = ActKind.Case kind

        let revision =
            if kind = CaseActKind.Business BusinessActKind.Open then
                "expectedRevision", CatalogShape.Const(BindingValue.Integer 0L)
            else
                expected

        mutation
            act
            [
                reference
                revision
                "act", obj (("kind", constant (ActKinds.token act)) :: payload)
            ]

    let private caseFields kind group prefill =
        caseEntry kind [ "values", obj (fields group prefill) ]

    let private correction group =
        let fieldGroup =
            match group with
            | CorrectionGroup.Registration -> FieldGroup.Registration
            | CorrectionGroup.Decision -> FieldGroup.Decision
            | CorrectionGroup.Payment -> FieldGroup.Payment

        let branches =
            Protocol.RequestCodecs.correctionActions group
            |> List.map (fun action ->
                let body =
                    if action = CorrectionGroupAction.Replace then
                        [ "values", obj (fields fieldGroup true) ]
                    else
                        []

                CorrectionActions.token action, obj body)

        CorrectionGroup.token group,
        CatalogShape.Variants(
            "mode",
            CatalogSelection.ByChoice(CatalogInputName.Group group),
            branches
        )

    let private caseEntries =
        [
            caseFields (CaseActKind.Business BusinessActKind.Open) FieldGroup.Registration false
            caseFields
                (CaseActKind.Business BusinessActKind.AmendRegistration)
                FieldGroup.Registration
                true
            caseEntry
                (CaseActKind.Business BusinessActKind.Correct)
                [
                    "groups", obj (Vocabulary.all CorrectionGroup.vocabulary |> List.map correction)
                ]
            caseFields (CaseActKind.Business BusinessActKind.Decide) FieldGroup.Decision true
            caseEntry (CaseActKind.Business BusinessActKind.WithdrawDecision) []
            caseFields (CaseActKind.Business BusinessActKind.RecordPayment) FieldGroup.Payment false
            caseEntry (CaseActKind.Business BusinessActKind.ClearPayment) []
            caseEntry (CaseActKind.Business BusinessActKind.Close) []
            caseEntry (CaseActKind.Business BusinessActKind.Reopen) []
            caseEntry
                (CaseActKind.Stewardship StewardshipActKind.Void)
                [ named (technical TechnicalInput.Reason) CatalogAtom.Text ]
            caseEntry
                (CaseActKind.Stewardship StewardshipActKind.Reinstate)
                [ named (technical TechnicalInput.Reason) CatalogAtom.Text ]
            caseEntry (CaseActKind.Stewardship StewardshipActKind.Erase) []
        ]

    let private target =
        obj
            [
                named
                    (technical TechnicalInput.PrincipalKind)
                    (CatalogAtom.Choice PrincipalKinds.all)
                named (technical TechnicalInput.Issuer) CatalogAtom.Text
                named (technical TechnicalInput.Subject) CatalogAtom.Text
            ]

    let private grant =
        [
            named
                (technical TechnicalInput.Role)
                (CatalogAtom.Choice(RoleTokens.all |> List.map RoleTokens.token))
            "scope",
            CatalogShape.Variants(
                "kind",
                CatalogSelection.ByPresence(technical TechnicalInput.Scope),
                [
                    "INSTALLATION", obj []
                    OperationScopes.Case,
                    obj
                        [
                            named
                                (CatalogInputName.Field BusinessField.CaseReference)
                                CatalogAtom.Text
                        ]
                ]
            )
        ]

    let private authorityEntries =
        AuthorityKinds.all
        |> List.choose (fun kind ->
            match kind with
            | AuthorityKind.ProvisionOwner -> None
            | _ ->
                let act = ActKind.Authority kind

                let payload =
                    match kind with
                    | AuthorityKind.Grant
                    | AuthorityKind.Revoke -> grant
                    | _ -> []

                Some(
                    mutation
                        act
                        [
                            TechnicalInputs.token TechnicalInput.Target, target
                            "act", obj (("kind", constant (ActKinds.token act)) :: payload)
                        ]
                ))

    let private page =
        [
            input
                (technical TechnicalInput.Cursor)
                CatalogAtom.Text
                CatalogArgument.Named
                false
                None
                None
            input
                (technical TechnicalInput.Limit)
                CatalogAtom.Integer
                CatalogArgument.Named
                false
                (Some(BindingValue.Integer(int64 Limits.DefaultPageSize)))
                None
        ]

    let private read endpoint body =
        {
            Endpoint = endpoint
            Act = None
            Body = obj body
        }

    let private reads =
        [
            read Api.caseGet.Id [ reference ]
            read Api.caseList.Id page
            read
                Api.caseHistory.Id
                (reference
                 :: ("detail",
                     CatalogShape.Input
                         {
                             Name = technical TechnicalInput.Full
                             Atom =
                                 CatalogAtom.Switch(
                                     Vocabulary.token
                                         HistoryDetails.vocabulary
                                         HistoryDetail.Summary,
                                     Vocabulary.token HistoryDetails.vocabulary HistoryDetail.Full
                                 )
                             Argument = CatalogArgument.Named
                             Required = false
                             Default = None
                             Prefill = None
                         })
                 :: page)
            read Api.operationObserve.Id [ operand (technical TechnicalInput.OperationId) ]
            read Api.operationResolve.Id [ operand (technical TechnicalInput.OperationId) ]
            read Api.authorityList.Id page
            read Api.authorityGrants.Id (operand (technical TechnicalInput.ActorId) :: page)
            read Api.context.Id []
        ]

    let all = caseEntries @ authorityEntries @ reads
    let mutations = all |> List.filter (fun entry -> entry.Act.IsSome)

    let authorityKinds =
        authorityEntries
        |> List.choose (fun entry ->
            match entry.Act with
            | Some(ActKind.Authority kind) -> Some kind
            | _ -> None)

    let private message entry part =
        match entry.Act with
        | Some act -> Vocabulary.messageKey ActKinds.vocabulary act part
        | None -> "cli.read." + entry.Endpoint + "." + part

    let labelId entry = message entry "label"
    let meaningId entry = message entry "meaning"

    let confirmationId entry =
        entry.Act |> Option.map (fun _ -> message entry "confirmation")

    let kebab (value: string) =
        Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2").Replace('_', '-').ToLowerInvariant()

    let words entry =
        match entry.Act with
        | Some(ActKind.Case kind) -> [ "case"; kebab (CaseActKinds.token kind) ]
        | Some(ActKind.Authority kind) -> [ "authority"; kebab (AuthorityKinds.token kind) ]
        | None -> entry.Endpoint.Split('.') |> List.ofArray

    let private source entry name =
        match name with
        | CatalogInputName.Technical TechnicalInput.OperationId when entry.Act.IsSome ->
            InputSource.OperationIdentity
        | CatalogInputName.Technical TechnicalInput.ExpectedRevision -> InputSource.ExpectedRevision
        | _ -> InputSource.Authored

    let private targetFor =
        function
        | CatalogInputName.Field field -> Some(InputTarget.Field field)
        | CatalogInputName.Technical TechnicalInput.Reason -> Some InputTarget.Reason
        | _ -> None

    let private diagnostics =
        function
        | CatalogInputName.Field field -> [ FieldTable.key field ]
        | CatalogInputName.Group group -> [ CorrectionGroup.token group ]
        | CatalogInputName.Technical TechnicalInput.OperationId -> [ "operationId" ]
        | CatalogInputName.Technical TechnicalInput.ExpectedRevision -> [ "expectedRevision" ]
        | CatalogInputName.Technical TechnicalInput.Issuer -> [ "principal.issuer" ]
        | CatalogInputName.Technical TechnicalInput.Subject -> [ "principal.subject" ]
        | CatalogInputName.Technical TechnicalInput.Reason -> [ "reason" ]
        | _ -> []

    let private bindingKind =
        function
        | CatalogAtom.Text -> BindingKind.Text
        | CatalogAtom.Integer -> BindingKind.Integer
        | CatalogAtom.Choice options -> BindingKind.Choice options
        | CatalogAtom.Switch(off, on) -> BindingKind.Choice [ off; on ]

    let derive entry =
        let binding path branches spec kind =
            {
                Spec = spec
                Binding =
                    {
                        Identity = String.concat "." path
                        Field = targetFor spec.Name
                        Path = path
                        Kind = kind
                        Branches = branches
                        LabelId = inputMessage spec.Name "label"
                        MeaningId = Some(inputMessage spec.Name "meaning")
                        Order = 0
                        Prefill =
                            spec.Prefill
                            |> Option.map (fun field ->
                                let row = FieldTable.forField field

                                match row.Group with
                                | FieldGroup.Identity
                                | FieldGroup.Status -> [ row.Key ]
                                | group -> [ FieldGroup.token group; row.Key ])
                        Required = spec.Required
                        Default = spec.Default
                        DiagnosticTargets = diagnostics spec.Name
                        Source = source entry spec.Name
                    }
            }

        let rec walk path branches =
            function
            | CatalogShape.Const value ->
                [],
                [
                    {
                        Path = path
                        Value = value
                        Branches = branches
                    }
                ]
            | CatalogShape.Input spec -> [ binding path branches spec (bindingKind spec.Atom) ], []
            | CatalogShape.Object members ->
                members
                |> List.map (fun (name, shape) -> walk (path @ [ name ]) branches shape)
                |> List.fold
                    (fun (inputs, constants) (moreInputs, moreConstants) ->
                        inputs @ moreInputs, constants @ moreConstants)
                    ([], [])
            | CatalogShape.Variants(tag, selection, cases) ->
                let name, argument, required =
                    match selection with
                    | CatalogSelection.ByChoice name -> name, CatalogArgument.Named, true
                    | CatalogSelection.ByPresence name -> name, CatalogArgument.Implicit, false

                let tags = List.map fst cases

                let spec =
                    {
                        Name = name
                        Atom = CatalogAtom.Choice tags
                        Argument = argument
                        Required = required
                        Default = None
                        Prefill = None
                    }

                let selectorPath = path @ [ tag ]
                let selector = binding selectorPath branches spec (BindingKind.Group tags)

                let inputs, constants =
                    cases
                    |> List.map (fun (value, shape) ->
                        walk path (branches @ [ { Path = selectorPath; Value = value } ]) shape)
                    |> List.fold
                        (fun (inputs, constants) (moreInputs, moreConstants) ->
                            inputs @ moreInputs, constants @ moreConstants)
                        ([], [])

                selector :: inputs, constants

        let inputs, constants = walk [] [] entry.Body

        inputs
        |> List.mapi (fun order input ->
            { input with
                Binding = { input.Binding with Order = order }
            }),
        constants

    let synopsis entry =
        let placeholder =
            function
            | CatalogAtom.Text -> "VALUE"
            | CatalogAtom.Integer -> "N"
            | CatalogAtom.Choice options -> options |> List.map kebab |> String.concat "|"
            | CatalogAtom.Switch _ -> ""

        let inputs, _ = derive entry

        let arguments =
            inputs
            |> List.choose (fun input ->
                let spec = input.Spec

                match spec.Argument with
                | CatalogArgument.Implicit -> None
                | CatalogArgument.Operand ->
                    Some(kebab (inputKey spec.Name) |> _.ToUpperInvariant())
                | CatalogArgument.Named ->
                    let flag = "--" + kebab (inputKey spec.Name)
                    let suffix = placeholder spec.Atom
                    let text = if suffix = "" then flag else flag + " " + suffix

                    Some(
                        if spec.Required && input.Binding.Branches.IsEmpty then
                            text
                        else
                            "[" + text + "]"
                    ))

        String.concat " " (words entry @ arguments)

    // Adapter metadata is projected from the same tree, never authored independently.
    let private fieldInput input =
        match input.Binding.Field with
        | Some(InputTarget.Field BusinessField.CaseReference)
        | None -> None
        | Some field ->
            Some
                {
                    FieldName = field
                    Prefill =
                        input.Spec.Prefill
                        |> Option.map PrefillSource.CurrentField
                        |> Option.defaultValue PrefillSource.Blank
                }

    let caseDefinitions =
        all
        |> List.choose (fun operation ->
            match operation.Act with
            | Some(ActKind.Case kind) ->
                let inputs, _ = derive operation

                let groups =
                    inputs
                    |> List.choose (fun selector ->
                        match selector.Spec.Name with
                        | CatalogInputName.Group group ->
                            let actions =
                                match selector.Spec.Atom with
                                | CatalogAtom.Choice tokens ->
                                    tokens
                                    |> List.map (fun token ->
                                        CorrectionActions.all
                                        |> List.find (fun action ->
                                            CorrectionActions.token action = token))
                                | _ -> invalidOp "A correction selector must be a closed choice."

                            let fields =
                                inputs
                                |> List.filter (fun input ->
                                    input.Binding.Branches
                                    |> List.exists (fun branch ->
                                        branch.Path = selector.Binding.Path
                                        && branch.Value = "REPLACE"))
                                |> List.choose fieldInput

                            Some
                                {
                                    Name = group
                                    LabelId = inputMessage selector.Spec.Name "label"
                                    MeaningId = inputMessage selector.Spec.Name "meaning"
                                    Actions = actions
                                    ReplaceFields = fields
                                }
                        | _ -> None)

                Some
                    {
                        ActDefinition.Kind = kind
                        LabelId = labelId operation
                        MeaningId = meaningId operation
                        ConfirmationId = message operation "confirmation"
                        Inputs =
                            if groups.IsEmpty then
                                ActInputShape.Fields(inputs |> List.choose fieldInput)
                            else
                                ActInputShape.CorrectionGroups groups
                    }
            | _ -> None)

    let authorityTarget: AuthorityInputDefinition =
        {
            Name = TechnicalInputs.token TechnicalInput.Target
            LabelId = Vocabulary.messageKey TechnicalInputs.vocabulary TechnicalInput.Target "label"
            Primitive = AuthorityInputPrimitive.Principal
        }

    let authorityDefinitions =
        all
        |> List.choose (fun operation ->
            match operation.Act with
            | Some(ActKind.Authority kind) ->
                let inputs, _ = derive operation

                let authored =
                    inputs
                    |> List.choose (fun input ->
                        let primitive =
                            match input.Spec.Name with
                            | CatalogInputName.Technical TechnicalInput.Role ->
                                Some AuthorityInputPrimitive.Role
                            | CatalogInputName.Technical TechnicalInput.Scope ->
                                Some AuthorityInputPrimitive.Scope
                            | _ -> None

                        primitive
                        |> Option.map (fun primitive ->
                            {
                                Name = inputKey input.Spec.Name
                                LabelId = inputMessage input.Spec.Name "label"
                                Primitive = primitive
                            }))

                Some
                    {
                        AuthorityOperationDefinition.Kind = kind
                        LabelId = labelId operation
                        MeaningId = meaningId operation
                        ConfirmationId = message operation "confirmation"
                        Inputs = authored
                    }
            | _ -> None)

    let entries =
        all
        |> List.map (fun operation ->
            let inputs, constants = derive operation

            {
                Endpoint = operation.Endpoint
                Scope = words operation |> List.head |> _.ToUpperInvariant()
                Kind =
                    match operation.Act with
                    | Some act -> ActKinds.token act
                    | None -> operation.Endpoint
                LabelId = labelId operation
                MeaningId = meaningId operation
                ConfirmationId = confirmationId operation
                Synopsis = synopsis operation
                Inputs = inputs |> List.map _.Binding
                Constants = constants
            })

    let technicalFields =
        Vocabulary.all TechnicalInputs.vocabulary
        |> List.map (fun name ->
            {
                Name = TechnicalInputs.token name
                LabelId = Vocabulary.messageKey TechnicalInputs.vocabulary name "label"
            })

    let forCase kind =
        caseDefinitions |> List.find (fun entry -> entry.Kind = kind)
