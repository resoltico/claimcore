import { NoticeView } from "../../presentation/Message";
import { apiEndpoints } from "../../api/service";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { PresentedValue } from "../../components/PresentedValue";
import { ViewTabs } from "../../ui/ViewTabs";
import { useEffect, useMemo, useState } from "react";
import { Button } from "../../ui/Button";
import { Select } from "../../ui/Select";
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
  return (
    <form
      className="reminder-search"
      role="search"
      aria-label={p.text("label.reminders")}
      onSubmit={(event) => {
        event.preventDefault();
        const { text: _text, ...rest } = query;
        change(draft === "" ? rest : { ...rest, text: draft });
      }}
    >
      <label>
        {p.text("ui.search")}
        <input
          type="search"
          value={draft}
          onChange={(event) => {
            onDraft(event.target.value);
          }}
        />
      </label>
      <Button type="submit">{p.text("ui.search")}</Button>
    </form>
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
const noLockChange = () => undefined;
const ReminderFilters = ({
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
  const effective = effectiveReminderSelection(query);
  return (
    <div className="work-queue-filters">
      <Select
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
        label={renderCatalogKey(p, kindChoice.labelId)}
        value={query.kind ?? "ANY"}
        options={kindChoice.choices
          .filter((value) => isTerm("reminderKindSelection", value))
          .map((value) => ({ value, label: p.term("reminderKindSelection", value) }))}
        onChange={(value) => {
          if (isTerm("reminderKindSelection", value)) {
            change({ ...query, kind: value });
          }
        }}
      />
      <Select
        label={renderCatalogKey(p, deadlineOutcomeChoice.labelId)}
        value={query.deadlineOutcome ?? ""}
        options={[
          { value: "", label: p.text("ui.anyFilter") },
          ...deadlineOutcomeChoice.choices
            .filter((value) => isTerm("deadlineOutcome", value))
            .map((value) => ({ value, label: p.term("deadlineOutcome", value) })),
        ]}
        onChange={(value) => {
          const { deadlineOutcome: _outcome, ...rest } = query;
          if (value === "") {
            change(rest);
          } else if (isTerm("deadlineOutcome", value)) {
            change({ ...rest, deadlineOutcome: value });
          }
        }}
      />
      <Select
        label={renderCatalogKey(p, dueChoice.labelId)}
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
      <label className="field-toggle">
        <input
          type="checkbox"
          checked={effective.includeInactive}
          onChange={(event) => {
            change({ ...query, includeInactive: event.target.checked });
          }}
        />
        {p.text("input.includeInactive.label")}
      </label>
      <ReminderSearch query={query} change={change} draft={draft} onDraft={onDraft} />
    </div>
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
  const filters = (
    <ReminderFilters
      query={query}
      change={change}
      draft={draft.value}
      onDraft={(value) => {
        setDraft({ source, value });
      }}
    />
  );
  const work = (
    <ReminderQueueWork
      filter={query}
      onLockChange={onLockChange}
      disclosureAvailable={disclosureAvailable}
    />
  );
  return (
    <section className="reminder-queue" aria-labelledby="reminder-queue-title">
      <h1 id="reminder-queue-title" tabIndex={-1}>
        {p.text("label.reminders")}
      </h1>
      <WorkspaceViewsStatus area="reminders" />
      <ReminderCaseTarget query={query} change={change} />
      <Button
        variant="secondary"
        aria-pressed={manual}
        isDisabled={manual && views.reminders.data?.items[0] === undefined}
        onPress={switchMode}
      >
        {p.text("ui.manualFilters")}
      </Button>
      {manual ? (
        <>
          {filters}
          {work}
        </>
      ) : (
        <ViewTabs
          items={
            views.reminders.data?.items.map(({ view, label, count }) => ({
              id: view,
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
          controls={filters}
        >
          {work}
        </ViewTabs>
      )}
    </section>
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
      <section aria-labelledby="reminder-queue-title">
        <h1 id="reminder-queue-title">{p.text("label.reminders")}</h1>
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
      </section>
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
