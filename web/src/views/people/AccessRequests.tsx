import { PresentedValue } from "../../components/PresentedValue";
import { renderCatalogKey } from "../../presentation/messages";
import { assembleCommand } from "../../api/commandControl";
import {
  taskDefinitions,
  type Affordance,
  type RegionRow,
  type TaskDefinition,
  type HumanDocument,
} from "@contracts/client";
import { operationTask } from "../../protocol/taskCatalog";
import {
  apiEndpoints,
  requestEndpoint,
  resultNotice,
  type ServiceResponse,
} from "../../api/service";
import { isOperationAffordance, type OperationAffordance } from "../../operation/affordances";

import { ScreenView } from "../../ui/ScreenView";
import type { PresentedPage } from "../../protocol/types";
import { pageItemIndex } from "../../ui/ResultPages";
import { PageRow } from "../../ui/PageRow";
import { useCallback, useEffect, useRef, useState } from "react";

export type AccessRequestControl = OperationAffordance;
type DismissControl = Affordance;

import { isDisclosureRefused } from "../../api/outcomes";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { useCredential, useSessionContext } from "../../session/SessionContext";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import type { Notice } from "../../protocol/notices";
import { Button } from "../../ui/Button";
import { Dialog } from "../../ui/Dialog";
import {} from "./access-requests.css";

