import {
  assembleOpenCase,
  assembleCaseOperation,
  assembleRegistrationAmendment,
  assembleCorrection,
  assembleRecordPayment,
  assembleWorkOperation,
  assembleNoteOperation,
  assembleReminderOperation,
  assemblePeopleOperation,
  assemblePeopleAdmission,
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
  type PeopleGrantInput,
  type PeopleAdmissionRow,
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
      readonly kind: "PEOPLE";
      readonly operationId: string;
      readonly principal?: PrincipalInput;
      readonly grant?: PeopleGrantInput;
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
  readonly submittedRows: Readonly<Partial<Record<"peopleGrants", ReadonlyArray<string>>>>;
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
    if (binding.kind === "PEOPLE" && binding.principal !== undefined) {
      return binding.principal;
    }
    return {
      kind: choice("input:principal.kind", taskChoices.principalKind),
      issuer: text("input:principal.issuer"),
      subject: text("input:principal.subject"),
    };
  };
  const grant = (rowValues: FormValues, row?: string): PeopleGrantInput => {
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
    if (definition.id === "case.open") {
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
      Exclude<SubjectTask<"CASE">["id"], "case.open">,
      () => TaskSubmission
    > = {
      "case.amendRegistration": () =>
        submit(assembleRegistrationAmendment(basis, registrationUpdate())),
      "case.correct": () => {
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
      "case.recordPayment": () =>
        submit(
          assembleRecordPayment(basis, {
            paymentDate: text("field:paymentDate"),
            paid: { amount: text("field:paidAmount"), currency: text("field:paidCurrency") },
          }),
        ),
      "case.decide": () =>
        submit(
          assembleCaseOperation(basis, {
            kind: "case.decide",
            values: {
              paymentDecisionDate: text("field:paymentDecisionDate"),
              payableAmount: text("field:payableAmount"),
              payableCurrency: text("field:payableCurrency"),
            },
          }),
        ),
      "case.removePayment": () => {
        if (binding.paymentId === undefined) {
          throw new TaskInputError("input:paymentId");
        }
        return submit(
          assembleCaseOperation(basis, {
            kind: "case.removePayment",
            paymentId: binding.paymentId,
          }),
        );
      },
      "case.void": () =>
        submit(assembleCaseOperation(basis, { kind: "case.void", reason: text("input:reason") })),
      "case.reinstate": () =>
        submit(
          assembleCaseOperation(basis, { kind: "case.reinstate", reason: text("input:reason") }),
        ),
      "case.withdrawDecision": () =>
        submit(assembleCaseOperation(basis, { kind: "case.withdrawDecision" })),
      "case.close": () => submit(assembleCaseOperation(basis, { kind: "case.close" })),
      "case.reopen": () => submit(assembleCaseOperation(basis, { kind: "case.reopen" })),
      "case.erase": () => submit(assembleCaseOperation(basis, { kind: "case.erase" })),
    };
    return builders[definition.id]();
  };
  const assembleWorkTask = (definition: SubjectTask<"WORK">): TaskSubmission => {
    switch (definition.id) {
      case "work.update": {
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
            kind: "work.update",
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
      "note.add": () =>
        submit(assembleNoteOperation(binding, { kind: "note.add", text: text("input:noteText") })),
      "note.remove": () =>
        submit(
          assembleNoteOperation(binding, {
            kind: "note.remove",
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
    if (definition.id === "reminder.create") {
      return submit(
        assembleReminderOperation(basis, {
          kind: "reminder.create",
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
      case "reminder.update":
        return submit(
          assembleReminderOperation(basis, {
            kind: "reminder.update",
            reminderId,
            expectedRevision: revision,
            action: text("input:reminderAction"),
            dueDate: text("input:reminderDueDate"),
          }),
        );
      case "reminder.complete":
        return submit(
          assembleReminderOperation(basis, {
            kind: "reminder.complete",
            reminderId,
            expectedRevision: revision,
          }),
        );
      case "reminder.cancel":
        return submit(
          assembleReminderOperation(basis, {
            kind: "reminder.cancel",
            reminderId,
            expectedRevision: revision,
          }),
        );
      case "reminder.reopen":
        return submit(
          assembleReminderOperation(basis, {
            kind: "reminder.reopen",
            reminderId,
            expectedRevision: revision,
            dueDate: text("input:reminderDueDate"),
          }),
        );
      case "reminder.completeAndPlanNext":
        return submit(
          assembleReminderOperation(basis, {
            kind: "reminder.completeAndPlanNext",
            reminderId,
            expectedRevision: revision,
            action: text("input:reminderAction"),
            dueDate: text("input:reminderDueDate"),
          }),
        );
    }
  };
  const assemblePeopleTask = (definition: SubjectTask<"PEOPLE">): TaskSubmission => {
    if (binding.kind !== "PEOPLE") {
      throw new Error("The task requires its people target binding.");
    }
    const target = principal();
    const builders: Record<SubjectTask<"PEOPLE">["id"], () => TaskSubmission> = {
      "people.admit": () => {
        const collection = values["peopleGrants"];
        if (!isFormCollection(collection)) {
          throw new TaskInputError("input:grants");
        }
        const declaration = definition.collections.find(
          (candidate) => candidate.id === "peopleGrants",
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
        const rows: PeopleAdmissionRow[] = collection.rows.map((row) => ({
          key: row.key,
          grant: grant(row.values, row.key),
        }));
        const assembled = assemblePeopleAdmission(binding.operationId, target, profile(), rows);
        return {
          command: assembled.command,
          submittedRows: Object.freeze(assembled.submittedRows),
        };
      },
      "people.register": () =>
        submit(
          assemblePeopleOperation(binding.operationId, target, {
            kind: "people.register",
            profile: profile(),
          }),
        ),
      "people.setProfile": () =>
        submit(
          assemblePeopleOperation(binding.operationId, target, {
            kind: "people.setProfile",
            profile: profile(),
          }),
        ),
      "people.grant": () =>
        submit(
          assemblePeopleOperation(binding.operationId, target, {
            kind: "people.grant",
            ...grant(values),
          }),
        ),
      "people.revoke": () =>
        submit(
          assemblePeopleOperation(binding.operationId, target, {
            kind: "people.revoke",
            ...(binding.grant ?? grant(values)),
          }),
        ),
      "people.enable": () =>
        submit(assemblePeopleOperation(binding.operationId, target, { kind: "people.enable" })),
      "people.disable": () =>
        submit(assemblePeopleOperation(binding.operationId, target, { kind: "people.disable" })),
      "people.eraseProfile": () =>
        submit(
          assemblePeopleOperation(binding.operationId, target, { kind: "people.eraseProfile" }),
        ),
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
    case "PEOPLE":
      return assemblePeopleTask(task);
  }
};
