import { RequesterContext } from "../../src/hooks/useRequesterContext";
import { context as requester } from "./service-ui.fixtures";
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
  <SessionContext
    value={{
      state: { kind: "authenticated", token: credential, epoch: 0 },
      logout,
      refresh: () => Promise.resolve(),
    }}
  >
    {children}
  </SessionContext>
);
export const TestProviders = ({ children }: { children: ReactNode }) => (
  <PresentationProvider>
    <SessionContext
      value={{
        state: { kind: "authenticated", token: "token", epoch: 0 },
        logout: () => Promise.resolve(),
        refresh: () => Promise.resolve(),
      }}
    >
      <EndpointProvider>
        <RequesterContext value={requester}>{children}</RequesterContext>
      </EndpointProvider>
    </SessionContext>
  </PresentationProvider>
);
export const render = (ui: ReactElement, options?: RenderOptions) =>
  testingRender(ui, { wrapper: TestProviders, ...options });

/** Accessible button queries shared by the interaction scenarios. */
export const getButton = (name: string | RegExp) => screen.getByRole("button", { name });
export const findButton = (name: string | RegExp) => screen.findByRole("button", { name });