type Props = {
  available: boolean;
  busy: boolean;
  mayAdmit?: boolean;
  onAdmit: (request: AccessRequestControl) => void;
  onDisclosureRefused: (notice?: Notice) => void;
};
const input = (cursor: string | null) => ({ limit: 50, ...(cursor === null ? {} : { cursor }) });
const select = (reply: ServiceResponse<typeof apiEndpoints.peopleRequests>) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = reply.outcome.document;
  if (doc.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: doc.screen.regions.flatMap((region) =>
      region.content.kind === "TABLE" ? region.content.rows : [],
    ),
    nextCursor: doc.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
    disclosure: doc.disclosure,
  };
};
type RequestPage = NonNullable<ReturnType<typeof select>>;
const RequestFeedback = ({
  loading,
  completion,
  message,
  notice,
  empty,
}: {
  loading: boolean;
  completion: HumanDocument | null;
  message: Notice | null;
  notice: Notice | null;
  empty: boolean;
}) => {
  const p = usePresentation();
  return (
    <>
      {loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {completion === null ? null : (
        <ScreenView
          view={completion.screen}
          timeZone={
            completion.basis.kind === "SNAPSHOT" ? completion.basis.observation.timeZone : "UTC"
          }
        />
      )}
      {message === null ? null : (
        <p role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {notice === null ? null : (
        <p role="alert">
          <NoticeView value={notice} />
        </p>
      )}
      {!loading && message === null && notice === null && empty ? (
        <p>{p.text("label.noAccessRequests")}</p>
      ) : null}
    </>
  );
};
const AvailableRequests = ({
  busy,
  mayAdmit = true,
  onAdmit,
  onDisclosureRefused,
}: Omit<Props, "available">) => {
  const p = usePresentation();
  const credential = useCredential();
  const [selected, setSelected] = useState<{
    control: DismissControl;
    definition: Extract<TaskDefinition, { kind: "COMMAND" }>;
    row: RegionRow;
    view: PresentedPage;
  } | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [completion, setCompletion] = useState<HumanDocument | null>(null);
  const [pending, setPending] = useState(false);
  const [refused, setRefused] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const refuseDisclosure = useCallback(
    (message?: Notice) => {
      controller.current?.abort();
      controller.current = null;
      setPending(false);
      setSelected(null);
      setCompletion(null);
      setRefused(true);
      onDisclosureRefused(message);
    },
    [onDisclosureRefused],
  );
  const page = useEndpointPage<typeof apiEndpoints.peopleRequests, RegionRow, RequestPage>(
    apiEndpoints.peopleRequests,
    input,
    select,
    refuseDisclosure,
  );
  useEffect(() => () => controller.current?.abort(), []);
  const dismiss = async () => {
    if (busy || selected === null || controller.current !== null) {
      return;
    }
    const command = assembleCommand(selected.control);
    if (command === null) {
      setNotice({ kind: "local", reason: "incomplete" });
      return;
    }
    const current = new AbortController();
    controller.current = current;
    setPending(true);
    setNotice(null);
    const result = await requestEndpoint(
      command.endpoint,
      credential,
      command.input,
      current.signal,
    );
    if (current.signal.aborted) {
      return;
    }
    controller.current = null;
    setPending(false);
    if (isDisclosureRefused(result)) {
      refuseDisclosure(resultNotice(result));
    } else if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
      setSelected(null);
      setCompletion(result.value.outcome.document);
      await page.load(null);
    } else {
      setNotice(resultNotice(result));
      // A delivery failure may have removed the request. Refresh is explicit; never repeat the write automatically.
      setSelected(null);
    }
  };
  if (refused) {
    return null;
  }
  const commandText = (part: "labelId" | "meaningId") =>
    renderCatalogKey(p, selected?.definition[part] ?? "label.accessRequest");
  const disabled = busy || pending;
  return (
    <section className="access-requests" aria-labelledby="access-requests-title">
      <h2 id="access-requests-title">
        {p.text("endpoint.people.requests.label")} · {p.integer(page.items.length)}
      </h2>
      <RequestFeedback
        loading={page.loading}
        completion={completion}
        message={page.message}
        notice={notice}
        empty={page.page !== null && page.cursor === null && page.items.length === 0}
      />
      {page.pages.flatMap((observed) =>
        observed.screen.regions.flatMap(({ content }) =>
          content.kind === "TABLE"
            ? content.rows.map((row) => (
                <article className="access-request-card" key={row.key}>
                  <dl>
                    {content.columns.flatMap((column, index) => {
                      const field = row.values[index];
                      return field === undefined || column.prominence === "DETAIL" ? (
                        []
                      ) : (
                        <div key={column.key}>
                          <dt>{renderCatalogKey(p, column.labelId)}</dt>
                          <dd>
                            <PresentedValue value={field} timeZone={observed.basis.timeZone} />
                          </dd>
                        </div>
                      );
                    })}
                  </dl>
                  <div className="actions">
                    {(row.actions ?? []).map((action) => (
                      <Button
                        key={action.task}
                        variant="secondary"
                        compact
                        isDisabled={disabled || action.blocked !== null}
                        onPress={() => {
                          if (
                            isOperationAffordance(action) &&
                            operationTask(action.task).creates &&
                            mayAdmit
                          ) {
                            onAdmit(action);
                          }
                          const definition = taskDefinitions.find(
                            (task): task is Extract<TaskDefinition, { kind: "COMMAND" }> =>
                              task.id === action.task && task.kind === "COMMAND",
                          );
                          if (definition !== undefined) {
                            setCompletion(null);
                            setSelected({
                              control: action,
                              definition,
                              row,
                              view: { view: observed.screen, basis: observed.basis },
                            });
                          }
                        }}
                      >
                        {renderCatalogKey(p, action.labelId)}
                      </Button>
                    ))}
                  </div>
                </article>
              ))
            : [],
        ),
      )}
      {page.cursor === null ? null : (
        <Button
          variant="quiet"
          compact
          isDisabled={disabled || page.loading}
          onPress={() => {
            void page.load(page.cursor);
          }}
        >
          {p.text("ui.loadMore")}
        </Button>
      )}
      {page.refreshBusy ? (
        <p role="status">
          {p.text("ui.refreshFailed")}{" "}
          <Button variant="quiet" compact onPress={page.refresh}>
            {p.text("ui.retryRefresh")}
          </Button>
        </p>
      ) : null}
      <Dialog
        title={commandText("labelId")}
        description={commandText("meaningId")}
        closeLabel={p.text("ui.close")}
        isOpen={selected !== null}
        isDismissable={!pending}
        onOpenChange={(open) => {
          if (!open && !pending) {
            setSelected(null);
          }
        }}
        footer={
          <>
            <Button
              variant="secondary"
              isDisabled={pending}
              onPress={() => {
                setSelected(null);
              }}
            >
              {p.text("ui.close")}
            </Button>
            <Button
              variant="danger"
              isDisabled={disabled}
              onPress={() => {
                void dismiss();
              }}
            >
              {commandText("labelId")}
            </Button>
          </>
        }
      >
        {selected === null ? null : (
          <PageRow
            views={[selected.view]}
            index={pageItemIndex([selected.view], selected.row.key)}
            titleId="label.accessRequest"
          />
        )}
      </Dialog>
    </section>
  );
};
/** Availability and the session epoch bound all disclosed request data and in-flight mutations. */
export const AccessRequests = ({ available, ...props }: Props) => {
  const { state } = useSessionContext();
  return available && state.kind === "authenticated" ? (
    <AvailableRequests key={`${state.epoch}:${state.token}`} {...props} />
  ) : null;
};
