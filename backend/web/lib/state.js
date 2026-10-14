// Session persistence + the auto-refresh gate. Pure (storage is injected) so node --test can
// exercise it without a browser.

export const POLL_MS = 2000;
const TOKEN_KEY = "mvweb.token";
const USER_KEY = "mvweb.user";

export function createSessionStore(storage) {
  return {
    getToken() {
      return storage.getItem(TOKEN_KEY);
    },
    getUser() {
      try {
        const raw = storage.getItem(USER_KEY);
        return raw ? JSON.parse(raw) : null;
      } catch {
        return null;
      }
    },
    save(token, user) {
      storage.setItem(TOKEN_KEY, token);
      storage.setItem(USER_KEY, JSON.stringify(user ?? {}));
    },
    clear() {
      storage.removeItem(TOKEN_KEY);
      storage.removeItem(USER_KEY);
    },
  };
}

// Auto-refresh only while something is actually moving — an idle console must not hammer the API.
export function shouldPoll(meetings) {
  return (meetings || []).some((m) => m.latest_job_state === "queued" || m.latest_job_state === "running");
}
