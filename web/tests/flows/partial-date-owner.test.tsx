import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { Dashboard } from "../../src/views/Dashboard";
import { DisclosureRefreshDriver } from "../support/people-drawer.fixtures";
import { App } from "../../src/App";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { workBasis, actorRefInput } from "../support/inputs";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
import type { SessionState } from "../../src/session/useSession";
const session = vi.hoisted(() => ({
  state: { kind: "authenticated", token: "first-token", epoch: 0 } as SessionState,
}));
vi.mock("../../src/session/useSession", () => ({
  useSession: () => ({
    state: session.state,
    logout: () => Promise.resolve(),
    refresh: () => Promise.resolve(),
  }),
}));
beforeEach(() => {
  session.state = { kind: "authenticated", token: "first-token", epoch: 0 };
});
const refreshedContext = (
  original: ApiResult<ServiceResponse<"context">>,
  different: boolean,
  offered: boolean,
): ApiResult<ServiceResponse<"context">> => {
  if (
    original.kind !== "outcome" ||
    original.value.outcome.kind !== "SUCCEEDED" ||
    original.value.outcome.data.requester.kind !== "ACTIVE"
  ) {
    throw new Error("Expected the real active context fixture.");
  }
  const current = original.value.outcome.data.requester;
  const requester = different
    ? {
        ...current,
        actorId: "10000000-0000-4000-8000-000000000099",
        self: actorRefInput({ actorId: "10000000-0000-4000-8000-000000000099" }),
      }
    : current;
  return {
    ...original,
    value: {
      outcome: {
        view: { kind: "RECORD" as const, sections: [] },
        kind: "SUCCEEDED",
        data: { requester: { ...requester, offers: offered ? requester.offers : [] } },
      },
    },
  };
};
it.each([
  { different: false, offered: true },
  { different: true, offered: true },
  { different: false, offered: false },
])(
  "preserves only the verified same owner's partial date; different owner=%j",
  async ({ different, offered }) => {
    const context = stubDashboardContext();
    mockEndpoint("case.list").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        outcome: {
          view: { kind: "RECORD" as const, sections: [] },
          kind: "SUCCEEDED",
          data: {
            basis: workBasis,
            items: [],
            offers: [{ kind: "OPEN" }],
            exactReference: null,
            nextCursor: null,
          },
        },
      },
    });
    const preview = vi.spyOn(service, "preview");
    const execute = vi.spyOn(service, "execute");
    const user = userEvent.setup();
    const view = render(<App />);
    await user.click(await screen.findByRole("link", { name: "Open a case" }));
    const date = await screen.findByRole("group", { name: "Incident date" });
    await user.click(date.querySelector<HTMLElement>('[data-type="day"]')!);
    await user.keyboard("12");
    expect(date.querySelector('[data-type="day"]')).toHaveAttribute("aria-valuenow", "12");
    const [initial] = context.mock.results;
    if (initial === undefined || initial.type !== "return") {
      throw new Error("Expected the real initial context fixture.");
    }
    const original = await initial.value;
    const refreshed = refreshedContext(original, different, offered);
    const pending = Promise.withResolvers<ApiResult<ServiceResponse<"context">>>();
    context.mockReturnValueOnce(pending.promise);
    session.state = { kind: "authenticated", token: "renewed-token", epoch: 1 };
    view.rerender(<App />);
    await waitFor(() => {
      expect(context).toHaveBeenCalledTimes(2);
    });
    expect(screen.queryByRole("group", { name: "Incident date" })).toBeNull();
    await act(async () => {
      pending.resolve(refreshed);
      await pending.promise;
    });
    if (!offered) {
      expect(screen.queryByRole("group", { name: "Incident date" })).toBeNull();
      expect(preview).not.toHaveBeenCalled();
      expect(execute).not.toHaveBeenCalled();
      return;
    }
    const restored = await screen.findByRole("group", { name: "Incident date" });
    if (different) {
      expect(restored.querySelector('[data-type="day"]')).not.toHaveAttribute("aria-valuenow");
    } else {
      await waitFor(() => {
        expect(restored.querySelector('[data-type="day"]')).toHaveAttribute("aria-valuenow", "12");
      });
    }
    expect(document.querySelector('input[name="act.values.incidentDate"]')).toHaveValue("");
    expect(preview).not.toHaveBeenCalled();
    expect(execute).not.toHaveBeenCalled();
  },
);

it.each(["NO_ROLES", "DISABLED"] as const)(
  "keeps partial creation entry inert through %s and returns it only after current access is checked",
  async (kind) => {
    const context = stubDashboardContext();
    mockEndpoint("case.list").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        outcome: {
          view: { kind: "RECORD" as const, sections: [] },
          kind: "SUCCEEDED",
          data: {
            basis: workBasis,
            items: [],
            offers: [{ kind: "OPEN" }],
            exactReference: null,
            nextCursor: null,
          },
        },
      },
    });
    const preview = vi.spyOn(service, "preview");
    const execute = vi.spyOn(service, "execute");
    let refresh: () => void = () => {
      throw new Error("The real cache refresh driver must mount.");
    };
    const user = userEvent.setup();
    render(
      <>
        <Dashboard />
        <DisclosureRefreshDriver
          capture={(invalidate) => {
            refresh = invalidate;
          }}
        />
      </>,
    );
    await user.click(await screen.findByRole("link", { name: "Open a case" }));
    const date = await screen.findByRole("group", { name: "Incident date" });
    await user.click(date.querySelector<HTMLElement>('[data-type="day"]')!);
    await user.keyboard("12");
    const [first] = context.mock.results;
    if (first === undefined || first.type !== "return") {
      throw new Error("A current context fixture must exist.");
    }
    const active = await first.value;
    if (
      active.kind !== "outcome" ||
      active.value.outcome.kind !== "SUCCEEDED" ||
      active.value.outcome.data.requester.kind !== "ACTIVE"
    ) {
      throw new Error("Expected active context.");
    }
    const self = active.value.outcome.data.requester;
    const inactive =
      kind === "NO_ROLES"
        ? {
            kind,
            self: self.self,
            installationId: self.installationId,
            businessDate: self.businessDate,
            timeZone: self.timeZone,
          }
        : { kind, self: { ...self.self, availability: "DISABLED" as const } };
    context
      .mockResolvedValueOnce({
        kind: "outcome",
        status: 200,
        value: {
          outcome: {
            view: { kind: "RECORD" as const, sections: [] },
            kind: "SUCCEEDED",
            data: { requester: inactive },
          },
        },
      })
      .mockResolvedValue(active);
    await act(() => Promise.resolve().then(refresh));
    await screen.findByRole("heading", {
      name: kind === "NO_ROLES" ? "You don’t have access yet" : "This account is disabled",
    });
    expect(screen.queryByRole("group", { name: "Incident date" })).toBeNull();
    expect(preview).not.toHaveBeenCalled();
    expect(execute).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Check access" }));
    const restored = await screen.findByRole("group", { name: "Incident date" });
    await waitFor(() => {
      expect(restored.querySelector('[data-type="day"]')).toHaveAttribute("aria-valuenow", "12");
    });
    expect(context).toHaveBeenCalledTimes(3);
    expect(preview).not.toHaveBeenCalled();
    expect(execute).not.toHaveBeenCalled();
  },
);
