import type {
  ActorReviewItem,
  CaseTimelineEntry,
  PresentedSection,
  CaseFields,
  CaseSummary,
  CaseView,
  CurrencyTotal,
  OperationReceipt,
  ReminderReadModel,
  PresentedEntry,
  PresentedValue,
  Screen,
  DataResponseByEndpoint,
} from "@contracts/client";
import { isEndpointId, isResponseFor } from "../../src/protocol/client";

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
    money(value.finance.decision?.payableAmount, value.finance.decision?.payableCurrency),
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
    date(value.finance.decision?.paymentDecisionDate),
  ),
];
const reminderTiming = (item: ReminderReadModel): PresentedValue => {
  if (!item.active) {
    return { kind: "CODE", vocabulary: "due", token: "INACTIVE" };
  }
  if (item.timing === null) {
    return absent;
  }
  return { kind: "CODE", vocabulary: "reminderDue", token: item.timing };
};
const paymentFixtureRegion = (
  liveFields: CaseFields | undefined,
  payments: CaseView["latestPayments"],
): Screen["regions"][number] => ({
  role: "PAYMENTS" as const,
  titleId: "label.paymentDecisionAndPayments",
  content: {
    kind: "TABLE" as const,
    columns: [
      {
        key: "paymentDate",
        labelId: "field.paymentDate.label",
        prominence: "PRIMARY" as const,
        isMoney: false,
      },
      {
        key: "paidAmount",
        labelId: "field.paidAmount.label",
        prominence: "PRIMARY" as const,
        isMoney: true,
      },
    ],
    rows: payments.map((payment) => ({
      key: payment.paymentId,
      values: [
        date(payment.paymentDate),
        money(payment.paidAmount, liveFields?.finance.decision?.payableCurrency),
      ],
    })),
    sections: [
      {
        titleId: "label.paymentTotals",
        entries: [
          entry(
            "paymentDecisionDate",
            "field.paymentDecisionDate.label",
            date(liveFields?.finance.decision?.paymentDecisionDate),
          ),
          entry(
            "payable",
            "field.payableAmount.label",
            money(
              liveFields?.finance.decision?.payableAmount,
              liveFields?.finance.decision?.payableCurrency,
            ),
          ),
        ],
      },
    ],
  },
});

