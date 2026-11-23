import { Page } from "../../ui/Page";
import { ChoiceControl } from "../../ui/TextField";
import { SearchField } from "../../ui/SearchField";
import { NoticeView } from "../../presentation/Message";
import { apiEndpoints } from "../../api/service";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { PresentedValue } from "../../components/PresentedValue";
import { Toolbar } from "../../ui/Toolbar";
import { ViewTabs } from "../../ui/ViewTabs";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Button } from "../../ui/Button";
import { Select } from "../../ui/Select";
import { Dialog } from "../../ui/Dialog";
import { usePresentation } from "../../presentation/context";
import { queryInput } from "../../protocol/taskCatalog";
import { renderCatalogKey, renderIsolatedValues } from "../../presentation/messages";
import { isTerm } from "../../presentation/terms";
import { useContentRoute } from "../../hooks/SettingsDetourContext";
import { navigate } from "../../routing/navigation";
import {
  parseReminderPageQuery,
  effectiveReminderSelection,
  reminderPagePath,
  remindersPath,
  type ReminderQuery,
} from "../../routing/reminderQuery";
import { useWorkspaceViews, WorkspaceViewsStatus } from "../WorkspaceViews";
import { ReminderQueueWork } from "./ReminderQueueWork";
const deadlineOutcomeChoice = queryInput(apiEndpoints.reminderList, "input:deadlineOutcome");
const kindChoice = queryInput(apiEndpoints.reminderList, "input:reminderKind");
const stateChoice = queryInput(apiEndpoints.reminderList, "input:reminderState");
const assignmentChoice = queryInput(apiEndpoints.reminderList, "input:assignment");
const dueChoice = queryInput(apiEndpoints.reminderList, "input:reminderDue");
const DefaultReminderView = ({
  query,
  view,
}: {
  query: ReminderQuery;
  view: NonNullable<ReminderQuery["view"]>;
}) => {
  useEffect(() => {
    navigate(remindersPath({ ...query, view }), true);
  }, [query, view]);
  const p = usePresentation();
  return <p role="status">{p.text("ui.loading")}</p>;
};
const ReminderSearch = ({
  query,
  change,
  draft,
  onDraft,
}: {
  query: ReminderQuery;
  change: (value: ReminderQuery) => void;
  draft: string;
  onDraft: (value: string) => void;
}) => {
  const p = usePresentation();
  const apply = (text: string) => {
    const { text: _text, ...rest } = query;
    change(text === "" ? rest : { ...rest, text });
  };
  return (
    <SearchField
      id="reminder-search"
      label={p.text("ui.worklistSearch")}
      clearLabel={p.text("ui.clearFilters")}
      value={draft}
      onChange={onDraft}
      onSubmit={apply}
      onClear={() => {
        apply("");
      }}
    />
  );
};
const ReminderCaseTarget = ({
  query,
  change,
}: {
  query: ReminderQuery;
  change: (value: ReminderQuery) => void;
}) => {
  const p = usePresentation();
  const { target } = query;
  if (target === undefined) {
    return null;
  }
  const value = target.kind === "ID" ? target.caseId : target.caseReference;
  const label =
    target.kind === "ID" ? "label.remindersForCaseId" : "label.remindersForCaseReference";
  return (
    <div className="reminder-case-target">
      <p>
        {renderIsolatedValues(
          p,
          label,
          { case: value },
          {
            case: (
              <bdi key="case" dir={target.kind === "ID" ? "ltr" : "auto"}>
                {value}
              </bdi>
            ),
          },
        )}
      </p>
      <Button
        variant="secondary"
        onPress={() => {
          const { target: _target, ...rest } = query;
          change(rest);
          document.getElementById("reminder-queue-title")?.focus();
        }}
      >
        {p.text("ui.clearCaseRestriction")}
      </Button>
    </div>
  );
};
type QueueProps = { onLockChange?: MutationLockChange; disclosureAvailable?: boolean };
type FilterMode = { manual: boolean; switchMode: () => void; canSwitchMode: boolean };

