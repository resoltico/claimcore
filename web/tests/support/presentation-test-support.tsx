import { screen, render as testingRender, type RenderOptions } from "@testing-library/react";
import type { ReactElement } from "react";
import { PresentationProvider } from "../../src/presentation/PresentationProvider";
export { fireEvent, screen, waitFor } from "@testing-library/react";
export const render = (ui: ReactElement, options?: RenderOptions) =>
  testingRender(ui, { wrapper: PresentationProvider, ...options });

/** Accessible button queries shared by the interaction scenarios. */
export const getButton = (name: string | RegExp) => screen.getByRole("button", { name });
export const findButton = (name: string | RegExp) => screen.findByRole("button", { name });
