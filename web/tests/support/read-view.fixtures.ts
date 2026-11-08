import type {
  ActorReviewItem,
  CaseTimelineEntry,
  PresentedSection,
  CaseFields,
  CaseSummary,
  CaseView,
  CurrencyTotal,
  OperationReceipt,
  PresentedEntry,
  PresentedValue,
  Screen,
  ResponseByEndpoint,
} from "@contracts/client";
import { isEndpointId, isResponse } from "../../src/protocol/client";

// Independently authored semantic facts for behavior fixtures. These builders never read a
// product descriptor, generated presentation output, or a saved implementation snapshot.
const empty: Screen = { regions: [] };
const absent: PresentedValue = { kind: "ABSENT", reasonId: "label.notRecorded" };
const text = (value: string | null | undefined): PresentedValue =>
  value === null || value === undefined ? absent : { kind: "TEXT", value };
const date = (value: string | null | undefined): PresentedValue =>
  value === null || value === undefined ? absent : { kind: "DATE", value };
const entry = (key: string, labelId: string, value: PresentedValue): PresentedEntry => ({
  key,
  labelId,
  prominence: "PRIMARY",
  value,
});
const record = (entries: PresentedEntry[]): Screen => ({
  regions: ([{ titleId: "label.recordedFacts", entries }] as const).map((section) => ({
    role: "FACTS" as const,
    titleId: section.titleId,
    content: { kind: "ENTRIES" as const, entries: section.entries },
  })),
});
const money = (
  amount: string | null | undefined,
  currency: string | null | undefined,
): PresentedValue =>
  amount === null || amount === undefined || currency === null || currency === undefined
    ? absent
    : {
        kind: "MONEY",
        amount,
        currency,
        displayScale: Math.max(2, amount.split(".")[1]?.length ?? 0),
      };
const fields = (value: CaseFields): PresentedEntry[] => [
  entry("claimantName", "field.claimantName.label", {
    kind: "PARTY",
    value: value.registration.claimantName,
  }),
  entry("insurerName", "field.insurerName.label", {
    kind: "PARTY",
    value: value.registration.insurerName,
  }),
  entry(
    "claimedAmount",
    "field.claimedAmount.label",
    money(value.registration.claimedAmount, value.registration.claimedCurrency),
  ),
  entry(
    "payableAmount",
    "field.payableAmount.label",
    money(value.decision?.payableAmount, value.decision?.payableCurrency),
  ),
  entry("incidentDate", "field.incidentDate.label", date(value.registration.incidentDate)),
  entry("incidentCountry", "field.incidentCountry.label", {
    kind: "COUNTRY",
    value: value.registration.incidentCountry,
  }),
  entry(
    "incidentNotificationDate",
    "field.incidentNotificationDate.label",
    date(value.registration.incidentNotificationDate),
  ),
  entry(
    "incidentDescription",
    "field.incidentDescription.label",
    value.registration.incidentDescription === null ||
      value.registration.incidentDescription === undefined
      ? absent
      : { kind: "NARRATIVE", value: value.registration.incidentDescription },
  ),
  entry(
    "paymentDecisionDate",
    "field.paymentDecisionDate.label",
    date(value.decision?.paymentDecisionDate),
  ),
  entry("paymentDate", "field.paymentDate.label", date(value.payment?.paymentDate)),
];
const caseRecord = (value: CaseView): Screen => ({
  regions: [
    {
      role: "IDENTITY",
      content: {
        kind: "ENTRIES",
        entries: [
          entry("caseReference", "field.caseReference.label", {
            kind: "REFERENCE",
            value: value.caseReference,
          }),
          ...(value.state.kind === "ERASED"
            ? []
            : [
                entry("headline", "field.status.label", {
                  kind: "STATE",
                  vocabulary: "headline",
                  token: value.headline,
                }),
              ]),
        ],
      },
    },
    ...(value.state.kind === "ERASED"
      ? [
          {
            role: "NOTICE" as const,
            content: {
              kind: "NOTICE" as const,
              messageId: "lifecycle.ERASED.meaning",
              tone: "FINAL" as const,
            },
          },
        ]
      : [
          {
            role: "FACTS" as const,
            content: {
              kind: "ENTRIES" as const,
              entries: [
                ...fields(value.state.fields),
                entry(
                  "responsible",
                  "field.responsibleActor.label",
                  value.work.responsible === null
                    ? absent
                    : { kind: "ACTOR", value: value.work.responsible },
                ),
                entry(
                  "handlingSummary",
                  "field.handlingSummary.label",
                  value.work.summary === null
                    ? absent
                    : { kind: "NARRATIVE", value: value.work.summary },
                ),
              ],
            },
          },
        ]),
    {
      role: "DETAILS",
      titleId: "label.recordDetails",
      content: {
        kind: "ENTRIES",
        entries: [
          entry("revision", "label.revisionLabel", { kind: "COUNT", value: value.revision }),
        ],
      },
    },
  ],
});

