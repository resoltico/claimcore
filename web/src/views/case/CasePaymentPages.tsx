import { useCallback, useLayoutEffect } from "react";
import type { HumanResponseByEndpoint } from "@contracts/client";
import type { CurrentCase } from "../../protocol/types";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { Button } from "../../ui/Button";
import { PaymentRows, PaymentsShown } from "./PaymentRows";

const select = (reply: HumanResponseByEndpoint["case.payments"]) => {
  if (reply.outcome.kind !== "SUCCEEDED" || reply.outcome.document.basis.kind !== "SNAPSHOT") {
    return null;
  }
  const doc = reply.outcome.document;
  return {
    items: doc.screen.regions.flatMap((region) =>
      region.content.kind === "TABLE" ? region.content.rows : [],
    ),
    nextCursor: doc.nextCursor,
    caseId: doc.subject.caseId,
    revision: doc.subject.revision,
    screen: doc.screen,
    basis: reply.outcome.document.basis.observation,
    disclosure: doc.disclosure,
  };
};
type Page = NonNullable<ReturnType<typeof select>>;
type Handle = Page["items"][number];
const continuationKey = (page: Page) => `${page.caseId}:${page.revision}`;
export const CasePaymentPages = ({
  current,
  total,
  onRemove,
  onChanged,
}: {
  current: CurrentCase;
  total: number;
  onRemove: (paymentId: string) => void;
  onChanged: () => void;
}) => {
  const p = usePresentation();
  const input = useCallback(
    (cursor: string | null) => ({
      target: { kind: "ID" as const, caseId: current.subject.caseId },
      ...(cursor === null ? {} : { cursor }),
    }),
    [current.subject.caseId],
  );
  const result = useEndpointPage<"case.payments", Handle, Page>(
    "case.payments",
    input,
    select,
    onChanged,
    undefined,
    continuationKey,
  );
  const { page } = result;
  const matches =
    page === null ||
    (page.caseId === current.subject.caseId &&
      page.revision === current.subject.revision &&
      page.disclosure === current.disclosure);
  useLayoutEffect(() => {
    if (!matches) {
      onChanged();
    }
  }, [matches, onChanged]);
  if (!matches) {
    return <p role="status">{p.text("ui.loadingCurrentCase")}</p>;
  }
  return (
    <div className="case-payment-pages" aria-busy={result.loading}>
      {result.views.map((observed) => {
        const content = observed.view.regions.find((region) => region.role === "PAYMENTS")?.content;
        return content?.kind === "TABLE" ? (
          <PaymentRows
            key={content.rows[0]?.key ?? "empty"}
            content={content}
            zone={observed.basis.timeZone}
            onRemove={onRemove}
          />
        ) : null;
      })}
      {result.message === null ? null : (
        <p role="alert">
          <NoticeView value={result.message} />
        </p>
      )}
      {result.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      <PaymentsShown shown={result.items.length} total={total} />
      {result.cursor === null && result.message === null ? null : (
        <Button
          variant="secondary"
          isDisabled={result.loading}
          onPress={() => {
            void result.load(result.cursor);
          }}
        >
          {p.text(result.message === null ? "ui.morePayments" : "ui.tryAgain")}
        </Button>
      )}
    </div>
  );
};
