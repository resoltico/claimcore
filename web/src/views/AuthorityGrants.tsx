import { PageTables, pageItemIndex } from "../ui/PageTables";
import { useCallback, useEffect, type ReactNode } from "react";
import type { Notice } from "../protocol/notices";
import { type ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { CharacterWarning } from "../components/BusinessValue";
import { IconButton } from "../ui/IconButton";
import { Dialog } from "../ui/Dialog";
import { Button } from "../ui/Button";
import { actorName } from "../presentation/actorName";
import { useSettingsDetour } from "../hooks/SettingsDetourContext";
import { followLink } from "../routing/followLink";
import { navigate } from "../routing/navigation";
import { ActorAvatar } from "../components/ActorAvatar";
import { RegionView } from "../ui/regions/RegionView";
import { worklistPath } from "../routing/worklistQuery";
import type { Region } from "@contracts/client";

const grantPage = (response: ServiceResponse<"authority.grants">) =>
  response.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
type GrantPage = NonNullable<ReturnType<typeof grantPage>>;
type Props = {
  actorId: string;

  onBack: () => void;
  onDisclosureRefused: (notice?: Notice) => void;
  onActor?: ((actor: GrantPage["actor"]) => void) | undefined;
  open?: boolean;
  actions?: ReactNode;
  onRevoke?: ((grant: GrantPage["items"][number]) => void) | undefined;
};
const useActorDisclosure = (actor: GrantPage["actor"] | null, onActor: Props["onActor"]) => {
  useEffect(() => {
    if (actor !== null) {
      onActor?.(actor);
    }
  }, [actor, onActor]);
};
const useActorGrants = (actorId: string, onDisclosureRefused: Props["onDisclosureRefused"]) => {
  const input = useCallback(
    (cursor: string | null) => ({ actorId, ...(cursor === null ? {} : { cursor }), limit: 50 }),
    [actorId],
  );
  return useEndpointPage<"authority.grants", GrantPage["items"][number], GrantPage>(
    "authority.grants",
    input,
    grantPage,
    onDisclosureRefused,
  );
};
const PersonContents = ({
  page,
  actions,
  onRevoke,
}: {
  onRevoke: Props["onRevoke"];
  page: ReturnType<typeof useActorGrants>;
  actions: ReactNode;
}) => {
  const p = usePresentation();
  return (
    <>
      {actions}
      <PageTables
        views={page.views}
        roles={["ACCESS"]}
        renderActions={(key) => {
          const index = pageItemIndex(page.views, key);
          const grant = page.items[index];
          return grant === undefined || onRevoke === undefined ? null : (
            <button
              type="button"
              onClick={() => {
                onRevoke(grant);
              }}
            >
              {p.authorityLabel("REVOKE")}
            </button>
          );
        }}
      />
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
          href={`/authority-history?actorId=${encodeURIComponent(page.page.actor.actor.actorId)}`}
          onClick={(event) => {
            followLink(event, () => {
              navigate(
                `/authority-history?actorId=${encodeURIComponent(page.page!.actor.actor.actorId)}`,
              );
            });
          }}
        >
          {p.text("ui.shell.authorityHistory")}
        </a>
      )}
      {page.page === null ? null : (
        <p role="status">{p.text(page.cursor === null ? "ui.grantsEnd" : "ui.grantsPartial")}</p>
      )}
      <button
        type="button"
        disabled={page.loading}
        onClick={() => {
          void page.load(null);
        }}
      >
        {p.text("ui.reloadGrants")}
      </button>
      {page.cursor === null ? null : (
        <button
          type="button"
          disabled={page.loading}
          onClick={() => {
            void page.load(page.cursor);
          }}
        >
          {p.text("ui.moreGrants")}
        </button>
      )}
    </>
  );
};
const personPresentation = (views: ReturnType<typeof useActorGrants>["views"]) => {
  const latest = views.at(-1);
  const account = latest?.view.regions.find((region) => region.role === "ACCOUNT");
  const entries = account?.content.kind === "ENTRIES" ? account.content.entries : [];
  const actorValue = entries.map((entry) => entry.value).find((value) => value.kind === "ACTOR");
  const kindValue = entries
    .map((entry) => entry.value)
    .find((value) => value.kind === "CODE" && value.vocabulary === "principalKind");
  const body: Region | undefined =
    account === undefined
      ? undefined
      : {
          ...account,
          content: {
            kind: "ENTRIES",
            entries: entries.filter((entry) => entry.value !== actorValue),
          },
        };
  return {
    actor: actorValue?.value,
    kind: kindValue,
    body,
    timeZone: latest?.basis.timeZone ?? "UTC",
  };
};
const responsibilityLink = (offered: GrantPage["actor"]["responsibleCases"]) => {
  if (offered === undefined) {
    return undefined;
  }
  const href = worklistPath({
    view: offered.view,
    responsibleActor: offered.responsibleActor,
    lifecycle: [offered.lifecycle],
    status: [offered.status],
  });
  return {
    href,
    follow: () => {
      navigate(href);
    },
  };
};
const PersonIdentity = ({
  presentation,
  countLink,
}: {
  presentation: ReturnType<typeof personPresentation>;
  countLink: ReturnType<typeof responsibilityLink>;
}) => {
  const { actor } = presentation;
  return (
    <>
      {actor?.displayName.kind === "NAMED" ? (
        <CharacterWarning value={actor.displayName.value} />
      ) : null}
      {presentation.body === undefined ? null : (
        <RegionView
          region={presentation.body}
          timeZone={presentation.timeZone}
          {...(countLink === undefined ? {} : { countLink })}
        />
      )}
    </>
  );
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

  const presentation = personPresentation(page.views);
  const countLink = responsibilityLink(page.page?.actor.responsibleCases);
  useActorDisclosure(page.page?.actor ?? null, onActor);

  return (
    <Dialog
      title={
        presentation.actor === undefined
          ? p.text("ui.people.person")
          : actorName(p, presentation.actor)
      }
      {...(presentation.actor === undefined
        ? {}
        : { titleIcon: <ActorAvatar actor={presentation.actor} /> })}
      {...(presentation.kind === undefined
        ? {}
        : { description: p.term(presentation.kind.vocabulary, presentation.kind.token) })}
      identity={<PersonIdentity presentation={presentation} countLink={countLink} />}
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