const noLockChange = () => undefined;
const ReminderAdvancedFilters = ({
  query,
  change,
  manual,
  switchMode,
  canSwitchMode,
}: {
  query: ReminderQuery;
  change: (value: ReminderQuery) => void;
} & FilterMode) => {
  const p = usePresentation();
  const [draft, setDraft] = useState<Pick<
    ReminderQuery,
    "kind" | "deadlineOutcome" | "includeInactive"
  > | null>(null);
  const { kind, deadlineOutcome, includeInactive, ...other } = query;
  const count =
    Number(kind !== undefined && kind !== "ANY") +
    Number(deadlineOutcome !== undefined) +
    Number(includeInactive !== undefined);
  return (
    <>
      <button
        type="button"
        aria-haspopup="dialog"
        onClick={() => {
          setDraft({
            ...(kind === undefined ? {} : { kind }),
            ...(deadlineOutcome === undefined ? {} : { deadlineOutcome }),
            ...(includeInactive === undefined ? {} : { includeInactive }),
          });
        }}
      >
        {p.text("ui.filters")}
        {count === 0 ? "" : ` (${p.integer(count)})`}
      </button>
      <Dialog
        title={p.text("ui.filters")}
        closeLabel={p.text("ui.close")}
        className="filter-sheet"
        isOpen={draft !== null}
        isDismissable
        onOpenChange={(open) => {
          if (!open) {
            setDraft(null);
          }
        }}
      >
        {draft === null ? null : (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              change({ ...other, ...draft });
              setDraft(null);
            }}
          >
            <Button
              variant="secondary"
              aria-pressed={manual}
              isDisabled={!canSwitchMode}
              onPress={() => {
                setDraft(null);
                switchMode();
              }}
            >
              {p.text("ui.manualFilters")}
            </Button>
            <div className="filter-field-grid">
              <Select
                label={renderCatalogKey(p, kindChoice.labelId)}
                value={draft.kind ?? "ANY"}
                options={kindChoice.choices
                  .filter((value) => isTerm("reminderKindSelection", value))
                  .map((value) => ({ value, label: p.term("reminderKindSelection", value) }))}
                onChange={(value) => {
                  if (isTerm("reminderKindSelection", value)) {
                    setDraft({ ...draft, kind: value });
                  }
                }}
              />
              <Select
                label={renderCatalogKey(p, deadlineOutcomeChoice.labelId)}
                value={draft.deadlineOutcome ?? ""}
                options={[
                  { value: "", label: p.text("ui.anyFilter") },
                  ...deadlineOutcomeChoice.choices
                    .filter((value) => isTerm("deadlineOutcome", value))
                    .map((value) => ({ value, label: p.term("deadlineOutcome", value) })),
                ]}
                onChange={(value) => {
                  const { deadlineOutcome: _outcome, ...rest } = draft;
                  if (value === "") {
                    setDraft(rest);
                  } else if (isTerm("deadlineOutcome", value)) {
                    setDraft({ ...rest, deadlineOutcome: value });
                  }
                }}
              />
            </div>
            <label className="field-toggle">
              <ChoiceControl
                type="checkbox"
                checked={effectiveReminderSelection({ ...other, ...draft }).includeInactive}
                onChange={(event) => {
                  setDraft({ ...draft, includeInactive: event.target.checked });
                }}
              />
              {p.text("input.includeInactive.label")}
            </label>
            <div className="worklist-actions">
              <button
                type="button"
                onClick={() => {
                  setDraft({});
                }}
              >
                {p.text("ui.clearFilters")}
              </button>
              <button
                type="button"
                onClick={() => {
                  setDraft(null);
                }}
              >
                {p.text("ui.cancelChanges")}
              </button>
              <button type="submit">{p.text("ui.applyFilters")}</button>
            </div>
          </form>
        )}
      </Dialog>
    </>
  );
};
const ReminderFilters = ({
  query,
  change,
  draft,
  onDraft,
  exportAction,
  ...mode
}: {
  query: ReminderQuery;
  change: (value: ReminderQuery) => void;
  draft: string;
  onDraft: (value: string) => void;
  exportAction: ReactNode;
} & FilterMode) => {
  const p = usePresentation();
  const effective = effectiveReminderSelection(query);
  return (
    <Toolbar
      className="worklist-toolbar"
      filter={<ReminderSearch query={query} change={change} draft={draft} onDraft={onDraft} />}
      activeFilters={
        Object.entries(query).filter(
          ([key, value]) =>
            key !== "view" &&
            key !== "text" &&
            key !== "target" &&
            value !== "ANY" &&
            value !== false,
        ).length
      }
    >
      <Select
        inlineLabel
        label={renderCatalogKey(p, stateChoice.labelId)}
        value={effective.state}
        options={stateChoice.choices
          .filter((value) => isTerm("reminderStateSelection", value))
          .map((value) => ({ value, label: p.term("reminderStateSelection", value) }))}
        onChange={(value) => {
          if (isTerm("reminderStateSelection", value)) {
            change({ ...query, state: value });
          }
        }}
      />
      <Select
        inlineLabel
        label={renderCatalogKey(p, dueChoice.labelId)}
        description={p.text("label.reminderQueueHint")}
        value={query.due ?? ""}
        options={[
          { value: "", label: p.text("ui.anyFilter") },
          ...dueChoice.choices
            .filter((value) => isTerm("reminderDue", value))
            .map((value) => ({
              value,
              label: p.term("reminderDue", value),
            })),
        ]}
        onChange={(value) => {
          const { due: _due, ...rest } = query;
          if (value === "") {
            change(rest);
          } else if (isTerm("reminderDue", value)) {
            change({ ...rest, due: value });
          }
        }}
      />
      <Select
        inlineLabel
        label={renderCatalogKey(p, assignmentChoice.labelId)}
        value={query.assignment ?? ""}
        options={[
          { value: "", label: p.text("ui.anyFilter") },
          ...assignmentChoice.choices
            .filter((value): value is "MINE" | "UNASSIGNED" => isTerm("assignment", value))
            .map((value) => ({ value, label: p.term("assignment", value) })),
        ]}
        onChange={(value) => {
          const { assignment: _assignment, ...rest } = query;
          if (value === "") {
            change(rest);
          } else if (isTerm("assignment", value)) {
            change({ ...rest, assignment: value });
          }
        }}
      />
      <ReminderAdvancedFilters query={query} change={change} {...mode} />
      {exportAction}
    </Toolbar>
  );
};
const ReadyReminderQueue = ({
  query,
  manual,
  onLockChange = noLockChange,
  disclosureAvailable = true,
}: { query: ReminderQuery; manual: boolean } & QueueProps) => {
  const p = usePresentation();
  const views = useWorkspaceViews();
  const source = query.text ?? "";
  const [draft, setDraft] = useState({ source, value: source });
  if (draft.source !== source) {
    setDraft({ source, value: source });
  }
  const change = (value: ReminderQuery) => {
    navigate(reminderPagePath(value, manual), true);
  };
  const switchMode = () => {
    if (manual) {
      const view = views.reminders.data?.items[0]?.view;
      if (view !== undefined) {
        navigate(reminderPagePath({ ...query, view }, false), true);
      }
    } else {
      navigate(reminderPagePath(query, true), true);
    }
  };
  const work = (
    <ReminderQueueWork
      filter={query}
      onLockChange={onLockChange}
      disclosureAvailable={disclosureAvailable}
      renderToolbar={(exportAction) => (
        <ReminderFilters
          query={query}
          change={change}
          draft={draft.value}
          onDraft={(value) => {
            setDraft({ source, value });
          }}
          manual={manual}
          switchMode={switchMode}
          canSwitchMode={!manual || views.reminders.data?.items[0] !== undefined}
          exportAction={exportAction}
        />
      )}
    />
  );
  return (
    <Page className="reminder-queue" id="reminder-queue-title" title={p.text("label.reminders")}>
      <WorkspaceViewsStatus area="reminders" />
      <ReminderCaseTarget query={query} change={change} />
      {manual ? (
        work
      ) : (
        <ViewTabs
          items={
            views.reminders.data?.items.map(({ view, label, count, prominence }) => ({
              id: view,
              prominence,
              ...(label.kind === "MESSAGE" && count.kind === "COUNT"
                ? {
                    menuLabel: `${renderCatalogKey(p, label.messageId)} · ${p.integer(count.value)}`,
                  }
                : {}),
              label: (
                <PresentedValue value={label} timeZone={views.reminders.data!.basis.timeZone} />
              ),
              count: (
                <PresentedValue value={count} timeZone={views.reminders.data!.basis.timeZone} />
              ),
            })) ?? []
          }
          selected={query.view ?? ""}
          label={p.text("input.reminderView.label")}
          loading={views.reminders.loading}
          onSelect={(view) => {
            if (isTerm("reminderView", view)) {
              change({ ...query, view });
            }
          }}
          controls={null}
        >
          {work}
        </ViewTabs>
      )}
    </Page>
  );
};
export const ReminderQueue = (props: QueueProps) => {
  const route = useContentRoute();
  const p = usePresentation();
  const parsed = useMemo(
    () =>
      route.screen === "reminders" && route.invalidSelection === true
        ? null
        : parseReminderPageQuery(route.screen === "reminders" ? route.query : ""),
    [route],
  );
  const views = useWorkspaceViews().reminders;
  if (parsed === null) {
    return (
      <Page id="reminder-queue-title" title={p.text("label.reminders")}>
        <p role="alert">
          <NoticeView value={{ kind: "local", reason: "invalidInput" }} />
        </p>
        <button
          type="button"
          onClick={() => {
            navigate(reminderPagePath({}, true), true);
          }}
        >
          {p.text("ui.clearFilters")}
        </button>
      </Page>
    );
  }
  const { query, manual } = parsed;
  if (query.view === undefined && !manual) {
    const first = views.data?.items[0];
    if (first !== undefined) {
      return <DefaultReminderView query={query} view={first.view} />;
    }
    return <WorkspaceViewsStatus area="reminders" />;
  }
  return <ReadyReminderQueue query={query} manual={manual} {...props} />;
};
