import { WorkspaceViewsContext } from "../../src/views/WorkspaceViews";
import { testWorkspaceViews } from "./view-counts.fixtures";
import { JournalContext } from "../../src/recovery/JournalContext";
import { testJournal } from "./recovery.fixtures";
import { RequesterContext } from "../../src/hooks/useRequesterContext";
import { activeRequester as requester } from "./human-requester.fixtures";
import { SessionContext } from "../../src/session/SessionContext";
import { EndpointProvider } from "../../src/hooks/EndpointProvider";
import { screen, render as testingRender, type RenderOptions } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { PresentationProvider } from "../../src/presentation/PresentationProvider";
export { fireEvent, screen, waitFor } from "@testing-library/react";
const noLogout = () => Promise.resolve();
export const TestSession = ({
  children,
  credential = "csrf",
  logout = noLogout,
}: {
  children: ReactNode;
  credential?: string;
  logout?: () => Promise<void>;
}) => (
  <JournalContext value={testJournal}>
    <SessionContext
      value={{
        state: { kind: "authenticated", token: credential, epoch: 0 },
        logout,
        refresh: () => Promise.resolve(),
      }}
    >
      {children}
    </SessionContext>
  </JournalContext>
);
const testSession = { kind: "authenticated", token: "token", epoch: 0 } as const;
export const testWorkspaceIdentity = {
  serviceOrigin: location.origin,
  actorId: requester.context.actorId,
  installationId: requester.context.installationId,
  credential: testSession.token,
  sessionEpoch: testSession.epoch,
};
export const TestProviders = ({ children }: { children: ReactNode }) => (
  <JournalContext value={testJournal}>
    <PresentationProvider>
      <SessionContext
        value={{
          state: testSession,
          logout: () => Promise.resolve(),
          refresh: () => Promise.resolve(),
        }}
      >
        <EndpointProvider>
          <RequesterContext value={requester}>
            <WorkspaceViewsContext value={testWorkspaceViews}>{children}</WorkspaceViewsContext>
          </RequesterContext>
        </EndpointProvider>
      </SessionContext>
    </PresentationProvider>
  </JournalContext>
);
export const render = (ui: ReactElement, options?: RenderOptions) =>
  testingRender(ui, { wrapper: TestProviders, ...options });

/** Accessible button queries shared by the interaction scenarios. */
export const getButton = (name: string | RegExp) => screen.getByRole("button", { name });
export const findButton = (name: string | RegExp) => screen.findByRole("button", { name });
