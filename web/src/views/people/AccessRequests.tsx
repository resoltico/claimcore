import type { Affordance, Principal, RegionRow } from "@contracts/client";
import type { OperationAffordance } from "../../operation/affordances";
import { ScreenView } from "../../ui/ScreenView";
import type { PresentedPage } from "../../protocol/types";
import { pageItemIndex } from "../../ui/PageTables";
import { PageRow } from "../../ui/PageRow";
import { useCallback, useEffect, useRef, useState } from "react";

export type AccessRequestControl = Extract<OperationAffordance, { task: "people.admit" }>;
type DismissControl = Extract<Affordance, { task: "people.requests.dismiss" }>;
const requestPrincipal = (action: AccessRequestControl | DismissControl): Principal => ({
  kind: action.seeds["input:principal.kind"]!,
  issuer: action.seeds["input:principal.issuer"]!,
  subject: action.seeds["input:principal.subject"]!,
});
import { requestEndpoint, resultNotice, type ServiceResponse } from "../../api/service";
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
const select = (reply: ServiceResponse<"people.requests">) => {
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
  dismissed,
  message,
  notice,
  empty,
}: {
  loading: boolean;
  dismissed: boolean;
  message: Notice | null;
  notice: Notice | null;
  empty: boolean;
}) => {
  const p = usePresentation();
  return (
    <>
      {loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {dismissed ? <p role="status">{p.text("endpoint.people.requests.dismiss.done")}</p> : null}
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
      {!loading && message === null && empty ? <p>{p.text("ui.requests.empty")}</p> : null}
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
    row: RegionRow;
    view: PresentedPage;
  } | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [dismissed, setDismissed] = useState(false);
  const [pending, setPending] = useState(false);
  const [refused, setRefused] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const refuseDisclosure = useCallback(
    (message?: Notice) => {
      controller.current?.abort();
      controller.current = null;
      setPending(false);
      setSelected(null);
      setRefused(true);
      onDisclosureRefused(message);
    },
    [onDisclosureRefused],
  );
  const page = useEndpointPage<"people.requests", RegionRow, RequestPage>(
    "people.requests",
    input,
    select,
    refuseDisclosure,
  );
  useEffect(() => () => controller.current?.abort(), []);
  const dismiss = async () => {
    if (busy || selected === null || controller.current !== null) {
      return;
    }
    const current = new AbortController();
    controller.current = current;
    setPending(true);
    setNotice(null);
    const result = await requestEndpoint(
      "people.requests.dismiss",
      credential,
      requestPrincipal(selected.control),
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
      setDismissed(true);
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
  const disabled = busy || pending;
  return (
    <section className="access-requests" aria-labelledby="access-requests-title">
      <h2 id="access-requests-title">{p.text("endpoint.people.requests.label")}</h2>
      <RequestFeedback
        loading={page.loading}
        dismissed={dismissed}
        message={page.message}
        notice={notice}
        empty={page.items.length === 0}
      />
      {page.pages.map((observed) => (
        <ScreenView
          key={observed.nextCursor === null ? "END" : `NEXT:${observed.nextCursor}`}
          view={observed.screen}
          actionsDisabled={disabled}
          timeZone={observed.basis.timeZone}
          onAction={(action, row) => {
            if (disabled) {
              return;
            }
            if (action.task === "people.admit" && mayAdmit) {
              onAdmit(action);
            }
            if (action.task === "people.requests.dismiss" && row !== undefined) {
              setDismissed(false);
              setSelected({
                control: action,
                row,
                view: { view: observed.screen, basis: observed.basis },
              });
            }
          }}
        />
      ))}

      <Button
        variant="secondary"
        isDisabled={disabled || page.loading}
        onPress={() => {
          void page.load(null);
        }}
      >
        {p.text("ui.requests.refresh")}
      </Button>
      {page.cursor === null ? null : (
        <Button
          variant="secondary"
          isDisabled={disabled || page.loading}
          onPress={() => {
            void page.load(page.cursor);
          }}
        >
          {p.text("ui.requests.more")}
        </Button>
      )}
      {page.page === null ? null : (
        <p className="review-basis" title={page.page.basis.observedAt}>
          {p.text("ui.review.checked", {
            when: p.recordedTime(page.page.basis.observedAt, page.page.basis.timeZone),
          })}
        </p>
      )}
      <Dialog
        title={p.text("endpoint.people.requests.dismiss.label")}
        description={p.text("endpoint.people.requests.dismiss.meaning")}
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
              {p.text("endpoint.people.requests.dismiss.label")}
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