type Column<T> = {
  key: string;
  labelId: string;
  value: (row: T) => PresentedValue;
  isMoney?: boolean;
};
const table = <T>(columns: Column<T>[], rows: ReadonlyArray<T>): Screen => ({
  regions: [
    {
      role: "RESULTS",
      content: {
        kind: "TABLE",
        sections: [],
        columns: columns.map(({ key, labelId, isMoney = false }) => ({
          key,
          labelId,
          isMoney,
          prominence: "PRIMARY",
        })),
        rows: rows.map((row, index) => ({
          key: String(index),
          values: columns.map((column) => column.value(row)),
        })),
      },
    },
  ],
});
const actorColumns: Column<ActorReviewItem>[] = [
  {
    key: "actor",
    labelId: "field.profile.displayName.label",
    value: (row) => ({ kind: "ACTOR", value: row.actor }),
  },
  {
    key: "state",
    labelId: "label.accountState",
    value: (row) => ({ kind: "CODE", vocabulary: "accountState", token: row.state }),
  },
  {
    key: "roles",
    labelId: "field.role.label",
    value: (row) => ({
      kind: "CODES",
      vocabulary: "role",
      tokens: row.roles.map((role) => role.role),
    }),
  },
  { key: "contact", labelId: "field.profile.contact.label", value: (row) => text(row.contact) },
];
const caseColumns: Column<CaseSummary>[] = [
  {
    key: "headline",
    labelId: "field.status.label",
    value: (row) => ({ kind: "STATE", vocabulary: "headline", token: row.headline }),
  },
  {
    key: "responsible",
    labelId: "field.responsibleActor.label",
    value: (row) => (row.responsible === null ? absent : { kind: "ACTOR", value: row.responsible }),
  },
  {
    key: "nextAction",
    labelId: "field.reminderAction.label",
    value: (row) => text(row.nextAction?.action),
  },
  {
    key: "reference",
    labelId: "field.caseReference.label",
    value: (row) => ({ kind: "REFERENCE", value: row.caseReference }),
  },
  {
    key: "claimant",
    labelId: "field.claimantName.label",
    value: (row) =>
      row.state.kind === "ERASED"
        ? absent
        : { kind: "PARTY", value: row.state.fields.claimantName },
  },
  {
    key: "insurer",
    labelId: "field.insurerName.label",
    value: (row) =>
      row.state.kind === "ERASED" ? absent : { kind: "PARTY", value: row.state.fields.insurerName },
  },
  {
    key: "claimed",
    labelId: "field.claimedAmount.label",
    isMoney: true,
    value: (row) =>
      row.state.kind === "ERASED"
        ? absent
        : money(row.state.fields.claimedAmount, row.state.fields.claimedCurrency),
  },
];
type ReadId =
  | "case.get"
  | "case.list"
  | "case.timeline"
  | "case.summary"
  | "authority.list"
  | "authority.grants"
  | "authority.history"
  | "authority.requests"
  | "reminder.list"
  | "reminder.get";
type Data<K extends ReadId> = Extract<
  ResponseByEndpoint[K]["outcome"],
  { kind: "SUCCEEDED" }
