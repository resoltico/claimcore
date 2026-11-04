import { createContext, useContext } from "react";
import type { RequesterState_ACTIVE } from "@contracts/client";

/** The workspace's authenticated read supplies identity and calendar to its children. */
export const RequesterContext = createContext<RequesterState_ACTIVE | null>(null);
export const useRequesterContext = () => useContext(RequesterContext);
