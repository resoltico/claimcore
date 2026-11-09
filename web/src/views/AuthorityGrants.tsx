import { PageTables } from "../ui/PageTables";
import { useCallback, useEffect, type ReactNode } from "react";
import type { HumanReadDocument_authorityGrants } from "@contracts/client";
import type { Notice } from "../protocol/notices";
import type { ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { PresentedValue } from "../components/PresentedValue";
import { IconButton } from "../ui/IconButton";
import { Dialog } from "../ui/Dialog";
import { Button } from "../ui/Button";
import { useSettingsDetour } from "../hooks/SettingsDetourContext";
import { followLink } from "../routing/followLink";
import { navigate } from "../routing/navigation";
import { RegionView } from "../ui/regions/RegionView";
import { worklistPath } from "../routing/worklistQuery";

export type PersonDocument = HumanReadDocument_authorityGrants;
export type RevocationControl = PersonDocument["context"]["revocations"][number];
const grantPage = (response: ServiceResponse<"authority.grants">, actorId: string) => {
  if (response.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const document = response.outcome.document;
  if (document.basis.kind !== "SNAPSHOT" || document.context.actorId !== actorId) {
    return null;
  }
  return {
    items: document.context.revocations,
    nextCursor: document.context.nextCursor,
    screen: document.screen,
    basis: document.basis.observation,
    document,
  };
};
type GrantPage = NonNullable<ReturnType<typeof grantPage>>;
type Props = {
  actorId: string;
  onBack: () => void;
  onDisclosureRefused: (notice?: Notice) => void;
  onActor?: ((document: PersonDocument) => void) | undefined;
  open?: boolean;
  actions?: ReactNode;
  onRevoke?: ((grant: RevocationControl) => void) | undefined;
};
const continuationKey = (page: GrantPage) =>
  JSON.stringify([page.document.disclosure, page.document.context.tasks]);
const useActorGrants = (actorId: string, onDisclosureRefused: Props["onDisclosureRefused"]) => {
  const input = useCallback(
    (cursor: string | null) => ({ actorId, ...(cursor === null ? {} : { cursor }), limit: 50 }),
    [actorId],
  );
  const select = useCallback(
    (response: ServiceResponse<"authority.grants">) => grantPage(response, actorId),
    [actorId],
  );
  return useEndpointPage<"authority.grants", RevocationControl, GrantPage>(
    "authority.grants",
    input,
    select,
    onDisclosureRefused,
    undefined,
    continuationKey,
  );
};
const PersonContents = ({
  page,
  actions,
  onRevoke,
}: {
  page: ReturnType<typeof useActorGrants>;
  actions: ReactNode;
  onRevoke: Props["onRevoke"];
}) => {
  const p = usePresentation();
  return (
    <>
      {actions}
      <PageTables
        views={page.views}
        roles={["ACCESS"]}
        renderActions={(key) => {
          const grant = page.items.find((item) => item.key === key);
          return grant === undefined || onRevoke === undefined ? null : (
            <button type="button" onClick={() => onRevoke(grant)}>
              {p.authorityLabel("REVOKE")}
            </button>
          );
        }}
      />
      {page.views
        .at(-1)
        ?.view.regions.filter((region) => region.role === "DETAILS")
        .map((region, index) => (
          <RegionView
            key={`${region.role}:${index}`}
            region={region}
            timeZone={page.views.at(-1)!.basis.timeZone}
          />
        ))}
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      {page.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {page.page === null ? null : (
        <a
          className="actor-detail-link"
          href={`/authority-history?actorId=${encodeURIComponent(page.page.document.context.actorId)}`}
          onClick={(event) => {
            followLink(event, () =>
              navigate(
                `/authority-history?actorId=${encodeURIComponent(page.page!.document.context.actorId)}`,
              ),
            );
          }}
        >
          {p.text("ui.shell.authorityHistory")}
        </a>
      )}
      {page.page === null ? null : (
        <p role="status">{p.text(page.cursor === null ? "ui.grantsEnd" : "ui.grantsPartial")}</p>
      )}
      <button type="button" disabled={page.loading} onClick={() => void page.load(null)}>
        {p.text("ui.reloadGrants")}
      </button>
      {page.cursor === null ? null : (
        <button type="button" disabled={page.loading} onClick={() => void page.load(page.cursor)}>
          {p.text("ui.moreGrants")}
        </button>
      )}
    </>
  );
};
const responsibilityLink = (offered: PersonDocument["context"]["responsibleCases"] | undefined) => {
  if (offered === undefined || offered === null) {
    return undefined;
  }
  const href = worklistPath({
    view: offered.view,
    responsibleActor: offered.responsibleActor,
    lifecycle: [offered.lifecycle],
    status: [offered.status],
  });
  return {
    column: "responsibleCases",
    href,
    follow: () => {
      navigate(href);
    },
  };
};
export const AuthorityGrants = ({
  actorId,
  onBack,
  onDisclosureRefused,
  onActor,
  open = true,
  actions,
  onRevoke,
}: Props) => {
  const p = usePresentation();
  const settings = useSettingsDetour();
  const page = useActorGrants(actorId, onDisclosureRefused);
  const document = page.page?.document ?? null;
  useEffect(() => {
    if (document !== null) {
      onActor?.(document);
    }
  }, [document, onActor]);
  const account = page.views.at(-1)?.view.regions.find((region) => region.role === "ACCOUNT");
  const entries = account?.content.kind === "ENTRIES" ? account.content.entries : [];
  const caption = entries.find((entry) => entry.key === "actor")?.value;
  const body =
    account?.content.kind === "ENTRIES"
      ? {
          ...account,
          content: {
            kind: "ENTRIES" as const,
            entries: entries.filter((entry) => entry.key !== "actor"),
          },
        }
      : undefined;
  const zone = page.page?.basis.timeZone ?? "UTC";
  const countLink = responsibilityLink(document?.context.responsibleCases);
  return (
    <Dialog
      title={
        caption === undefined ? (
          p.text("ui.people.person")
        ) : (
          <PresentedValue value={caption} timeZone={zone} avatar />
        )
      }
      identity={
        body === undefined ? null : (
          <RegionView
            region={body}
            timeZone={zone}
            {...(countLink === undefined ? {} : { countLink })}
          />
        )
      }
      closeLabel={p.text("ui.close")}
      placement="end"
      headerEnd={
        <div className="actions">
          {settings === null ? null : (
            <IconButton icon="settings" label={p.text("ui.settings")} onPress={settings.visit} />
          )}
          <IconButton icon="close" label={p.text("ui.close")} onPress={onBack} />
        </div>
      }
      isOpen={open && settings?.paused !== true}
      isDismissable
      onOpenChange={(next) => {
        if (!next) {
          onBack();
        }
      }}
      footer={
        <Button variant="secondary" onPress={onBack}>
          {p.text("ui.close")}
        </Button>
      }
    >
      <PersonContents page={page} actions={actions} onRevoke={onRevoke} />
    </Dialog>
  );
};