// Independently authored screen inputs for the browser's structural rendering tests.
const caseRecord = (value: CaseView): Screen => {
  const live = value.state.kind !== "ERASED";
  const liveFields = value.state.kind === "ERASED" ? undefined : value.state.fields;
  const { work } = value;
  const acts = value.advice.acts.filter((act) => act !== value.advice.recommended);
  const business = acts.filter((act) => !["VOID", "REINSTATE", "ERASE"].includes(act));
  const stewardship = acts.filter((act) => ["VOID", "REINSTATE", "ERASE"].includes(act));
  const facts =
    value.state.kind === "ERASED"
      ? []
      : fields(value.state.fields).filter(
          (field) => !["payableAmount", "paymentDecisionDate"].includes(field.key),
        );
  return {
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
            ...(live
              ? [
                  entry("headline", "field.status.label", {
                    kind: "STATE",
                    vocabulary: "headline",
                    token: value.headline,
                  }),
                ]
              : []),
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
              role: "ATTENTION" as const,
              content: {
                kind: "CODES" as const,
                vocabulary: "attention" as const,
                tokens: value.attention,
              },
            },
            ...(value.stages.kind === "TRACKER"
              ? [
                  {
                    role: "PROGRESS" as const,
                    content: {
                      kind: "STAGES" as const,
                      current: value.stages.current,
                      steps: value.stages.steps.map((step) => ({
                        stage: step.stage,
                        reached: step.completed,
                        date: step.date,
                        amount: step.amount === null ? null : { ...step.amount, displayScale: 4 },
                      })),
                    },
                  },
                ]
              : []),
            {
              role: "RECOMMENDATION" as const,
              content: {
                kind: "ACTIONS" as const,
                recommended: value.advice.recommended,
                groups: [
                  ...(business.length === 0
                    ? []
                    : [{ family: "BUSINESS" as const, acts: business }]),
                  ...(stewardship.length === 0
                    ? []
                    : [{ family: "STEWARDSHIP" as const, acts: stewardship }]),
                ],
              },
            },
            {
              role: "FACTS" as const,
              titleId: "group.registration.label",
              content: { kind: "ENTRIES" as const, entries: facts },
            },
            paymentFixtureRegion(liveFields, value.latestPayments),
            {
              role: "WORK" as const,
              titleId: "section.work.label",
              content: {
                kind: "ENTRIES" as const,
                entries: [
                  entry(
                    "responsible",
                    "field.responsibleActor.label",
                    work.responsible === null ? absent : { kind: "ACTOR", value: work.responsible },
                  ),
                  entry(
                    "handlingSummary",
                    "field.handlingSummary.label",
                    work.summary === null ? absent : { kind: "NARRATIVE", value: work.summary },
                  ),
                  entry(
                    "summaryChangedAt",
                    "label.summaryChangedAt",
                    work.summaryChangedAt === null
                      ? absent
                      : { kind: "INSTANT", value: work.summaryChangedAt },
                  ),
                  entry(
                    "summaryRecordedBy",
                    "label.summaryRecordedBy",
                    work.summaryRecordedBy === null
                      ? absent
                      : { kind: "ACTOR", value: work.summaryRecordedBy },
                  ),
                ],
              },
            },
            {
              role: "NEXT_ACTIONS" as const,
              titleId: "label.nextActions",
              content: {
                kind: "TABLE" as const,
                columns: [
                  {
                    key: "action",
                    labelId: "field.reminderAction.label",
                    prominence: "PRIMARY" as const,
                    isMoney: false,
                  },
                  {
                    key: "due",
                    labelId: "field.reminderDueDate.label",
                    prominence: "PRIMARY" as const,
                    isMoney: false,
                  },
                  {
                    key: "timing",
                    labelId: "input.reminderDue.label",
                    prominence: "SECONDARY" as const,
                    isMoney: false,
                  },
                ],
                rows: value.nextActions.map((item) => ({
                  key: item.reminder.reminderId,
                  values: [
                    text(item.reminder.action),
                    date(item.reminder.dueDate),
                    reminderTiming(item),
                  ],
                })),
              },
            },
          ]),
      {
        role: "DETAILS",
        titleId: "label.recordDetails",
        content: {
          kind: "ENTRIES",
          entries: [
            entry("revision", "label.revisionLabel", {
              kind: "IDENTIFIER",
              value: String(value.revision),
            }),
          ],
        },
      },
    ],
  };
};

