import { LoginScreen } from "./views/LoginScreen";
import { Dashboard } from "./views/Dashboard";
import { useSession } from "./hooks/useSession";

export const App = () => {
  const { state, login, logout, refresh } = useSession();
  if (state.kind === "loading") return <main className="loading">Loading local session…</main>;
  if (state.kind === "failure")
    return (
      <main className="loading">
        <p role="alert">{state.message}</p>
        <button type="button" onClick={() => void refresh()}>
          Try again
        </button>
      </main>
    );
  if (state.kind === "authenticated")
    return <Dashboard token={state.token} sessionEpoch={state.epoch} onLogout={logout} />;
  return (
    <LoginScreen tokenAvailable={state.token !== null} message={state.message} onLogin={login} />
  );
};
