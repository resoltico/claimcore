import { SessionContext } from "../../../src/session/SessionContext";
import type { RegionRow, HumanDocument, ResponseFor } from "@contracts/client";

import { useEndpointPage } from "../../../src/hooks/useEndpointPage";

import { act } from "@testing-library/react";
import { useLayoutEffect } from "react";
import { useEndpointCache } from "../../../src/hooks/EndpointProvider";
import { expect, it, vi } from "vitest";
import { useEndpoint } from "../../../src/hooks/useEndpoint";
import { useBusinessDate } from "../../../src/hooks/useBusinessDate";
import { render, screen, waitFor, fireEvent } from "../../support/presentation-test-support";
import { activeRequester } from "../../support/human-requester.fixtures";
const requester = {
  ...activeRequester,
  basis: {
    ...activeRequester.basis,
    observation: { ...activeRequester.basis.observation, businessDate: "2026-11-04" },
  },
};
const document = (businessDate: string): HumanDocument => ({
  screen: { regions: [] },
  subject: { kind: "LIST" },
  actions: [],
  links: [],
  history: null,
  nextCursor: null,
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-04T22:00:00.000000Z",
      businessDate,
      timeZone: "Europe/Riga",
    },
  },
  disclosure: "CURRENT",
});
const response = (businessDate: string) =>
  new Response(
    JSON.stringify({
      representation: "HUMAN",
      outcome: { kind: "SUCCEEDED", document: document(businessDate) },
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
const summaryDate = (value: ResponseFor<"case.list", "HUMAN">["outcome"] | undefined): string => {
  if (value?.kind === "SUCCEEDED") {
    if (value.document.basis.kind === "SNAPSHOT") {
      return value.document.basis.observation.businessDate;
    }
    return "No observation";
  }
  return "Loading";
};
const Probe = () => {
  const day = useBusinessDate(requester);
  const summary = useEndpoint("case.list", { limit: 50 });
  const value = summary.value?.outcome;
  return (
    <>
      <output data-testid="summary">{summaryDate(value)}</output>
      <output data-testid="change">{day.changedDate ?? "unchanged"}</output>
    </>
  );
};
it("refreshes date-dependent reads on focus and server-day changes without polling or work.overview", async () => {
  vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
  vi.setSystemTime(new Date("2031-06-15T12:00:00Z"));
  let advanced = false;
  const fetch = vi.fn((url: string) => {
    expect(url).toBe("/api/case.list");
    return Promise.resolve(response(advanced ? "2026-11-05" : "2026-11-04"));
  });
  vi.stubGlobal("fetch", fetch);
  const view = render(<Probe />);
  try {
    await waitFor(() => {
      expect(screen.getByTestId("summary")).toHaveTextContent("2026-11-04");
    });
    expect(fetch).toHaveBeenCalledTimes(1);
    advanced = true;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetch).toHaveBeenCalledTimes(1);
    fireEvent.focus(window);
    await waitFor(() => {
      expect(screen.getByTestId("summary")).toHaveTextContent("2026-11-05");
    });
    expect(screen.getByTestId("change")).toHaveTextContent("2026-11-05");
    expect(fetch).toHaveBeenCalledTimes(3);
    view.unmount();
    fireEvent.focus(window);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetch).toHaveBeenCalledTimes(3);
  } finally {
    view.unmount();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  }
});

const listInput = () => ({ limit: 50 });
const listPage = (reply: ResponseFor<"case.list", "HUMAN">) =>
  reply.outcome.kind === "SUCCEEDED" && reply.outcome.document.basis.kind === "SNAPSHOT"
    ? {
        items: reply.outcome.document.screen.regions.flatMap((region) =>
          region.content.kind === "TABLE" ? region.content.rows : [],
        ),
        nextCursor: reply.outcome.document.nextCursor,
        basis: reply.outcome.document.basis.observation,
        screen: reply.outcome.document.screen,
        disclosure: reply.outcome.document.disclosure,
      }
    : null;
const PageProbe = () => {
  const day = useBusinessDate(requester);
  const page = useEndpointPage<"case.list", RegionRow, NonNullable<ReturnType<typeof listPage>>>(
    "case.list",
    listInput,
    listPage,
  );
  return (
    <>
      <output data-testid="page-date">{page.page?.basis.businessDate ?? "loading"}</output>
      <output data-testid="page-change">{day.changedDate ?? "unchanged"}</output>
    </>
  );
};
it("refreshes completed paginated rows on focus and business-date invalidation without polling", async () => {
  vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
  let advanced = false;
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockImplementation(() => Promise.resolve(response(advanced ? "2026-11-05" : "2026-11-04")));
  vi.stubGlobal("fetch", fetch);
  const view = render(<PageProbe />);
  try {
    await waitFor(() => {
      expect(screen.getByTestId("page-date")).toHaveTextContent("2026-11-04");
    });
    advanced = true;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetch).toHaveBeenCalledTimes(1);
    fireEvent.focus(window);
    await waitFor(() => {
      expect(screen.getByTestId("page-date")).toHaveTextContent("2026-11-05");
    });
    expect(screen.getByTestId("page-change")).toHaveTextContent("2026-11-05");
    expect(fetch).toHaveBeenCalledTimes(3);
  } finally {
    view.unmount();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  }
});

