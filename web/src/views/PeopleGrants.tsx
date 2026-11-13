import { ScreenView } from "../ui/ScreenView";
import type { Screen, Region, PresentedValue as Value } from "@contracts/client";
import { useCallback, useEffect, type ReactNode } from "react";
import type { PersonDocument } from "../protocol/types";
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

export type RevocationControl = PersonDocument["context"]["revocations"][number];
const grantPage = (response: ServiceResponse<"people.grants">, actorId: string) => {
  if (response.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const { document } = response.outcome;
  if (document.basis.kind !== "SNAPSHOT" || document.context.actorId !== actorId) {
    return null;
  }
  return {
    items: document.context.revocations,
    nextCursor: document.context.nextCursor,
    screen: document.screen,
    disclosure: document.disclosure,
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
    (response: ServiceResponse<"people.grants">) => grantPage(response, actorId),
    [actorId],
  );
  return useEndpointPage<"people.grants", RevocationControl, GrantPage>(
    "people.grants",
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
      {page.pages.map((observed) => (
        <ScreenView
          key={observed.nextCursor === null ? "END" : `NEXT:${observed.nextCursor}`}
          view={observed.screen}
          timeZone={observed.basis.timeZone}
          roles={["ACCESS"]}
          renderActions={(key) => {
            const grant = observed.items.find((item) => item.key === key);
            return grant === undefined || onRevoke === undefined ? null : (
              <button
                type="button"
                onClick={() => {
                  onRevoke(grant);
                }}
              >
                {p.peopleLabel("people.revoke")}
              </button>
            );
          }}
        />
      ))}
      {page.views
        .at(-1)
        ?.view.regions.filter((region) => region.role === "DETAILS")
        .map((region) => (
          <RegionView
            key={region.role}
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
          href={`/people-history?actorId=${encodeURIComponent(page.page.document.context.actorId)}`}
          onClick={(event) => {
            followLink(event, () => {
              navigate(
                `/people-history?actorId=${encodeURIComponent(page.page!.document.context.actorId)}`,
              );
            });
          }}
        >
          {p.text("endpoint.people.history.label")}
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
const accountPresentation = (
  screen: Screen | undefined,
): { caption: Value | undefined; body: Region | undefined } => {
  const account = screen?.regions.find((region) => region.role === "ACCOUNT");
  if (account?.content.kind !== "ENTRIES") {
    return { caption: undefined, body: undefined };
  }
  const { entries } = account.content;
  return {
    caption: entries.find((entry) => entry.key === "actor")?.value,
    body: {
      ...account,
      content: { kind: "ENTRIES", entries: entries.filter((entry) => entry.key !== "actor") },
    },
  };
};
const PersonAccountFacts = ({
  body,
  timeZone,
  countLink,
}: {
  body: Region | undefined;
  timeZone: string;
  countLink: ReturnType<typeof responsibilityLink>;
}) =>
  body === undefined ? null : (
    <RegionView
      region={body}
      timeZone={timeZone}
      {...(countLink === undefined ? {} : { countLink })}
    />
  );
export const PeopleGrants = ({
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
  const { caption, body } = accountPresentation(page.views.at(-1)?.view);
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
      identity={<PersonAccountFacts body={body} timeZone={zone} countLink={countLink} />}
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