>["data"];
const views: { [K in ReadId]: (data: Data<K>) => Screen } = {
  "case.get": caseRecord,
  "case.list": (data) => table(caseColumns, data.items),
  "case.summary": (data) => {
    const totals = (key: string, label: string, values: ReadonlyArray<CurrencyTotal>) =>
      values.map((value, index) =>
        entry(`${key}.${index}`, label, {
          kind: "MONEY_TOTAL",
          amount: value.amount,
          currency: value.currency,
          displayScale: Math.max(
            value.currency === "KWD" ? 3 : 2,
            value.amount.split(".")[1]?.length ?? 0,
          ),
        }),
      );
    return record([
      ...totals("claimed", "label.claimedTotals", data.claimed),
      ...totals("payable", "label.payableTotals", data.payable),
    ]);
  },
  "authority.list": (data) => table(actorColumns, data.items),
  "authority.grants": (data) =>
    table(
      [
        {
          key: "role",
          labelId: "field.role.label",
          value: (row) => ({ kind: "CODE", vocabulary: "role", token: row.role }),
        },
        {
          key: "scope",
          labelId: "input.scope.label",
          value: (row) => ({ kind: "CODE", vocabulary: "scope", token: row.scope.kind }),
        },
        {
          key: "case",
          labelId: "field.caseReference.label",
          value: (row) =>
            row.scope.kind === "CASE"
              ? { kind: "REFERENCE", value: row.scope.caseReference }
              : absent,
        },
      ],
      data.items,
    ),
  "authority.history": (data) =>
    table(
      [
        {
          key: "recordedAt",
          labelId: "label.recordedAt",
          value: (row) => ({ kind: "INSTANT", value: row.recordedAt }),
        },
        {
          key: "target",
          labelId: "label.targetAccount",
          value: (row) => ({ kind: "ACTOR", value: row.target }),
        },
        {
          key: "author",
          labelId: "label.recordedBy",
          value: (row) => ({ kind: "ACTOR", value: row.recordedBy }),
        },
        {
          key: "act",
          labelId: "label.action",
          value: (row) => ({ kind: "CODE", vocabulary: "act", token: row.act.kind }),
        },
        {
          key: "case",
          labelId: "field.caseReference.label",
          value: (row) =>
            (row.act.kind === "GRANT" || row.act.kind === "REVOKE") && row.act.scope.kind === "CASE"
              ? { kind: "REFERENCE", value: row.act.scope.caseReference }
              : absent,
        },
      ],
      data.entries,
    ),
  "authority.requests": (data) =>
    table(
      [
        { key: "name", labelId: "label.requestedName", value: (row) => text(row.claims.name) },
        {
          key: "username",
          labelId: "label.requestedUsername",
          value: (row) => text(row.claims.preferredUsername),
        },
        { key: "email", labelId: "label.requestedEmail", value: (row) => text(row.claims.email) },
        {
          key: "issuer",
          labelId: "field.principal.issuer.label",
          value: (row) => ({ kind: "IDENTIFIER", value: row.principal.issuer }),
        },
        {
          key: "subject",
          labelId: "field.principal.subject.label",
          value: (row) => ({ kind: "IDENTIFIER", value: row.principal.subject }),
        },
      ],
      data.items,
    ),
  "reminder.get": (data) =>
    record([entry("action", "field.reminderAction.label", text(data.item.reminder.action))]),
  "reminder.list": (data) =>
    table(
      [
        {
          key: "reference",
          labelId: "field.caseReference.label",
          value: (row) => ({ kind: "REFERENCE", value: row.caseReference }),
        },
        {
          key: "action",
          labelId: "field.reminderAction.label",
          value: (row) => text(row.reminder.action),
        },
        {
          key: "due",
          labelId: "field.reminderDueDate.label",
          value: (row) => date(row.reminder.dueDate),
        },
        {
          key: "state",
          labelId: "input.reminderState.label",
          value: (row) => ({
            kind: "CODE",
            vocabulary: "reminderState",
            token: row.reminder.state,
          }),
        },
        {
          key: "timing",
          labelId: "input.reminderDue.label",
          value: (row) => {
            if (!row.active) {
              return { kind: "CODE", vocabulary: "due", token: "INACTIVE" };
            }
            return row.timing === null
              ? absent
              : { kind: "CODE", vocabulary: "reminderDue", token: row.timing };
          },
        },
      ],
      data.items,
    ),
  "case.timeline": (data) =>
    table(
      [
        {
          key: "operationId",
          labelId: "field.operationId.label",
          value: (row) => ({ kind: "IDENTIFIER", value: row.operationId }),
        },
        {
          key: "author",
          labelId: "label.recordedBy",
          value: (row) => ({ kind: "ACTOR", value: row.recordedBy }),
        },
        {
          key: "act",
          labelId: "label.action",
          value: (row) => {
            if (row.kind === "REMINDER") {
              return { kind: "CODE", vocabulary: "reminderAct", token: row.act };
            }
            return row.kind === "WORK"
              ? { kind: "CODE", vocabulary: "workAct", token: row.act }
              : { kind: "CODE", vocabulary: "act", token: row.act };
          },
        },
        {
          key: "changes",
          labelId: "label.changes",
          value: (row) =>
            row.changes.kind === "DISCLOSED"
              ? { kind: "COUNT", value: row.changes.items.length }
              : {
                  kind: "MESSAGE",
                  messageId:
                    row.changes.kind === "ERASED" ? "label.changesErased" : "label.changesWithheld",
                },
        },
      ],
      data.items,
    ),
};
export const timelineFixtureView = (items: ReadonlyArray<CaseTimelineEntry>): Screen => ({
  regions: [
    {
      role: "ACTIVITY",
      content: {
        kind: "TIMELINE",
        entries: items.map((item) => {
          const sections: PresentedSection[] =
            item.changes.kind === "DISCLOSED"
              ? item.changes.items.flatMap((change) =>
                  change.kind === "CASE_FIELD"
                    ? [
                        {
                          titleId: `field.${change.change.field}.label`,
                          entries: [
                            entry(
                              "before",
                              "label.before",
                              change.change.before === null
                                ? absent
                                : { ...change.change.before, kind: "TEXT" },
                            ),
                            entry(
                              "after",
                              "label.after",
                              change.change.after === null
                                ? absent
                                : { ...change.change.after, kind: "TEXT" },
                            ),
                          ],
                        },
                      ]
                    : [],
                )
              : [];
          return {
            key: String(item.position.sequence),
            when: item.recordedAt,
            who: item.recordedBy,
            summary: {
              messageId:
                item.changes.kind === "DISCLOSED"
                  ? item.changes.overview.event.messageId
                  : "label.changesWithheld",
              arguments: [],
            },
            highlights: sections,
            details: [],
          };
        }),
      },
    },
  ],
});