it("finishes activation before refreshing, coalesces resume signals and cancels obsolete scope work", () => {
  const frames = new Map<number, FrameRequestCallback>();
  let sequence = 0;
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    frames.set(++sequence, callback);
    return sequence;
  });
  vi.stubGlobal("cancelAnimationFrame", (key: number) => {
    frames.delete(key);
  });
  const control: { invalidate?: ReturnType<typeof vi.spyOn> } = {};
  const FocusProbe = ({ available = true }: { available?: boolean }) => {
    const cache = useEndpointCache();
    useBusinessDate(available ? requester : null);
    useLayoutEffect(() => {
      control.invalidate = vi.spyOn(cache!, "invalidate");
    }, [cache]);
    return null;
  };
  const paint = () => {
    const [next] = [...frames.entries()];
    if (next !== undefined) {
      const [id, callback] = next;
      frames.delete(id);
      act(() => {
        callback(0);
      });
    }
  };
  try {
    const view = render(<FocusProbe />);
    fireEvent.focus(window);
    fireEvent(globalThis.document, new Event("visibilitychange"));
    expect(frames.size).toBe(1);
    expect(control.invalidate).not.toHaveBeenCalled();
    paint();
    expect(control.invalidate).not.toHaveBeenCalled();
    paint();
    expect(control.invalidate).toHaveBeenCalledOnce();
    fireEvent.focus(window);
    view.rerender(<FocusProbe available={false} />);
    expect(frames.size).toBe(0);
    paint();
    expect(control.invalidate).toHaveBeenCalledOnce();
  } finally {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  }
});

it.each([0, 1])(
  "keeps a queued refresh when a same-owner document replaces frame %i",
  (painted) => {
    const frames = new Map<number, FrameRequestCallback>();
    let sequence = 0;
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      frames.set(++sequence, callback);
      return sequence;
    });
    vi.stubGlobal("cancelAnimationFrame", (key: number) => {
      frames.delete(key);
    });
    const control: { invalidate?: ReturnType<typeof vi.spyOn> } = {};
    const FocusProbe = ({ source }: { source: typeof requester }) => {
      const cache = useEndpointCache();
      useBusinessDate(source);
      useLayoutEffect(() => {
        control.invalidate = vi.spyOn(cache!, "invalidate");
      }, [cache]);
      return null;
    };
    const paint = () => {
      const [next] = [...frames.entries()];
      if (next !== undefined) {
        const [id, callback] = next;
        frames.delete(id);
        act(() => {
          callback(0);
        });
      }
    };
    try {
      const view = render(<FocusProbe source={requester} />);
      fireEvent.focus(window);
      if (painted === 1) {
        paint();
      }
      view.rerender(<FocusProbe source={{ ...requester, screen: { ...requester.screen } }} />);
      expect(frames.size).toBe(1);
      paint();
      if (painted === 0) {
        paint();
      }
      expect(control.invalidate).toHaveBeenCalledOnce();
    } finally {
      vi.restoreAllMocks();
      vi.unstubAllGlobals();
    }
  },
);

it.each(["credential", "epoch", "actor", "installation"])(
  "cancels queued refresh for a replaced %s scope",
  (part) => {
    const frames = new Map<number, FrameRequestCallback>();
    let sequence = 0;
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      frames.set(++sequence, callback);
      return sequence;
    });
    vi.stubGlobal("cancelAnimationFrame", (key: number) => {
      frames.delete(key);
    });
    const control: { invalidate?: ReturnType<typeof vi.spyOn> } = {};
    const FocusProbe = ({ source }: { source: typeof requester }) => {
      const cache = useEndpointCache();
      useBusinessDate(source);
      useLayoutEffect(() => {
        control.invalidate = vi.spyOn(cache!, "invalidate");
      }, [cache]);
      return null;
    };
    const baseSession = {
      state: { kind: "authenticated" as const, token: "token", epoch: 0 },
      logout: () => Promise.resolve(),
      endedBrowserSessions: vi.fn(),
      refresh: () => Promise.resolve(),
    };
    const replacedSession = {
      ...baseSession,
      state: {
        ...baseSession.state,
        token: part === "credential" ? "different-token" : "token",
        epoch: part === "epoch" ? 1 : 0,
      },
    };
    const replaced = {
      ...requester,
      subject: {
        ...requester.subject,
        actorId: part === "actor" ? "different-actor" : requester.subject.actorId,
        installationId:
          part === "installation" ? "different-installation" : requester.subject.installationId,
      },
    };
    try {
      const view = render(
        <SessionContext value={baseSession}>
          <FocusProbe source={requester} />
        </SessionContext>,
      );
      fireEvent.focus(window);
      expect(frames.size).toBe(1);
      view.rerender(
        <SessionContext value={replacedSession}>
          <FocusProbe source={part === "actor" || part === "installation" ? replaced : requester} />
        </SessionContext>,
      );
      expect(frames.size).toBe(0);
      expect(control.invalidate).not.toHaveBeenCalled();
    } finally {
      vi.restoreAllMocks();
      vi.unstubAllGlobals();
    }
  },
);
