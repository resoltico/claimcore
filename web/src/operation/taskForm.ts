import {
  assembleOpenCase,
  assembleCaseOperation,
  assembleRegistrationAmendment,
  assembleCorrection,
  assembleRecordPayment,
  assembleWorkOperation,
  assembleNoteOperation,
  assembleReminderOperation,
  assembleAuthorityOperation,
  assembleAuthorityAdmission,
  taskChoices,
  type OperationTaskId,
  type OperationTaskDefinition,
  type OperationInput,
  type CaseSelector,
  type PrincipalInput,
  type TaskInputId,
  type TaskUpdate,
  type OptionalTaskUpdate,
  type Registration,
  type RegistrationAmendmentDraft,
  type DecisionCorrectionDraft,
  type TaskMoneyInput,
  type AuthorityGrantInput,
  type AuthorityAdmissionRow,
} from "@contracts/client";
import { operationTask } from "../protocol/taskCatalog";
import { isFormCollection, type FormValues } from "../protocol/forms";

export type TaskBinding =
  | { readonly kind: "NEW_CASE"; readonly operationId: string }
  | {
      readonly kind: "CASE";
      readonly operationId: string;
      readonly target: CaseSelector;
      readonly revision: number;
      readonly paymentId?: string;
    }
  | {
      readonly kind: "WORK";
      readonly operationId: string;
      readonly target: CaseSelector;
      readonly workRevision: number;
    }
  | {
      readonly kind: "NOTE";
      readonly operationId: string;
      readonly target: CaseSelector;
      readonly noteId?: string;
    }
  | {
      readonly kind: "REMINDER";
      readonly operationId: string;
      readonly target: CaseSelector;
      readonly reminder?: { readonly reminderId: string; readonly revision: number };
    }
  | {
      readonly kind: "AUTHORITY";
      readonly operationId: string;
      readonly principal?: PrincipalInput;
      readonly grant?: AuthorityGrantInput;
    };

export type TaskBindingSource = TaskBinding extends infer B
  ? B extends TaskBinding
    ? Omit<B, "operationId">
    : never
  : never;

type SubjectTask<S extends OperationTaskDefinition["subject"]> = Extract<
  OperationTaskDefinition,
  { readonly subject: S }
>;

export type TaskSubmission = {
  readonly command: OperationInput;
  readonly submittedRows: Readonly<Partial<Record<"authorityGrants", ReadonlyArray<string>>>>;
};

export class TaskInputError extends Error {
  readonly input: TaskInputId;
  readonly row: string | undefined;
  constructor(input: TaskInputId, row?: string) {
    super("An authored task input is incomplete.");
    this.input = input;
    this.row = row;
  }
}