const readView = <K extends ReadId>(endpoint: K, data: Data<K>): Screen => {
  const view = views[endpoint](data);
  if (!("basis" in data)) {
    return view;
  }
  const { basis } = data;
  const entries = [
    entry("businessDate", "label.businessDate", { kind: "DATE", value: basis.businessDate }),
    entry("timeZone", "label.timeZone", { kind: "TEXT", value: basis.timeZone }),
    entry("observedAt", "label.observedAt", { kind: "INSTANT", value: basis.observedAt }),
  ];
  return {
    regions: [
      ...view.regions,
      { role: "DETAILS", titleId: "label.recordDetails", content: { kind: "ENTRIES", entries } },
    ],
  };
};

const isRead = (value: string): value is ReadId => Object.hasOwn(views, value);
const receiptView = (data: OperationReceipt): Screen => {
  const entries = [
    entry("operationId", "field.operationId.label", {
      kind: "IDENTIFIER",
      value: data.operationId,
    }),
    entry("author", "label.recordedBy", { kind: "ACTOR", value: data.recordedBy }),
  ];
  if (data.subject.kind === "CASE") {
    const { snapshot } = data.subject;
    if (snapshot.kind === "DISCLOSED") {
      entries.push(...fields(snapshot.view.fields));
    } else {
      entries.push(
        entry("disclosure", "label.details", {
          kind: "MESSAGE",
          messageId:
            snapshot.kind === "ERASED" ? "lifecycle.ERASED.meaning" : "label.detailsWithheld",
        }),
      );
    }
  }
  return record(entries);
};
const timelineRead = (endpoint: string, read: unknown): Screen | null => {
  if (
    endpoint === "case.timeline" &&
    isResponse("case.timeline", read) &&
    read.outcome.kind === "SUCCEEDED"
  ) {
    return timelineFixtureView(read.outcome.data.items);
  }
  return null;
};
/** Synthetic HTTP writers use declared fixture facts; invalid wire inputs remain invalid. */
export const fixtureView = (endpoint: string, kind: string, data: unknown): Screen => {
  if (!isEndpointId(endpoint)) {
    return empty;
  }
  const reply = {
    operationId: "10000000-0000-7000-8000-000000000001",
    outcome: { kind, data, view: empty },
  };
  const read = { outcome: reply.outcome };
  const timeline = timelineRead(endpoint, read);
  if (timeline !== null) {
    return timeline;
  }
  if (isRead(endpoint) && isResponse(endpoint, read) && read.outcome.kind === "SUCCEEDED") {
    return readView(endpoint, read.outcome.data);
  }
  if (
    isResponse("operation.execute", reply) &&
    (reply.outcome.kind === "ACCEPTED" || reply.outcome.kind === "ALREADY_ACCEPTED")
  ) {
    return receiptView(reply.outcome.data);
  }
  if (isResponse("operation.get", reply) && reply.outcome.kind === "ACCEPTED") {
    return receiptView(reply.outcome.data.receipt);
  }
  return empty;
};

export const presentedOutcome = <K extends "SUCCEEDED" | "ACCEPTED" | "ALREADY_ACCEPTED", const T>(
  endpoint: string,
  kind: K,
  data: T,
) => ({ kind, data, view: fixtureView(endpoint, kind, data) });
