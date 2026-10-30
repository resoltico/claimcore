import { createContext, useContext } from "react";
import type { ActorContext } from "@contracts/client";

/** The workspace's authenticated read supplies identity and calendar to its children. */
export const RequesterContext = createContext<ActorContext | null>(null);
export const useRequesterContext = () => useContext(RequesterContext);