type Column<T> = {
  key: string;
  labelId: string;
  value: (row: T) => PresentedValue;
  isMoney?: boolean;
};
const table = <T>(
  columns: Column<T>[],
  rows: ReadonlyArray<T>,
  identity: (row: T, index: number) => string = (_row, index) => String(index),
): Screen => ({
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
          key: identity(row, index),
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
    key: "caseReference",
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
  | "context"
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
  DataResponseByEndpoint[K]["outcome"],
  { kind: "SUCCEEDED" }
>["data"];
const views: { [K in ReadId]: (data: Data<K>) => Screen } = {
  context: (data) => {
    const actor = data.requester;
    return {
      regions: [
        {
          role: "IDENTITY",
          content: {
            kind: "ENTRIES",
            entries:
              actor.kind === "ACTIVE"
                ? [
                    entry("self", "label.account", { kind: "ACTOR", value: actor.self }),
                    entry("roles", "field.role.label", {
                      kind: "CODES",
                      vocabulary: "role",
                      tokens: actor.roles.map((role) => role.role),
                    }),
                  ]
                : actor.kind === "NOT_REGISTERED"
                  ? [
                      ...(actor.claims.name === undefined
                        ? []
                        : [
                            entry("name", "field.profile.displayName.label", {
                              kind: "TEXT",
                              value: actor.claims.name,
                            }),
                          ]),
                    ]
                  : [entry("self", "label.account", { kind: "ACTOR", value: actor.self })],
          },
        },
        ...(actor.kind === "NOT_REGISTERED"
          ? [
              {
                role: "DETAILS" as const,
                titleId: "label.recordDetails",
                content: {
                  kind: "ENTRIES" as const,
                  entries: [
                    entry("issuer", "field.principal.issuer.label", {
                      kind: "IDENTIFIER",
                      value: actor.issuer,
                    }),
                    entry("subject", "field.principal.subject.label", {
                      kind: "IDENTIFIER",
                      value: actor.subject,
                    }),
                  ],
                },
              },
            ]
          : []),
        ...(actor.kind === "ACTIVE"
          ? [
              {
                role: "FACTS" as const,
                content: {
                  kind: "ENTRIES" as const,
                  entries: [
                    entry("businessDate", "label.businessDate", date(actor.businessDate)),
                    entry("timeZone", "label.timeZone", text(actor.timeZone)),
                  ],
                },
              },
            ]
          : []),
      ],
    };
  },
  "case.get": caseRecord,
  "case.list": (data) => {
    const result = table(caseColumns, data.items, (row) => row.caseId);
    return {
      regions: result.regions.map((region) => {
        if (region.content.kind !== "TABLE" || data.exactReference === null) {
          return region;
        }
        return {
          role: region.role,
          content: {
            ...region.content,
            sections: [
              {
                titleId: "label.exactReferenceMatch",
                entries: [
                  entry("exactReference", "field.caseReference.label", {
                    kind: "REFERENCE",
                    value: data.exactReference.caseReference,
                  }),
                ],
              },
            ],
          },
        };
      }),
    };
  },
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
    return {
      regions: [
        {
          role: "TOTALS",
          content: {
            kind: "ENTRIES",
            entries: [
              entry("total", "label.total", { kind: "COUNT", value: data.counts.total }),
              ...totals("claimed", "label.claimedTotals", data.claimed),
              ...totals("payable", "label.payableTotals", data.payable),
            ],
          },
        },
      ],
    };
  },
  "authority.list": (data) => table(actorColumns, data.items, (row) => row.actor.actorId),
  "authority.grants": (data) => {
    const access = table(
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
        {
          key: "grantedAt",
          labelId: "label.grantedAt",
          value: (row) => ({ kind: "INSTANT", value: row.grantedAt }),
        },
        {
          key: "grantedBy",
          labelId: "label.grantedBy",
          value: (row) => ({ kind: "ACTOR", value: row.grantedBy }),
        },
      ],
      data.items,
    ).regions.map((region) => ({ content: region.content, role: "ACCESS" as const }));
    const { actor } = data;
    return {
      regions: [
        {
          role: "ACCOUNT",
          content: {
            kind: "ENTRIES",
            entries: [
              entry("actor", "field.profile.displayName.label", {
                kind: "ACTOR",
                value: actor.actor,
              }),
              entry("state", "label.accountState", {
                kind: "CODE",
                vocabulary: "accountState",
                token: actor.state,
              }),
              entry("kind", "field.principal.kind.label", {
                kind: "CODE",
                vocabulary: "principalKind",
                token: actor.principal.kind,
              }),
              entry("contact", "field.profile.contact.label", text(actor.contact)),
              ...(actor.responsibleCases === undefined
                ? []
                : [
                    entry("responsibleCases", "label.responsibleCases", {
                      kind: "COUNT",
                      value: actor.responsibleCases.count,
                    }),
                  ]),
            ],
          },
        },
        ...access,
      ],
    };
  },
  "authority.history": (data) => ({
    regions: [
      {
        role: "ACTIVITY",
        content: {
          kind: "TIMELINE",
          entries: data.entries.map((row) => ({
            key: `${row.position.sequence}:${row.position.effectIndex}`,
            when: row.recordedAt,
            who: row.recordedBy,
            summary: { messageId: `act.${row.act.kind}.label`, arguments: [] },
            highlights: [
              {
                titleId: "label.recordedFacts",
                entries: [
                  entry("target", "label.targetAccount", { kind: "ACTOR", value: row.target }),
                  ...(row.act.kind === "GRANT" || row.act.kind === "REVOKE"
                    ? [
                        entry("role", "field.role.label", {
                          kind: "CODE",
                          vocabulary: "role",
                          token: row.act.role,
                        }),
                        entry("scope", "input.scope.label", {
                          kind: "CODE",
                          vocabulary: "scope",
                          token: row.act.scope.kind,
                        }),
                        ...(row.act.scope.kind === "CASE"
                          ? [
                              entry("scopeReference", "field.caseReference.label", {
                                kind: "REFERENCE",
                                value: row.act.scope.caseReference,
                              }),
                            ]
                          : []),
                      ]
                    : []),
                ],
              },
            ],
            details: [
              {
                titleId: "label.recordDetails",
                entries: [
                  entry("operationId", "field.operationId.label", {
                    kind: "IDENTIFIER",
                    value: row.operationId,
                  }),
                  ...(row.act.kind === "REGISTER" || row.act.kind === "PROVISION_OWNER"
                    ? [
                        entry("issuer", "field.principal.issuer.label", {
                          kind: "IDENTIFIER",
                          value: row.act.principal.issuer,
                        }),
                        entry("subject", "field.principal.subject.label", {
                          kind: "IDENTIFIER",
                          value: row.act.principal.subject,
                        }),
                      ]
                    : []),
                ],
              },
            ],
          })),
        },
      },
    ],
  }),
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
          key: "caseReference",
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
      (row) => row.reminder.reminderId,
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
            key: `${item.position.sequence}:${item.position.effectIndex}`,
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
            details: [
              {
                titleId: "label.recordDetails",
                entries: [
                  entry("operationId", "field.operationId.label", {
                    kind: "IDENTIFIER",
                    value: item.operationId,
                  }),
                ],
              },
            ],
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
    entry("recordedAt", "label.recordedAt", { kind: "INSTANT", value: data.recordedAt }),
    entry("operationId", "field.operationId.label", {
      kind: "IDENTIFIER",
      value: data.operationId,
    }),
    entry("author", "label.recordedBy", { kind: "ACTOR", value: data.recordedBy }),
  ];
  if (data.subject.kind === "CASE") {
    const { snapshot } = data.subject;
    if (snapshot.kind === "DISCLOSED") {
      entries.push(...fields(snapshot.state.fields));
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
  return {
    regions: [
      {
        role: "NOTICE",
        content: { kind: "NOTICE", tone: "INFO", messageId: `act.${data.subject.act}.done` },
      },
      {
        role: "IDENTITY",
        content: { kind: "ENTRIES", entries: entries.filter((item) => item.key === "recordedAt") },
      },
      {
        role: "FACTS",
        content: {
          kind: "ENTRIES",
          entries: entries.filter(
            (item) => item.key !== "operationId" && item.key !== "recordedAt",
          ),
        },
      },
      {
        role: "DETAILS",
        titleId: "label.recordDetails",
        content: { kind: "ENTRIES", entries: entries.filter((item) => item.key === "operationId") },
      },
    ],
  };
};
const timelineRead = (endpoint: string, read: unknown): Screen | null => {
  if (
    endpoint === "case.timeline" &&
    isResponseFor("case.timeline", "DATA", read) &&
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
    representation: "DATA",
    outcome: { kind, data },
  };
  const read = { representation: "DATA", outcome: reply.outcome };
  const timeline = timelineRead(endpoint, read);
  if (timeline !== null) {
    return timeline;
  }
  if (
    isRead(endpoint) &&
    isResponseFor(endpoint, "DATA", read) &&
    read.outcome.kind === "SUCCEEDED"
  ) {
    return readView(endpoint, read.outcome.data);
  }
  if (
    isResponseFor("operation.execute", "DATA", reply) &&
    (reply.outcome.kind === "ACCEPTED" || reply.outcome.kind === "ALREADY_ACCEPTED")
  ) {
    return receiptView(reply.outcome.data);
  }
  if (isResponseFor("operation.get", "DATA", reply) && reply.outcome.kind === "ACCEPTED") {
    return receiptView(reply.outcome.data.receipt);
  }
  return empty;
};

export const presentedOutcome = <K extends "SUCCEEDED" | "ACCEPTED" | "ALREADY_ACCEPTED", const T>(
  endpoint: string,
  kind: K,
  data: T,
) => ({ kind, data, view: fixtureView(endpoint, kind, data) });