/** Acquired values have semantic keys. Selection, clearing and submitted row identity stay structured. */
export const assembleTask = (
  id: OperationTaskId,
  values: FormValues,
  binding: TaskBinding,
): TaskSubmission => {
  const task = operationTask(id);
  const has = (input: TaskInputId) => Object.hasOwn(values, input);
  const text = (input: TaskInputId): string => {
    const value = values[input];
    if (typeof value !== "string") {
      throw new TaskInputError(input);
    }
    return value;
  };
  const optionalText = (input: TaskInputId) => {
    const value = values[input];
    if (value === undefined || value === null) {
      return value;
    }
    if (typeof value !== "string") {
      throw new TaskInputError(input);
    }
    return value;
  };
  const choice = <T extends string>(
    input: TaskInputId,
    choices: ReadonlyArray<T>,
    fallback?: T,
  ): T => {
    const value = values[input] ?? fallback;
    const selected = choices.find((candidate) => candidate === value);
    if (selected === undefined) {
      throw new TaskInputError(input);
    }
    return selected;
  };
  const selected = (input: TaskInputId): TaskUpdate<string> =>
    has(input) ? { kind: "SET", value: text(input) } : { kind: "KEEP" };
  const optional = (input: TaskInputId): OptionalTaskUpdate<string> => {
    if (has(input)) {
      return values[input] === null ? { kind: "CLEAR" } : { kind: "SET", value: text(input) };
    }
    return { kind: "KEEP" };
  };
  const money = (amount: TaskInputId, currency: TaskInputId): TaskUpdate<TaskMoneyInput> =>
    !has(amount) && !has(currency)
      ? { kind: "KEEP" }
      : { kind: "SET", value: { amount: text(amount), currency: text(currency) } };
  const registrationUpdate = (): RegistrationAmendmentDraft => ({
    incidentDate: selected("field:incidentDate"),
    incidentNotificationDate: selected("field:incidentNotificationDate"),
    incidentCountry: selected("field:incidentCountry"),
    incidentDescription: optional("field:incidentDescription"),
    policyNumber: optional("field:policyNumber"),
    insurerClaimReference: optional("field:insurerClaimReference"),
    claimantName: selected("field:claimantName"),
    insurerName: selected("field:insurerName"),
    claimed: money("field:claimedAmount", "field:claimedCurrency"),
  });
  const decisionUpdate = (): DecisionCorrectionDraft => ({
    paymentDecisionDate: selected("field:paymentDecisionDate"),
    payable: money("field:payableAmount", "field:payableCurrency"),
  });
  const registration = (): Registration => {
    const description = optionalText("field:incidentDescription");
    const policy = optionalText("field:policyNumber");
    const reference = optionalText("field:insurerClaimReference");
    return {
      incidentDate: text("field:incidentDate"),
      incidentNotificationDate: text("field:incidentNotificationDate"),
      incidentCountry: text("field:incidentCountry"),
      claimantName: text("field:claimantName"),
      insurerName: text("field:insurerName"),
      claimedAmount: text("field:claimedAmount"),
      claimedCurrency: text("field:claimedCurrency"),
      ...(description === undefined ? {} : { incidentDescription: description }),
      ...(policy === undefined ? {} : { policyNumber: policy }),
      ...(reference === undefined ? {} : { insurerClaimReference: reference }),
    };
  };
  const profile = () => {
    const contact = optionalText("input:profile.contact");
    return {
      displayName: text("input:profile.displayName"),
      ...(contact === undefined ? {} : { contact }),
    };
  };
  const principal = (): PrincipalInput => {
    if (binding.kind === "AUTHORITY" && binding.principal !== undefined) {
      return binding.principal;
    }
    return {
      kind: choice("input:principal.kind", taskChoices.principalKind),
      issuer: text("input:principal.issuer"),
      subject: text("input:principal.subject"),
    };
  };
  const grant = (rowValues: FormValues, row?: string): AuthorityGrantInput => {
    const role = taskChoices.role.find((candidate) => candidate === rowValues["input:role"]);
    const scope = taskChoices.scope.find(
      (candidate) => candidate === (rowValues["input:scope"] ?? "INSTALLATION"),
    );
    if (role === undefined) {
      throw new TaskInputError("input:role", row);
    }
    if (scope === undefined) {
      throw new TaskInputError("input:scope", row);
    }
    if (scope === "INSTALLATION") {
      return { role, scope: { kind: "INSTALLATION" } };
    }
    // Selecting case scope exposes a required blank input before any text-change event.
    const authoredReference = rowValues["field:caseReference"];
    const reference = authoredReference === undefined ? "" : authoredReference;
    if (typeof reference !== "string") {
      throw new TaskInputError("field:caseReference", row);
    }
    return {
      role,
      scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: reference } },
    };
  };
  const submit = (command: OperationInput): TaskSubmission => ({
    command,
    submittedRows: Object.freeze({}),
  });
  const assembleCaseTask = (definition: SubjectTask<"CASE">): TaskSubmission => {
    if (definition.id === "CASE:OPEN") {
      if (binding.kind !== "NEW_CASE") {
        throw new Error("Case creation requires an authored new-case binding.");
      }
      return submit(
        assembleOpenCase(binding.operationId, text("field:caseReference"), registration()),
      );
    }
    if (binding.kind !== "CASE") {
      throw new Error("The task requires its case record binding.");
    }
    const basis = {
      operationId: binding.operationId,
      target: binding.target,
      expectedRevision: binding.revision,
    };
    const builders: Record<
      Exclude<SubjectTask<"CASE">["id"], "CASE:OPEN">,
      () => TaskSubmission
    > = {
      "CASE:AMEND_REGISTRATION": () =>
        submit(assembleRegistrationAmendment(basis, registrationUpdate())),
      "CASE:CORRECT": () => {
        const registrationMode = choice(
          "group:registration",
          taskChoices.registrationCorrection,
          "KEEP",
        );
        const decisionGroup = () => {
          const mode = choice("group:decision", taskChoices.decisionCorrection, "KEEP");
          if (mode === "KEEP") {
            return { kind: "KEEP" } as const;
          }
          if (mode === "CLEAR") {
            return { kind: "CLEAR" } as const;
          }
          return { kind: "UPDATE", draft: decisionUpdate() } as const;
        };
        return submit(
          assembleCorrection(basis, {
            registration:
              registrationMode === "KEEP"
                ? { kind: "KEEP" }
                : { kind: "UPDATE", draft: registrationUpdate() },
            decision: decisionGroup(),
          }),
        );
      },
      "CASE:RECORD_PAYMENT": () =>
        submit(
          assembleRecordPayment(basis, {
            paymentDate: text("field:paymentDate"),
            paid: { amount: text("field:paidAmount"), currency: text("field:paidCurrency") },
          }),
        ),
      "CASE:DECIDE": () =>
        submit(
          assembleCaseOperation(basis, {
            kind: "DECIDE",
            values: {
              paymentDecisionDate: text("field:paymentDecisionDate"),
              payableAmount: text("field:payableAmount"),
              payableCurrency: text("field:payableCurrency"),
            },
          }),
        ),
      "CASE:REMOVE_PAYMENT": () => {
        if (binding.paymentId === undefined) {
          throw new TaskInputError("input:paymentId");
        }
        return submit(
          assembleCaseOperation(basis, { kind: "REMOVE_PAYMENT", paymentId: binding.paymentId }),
        );
      },
      "CASE:VOID": () =>
        submit(assembleCaseOperation(basis, { kind: "VOID", reason: text("input:reason") })),
      "CASE:REINSTATE": () =>
        submit(assembleCaseOperation(basis, { kind: "REINSTATE", reason: text("input:reason") })),
      "CASE:WITHDRAW_DECISION": () =>
        submit(assembleCaseOperation(basis, { kind: "WITHDRAW_DECISION" })),
      "CASE:CLOSE": () => submit(assembleCaseOperation(basis, { kind: "CLOSE" })),
      "CASE:REOPEN": () => submit(assembleCaseOperation(basis, { kind: "REOPEN" })),
      "CASE:ERASE": () => submit(assembleCaseOperation(basis, { kind: "ERASE" })),
    };
    return builders[definition.id]();
  };
  const assembleWorkTask = (definition: SubjectTask<"WORK">): TaskSubmission => {
    switch (definition.id) {
      case "WORK:UPDATE_WORK": {
        if (binding.kind !== "WORK") {
          throw new Error("The task requires its case work binding.");
        }
        const action = values["input:responsibleActorAction"];
        const responsible =
          action === undefined || action === null || action === ""
            ? undefined
            : choice("input:responsibleActorAction", taskChoices.workChange);
        const summary = values["field:handlingSummary"];
        return submit(
          assembleWorkOperation(binding, {
            kind: "UPDATE_WORK",
            values: {
              ...(responsible === undefined
                ? {}
                : {
                    responsibleActor:
                      responsible === "CLEAR"
                        ? { kind: "CLEAR" }
                        : { kind: "SET", value: text("field:responsibleActor") },
                  }),
              ...(summary === undefined
                ? {}
                : { handlingSummary: summary === null ? null : text("field:handlingSummary") }),
            },
          }),
        );
      }
    }
  };
  const assembleNoteTask = (definition: SubjectTask<"NOTE">): TaskSubmission => {
    if (binding.kind !== "NOTE") {
      throw new Error("The task requires its note parent binding.");
    }
    const builders: Record<SubjectTask<"NOTE">["id"], () => TaskSubmission> = {
      "NOTE:ADD_NOTE": () =>
        submit(assembleNoteOperation(binding, { kind: "ADD_NOTE", text: text("input:noteText") })),
      "NOTE:REMOVE_NOTE": () =>
        submit(
          assembleNoteOperation(binding, {
            kind: "REMOVE_NOTE",
            noteId:
              binding.noteId ??
              (() => {
                throw new TaskInputError("input:noteId");
              })(),
          }),
        ),
    };
    return builders[definition.id]();
  };
  const assembleReminderTask = (definition: SubjectTask<"REMINDER">): TaskSubmission => {
    if (binding.kind !== "REMINDER") {
      throw new Error("The task requires its reminder parent binding.");
    }
    const basis = { operationId: binding.operationId, target: binding.target };
    if (definition.id === "REMINDER:REMINDER_CREATE") {
      return submit(
        assembleReminderOperation(basis, {
          kind: "CREATE",
          action: text("input:reminderAction"),
          dueDate: text("input:reminderDueDate"),
        }),
      );
    }
    if (binding.reminder === undefined) {
      throw new TaskInputError("input:reminderId");
    }
    const { reminderId, revision } = binding.reminder;
    switch (definition.id) {
      case "REMINDER:REMINDER_UPDATE":
        return submit(
          assembleReminderOperation(basis, {
            kind: "UPDATE",
            reminderId,
            expectedRevision: revision,
            action: text("input:reminderAction"),
            dueDate: text("input:reminderDueDate"),
          }),
        );
      case "REMINDER:REMINDER_COMPLETE":
        return submit(
          assembleReminderOperation(basis, {
            kind: "COMPLETE",
            reminderId,
            expectedRevision: revision,
          }),
        );
      case "REMINDER:REMINDER_CANCEL":
        return submit(
          assembleReminderOperation(basis, {
            kind: "CANCEL",
            reminderId,
            expectedRevision: revision,
          }),
        );
      case "REMINDER:REMINDER_REOPEN":
        return submit(
          assembleReminderOperation(basis, {
            kind: "REOPEN",
            reminderId,
            expectedRevision: revision,
            dueDate: text("input:reminderDueDate"),
          }),
        );
      case "REMINDER:REMINDER_COMPLETE_AND_PLAN_NEXT":
        return submit(
          assembleReminderOperation(basis, {
            kind: "COMPLETE_AND_PLAN_NEXT",
            reminderId,
            expectedRevision: revision,
            action: text("input:reminderAction"),
            dueDate: text("input:reminderDueDate"),
          }),
        );
    }
  };
  const assembleAuthorityTask = (definition: SubjectTask<"AUTHORITY">): TaskSubmission => {
    if (binding.kind !== "AUTHORITY") {
      throw new Error("The task requires its authority target binding.");
    }
    const target = principal();
    const builders: Record<SubjectTask<"AUTHORITY">["id"], () => TaskSubmission> = {
      "AUTHORITY:ADMIT": () => {
        const collection = values["authorityGrants"];
        if (!isFormCollection(collection)) {
          throw new TaskInputError("input:grants");
        }
        const declaration = definition.collections.find(
          (candidate) => candidate.id === "authorityGrants",
        );
        if (
          declaration === undefined ||
          collection.rows.length < declaration.minimumRows ||
          collection.rows.length > declaration.maximumRows
        ) {
          throw new TaskInputError("input:grants");
        }
        if (new Set(collection.rows.map((row) => row.key)).size !== collection.rows.length) {
          throw new TaskInputError("input:grants");
        }
        const rows: AuthorityAdmissionRow[] = collection.rows.map((row) => ({
          key: row.key,
          grant: grant(row.values, row.key),
        }));
        const assembled = assembleAuthorityAdmission(binding.operationId, target, profile(), rows);
        return {
          command: assembled.command,
          submittedRows: Object.freeze(assembled.submittedRows),
        };
      },
      "AUTHORITY:REGISTER": () =>
        submit(
          assembleAuthorityOperation(binding.operationId, target, {
            kind: "REGISTER",
            profile: profile(),
          }),
        ),
      "AUTHORITY:SET_PROFILE": () =>
        submit(
          assembleAuthorityOperation(binding.operationId, target, {
            kind: "SET_PROFILE",
            profile: profile(),
          }),
        ),
      "AUTHORITY:GRANT": () =>
        submit(
          assembleAuthorityOperation(binding.operationId, target, {
            kind: "GRANT",
            ...grant(values),
          }),
        ),
      "AUTHORITY:REVOKE": () =>
        submit(
          assembleAuthorityOperation(binding.operationId, target, {
            kind: "REVOKE",
            ...(binding.grant ?? grant(values)),
          }),
        ),
      "AUTHORITY:ENABLE": () =>
        submit(assembleAuthorityOperation(binding.operationId, target, { kind: "ENABLE" })),
      "AUTHORITY:DISABLE": () =>
        submit(assembleAuthorityOperation(binding.operationId, target, { kind: "DISABLE" })),
      "AUTHORITY:ERASE_PROFILE": () =>
        submit(assembleAuthorityOperation(binding.operationId, target, { kind: "ERASE_PROFILE" })),
    };
    return builders[definition.id]();
  };
  switch (task.subject) {
    case "CASE":
      return assembleCaseTask(task);
    case "WORK":
      return assembleWorkTask(task);
    case "NOTE":
      return assembleNoteTask(task);
    case "REMINDER":
      return assembleReminderTask(task);
    case "AUTHORITY":
      return assembleAuthorityTask(task);
  }
};
