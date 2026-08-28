import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  Body1,
  Button,
  Caption1,
  FluentProvider,
  MessageBar,
  MessageBarBody,
  Spinner,
  Textarea,
  Title3,
  teamsDarkTheme,
  teamsHighContrastTheme,
  teamsLightTheme
} from "@fluentui/react-components";
import { app, authentication } from "@microsoft/teams-js";
import { PublicClientApplication } from "@azure/msal-browser";
import "./styles.css";

type TeamsTheme = "default" | "dark" | "contrast";
type AuthConfig = {
  clientId: string;
  authority: string;
  apiScope: string;
  redirectUri: string;
};
type ChatRoute = {
  foundryHost: string;
  project: string;
  agent: string;
  baseUrl: string;
};
type TranscriptItem =
  | { id: string; kind: "user" | "assistant" | "error"; text: string }
  | { id: string; kind: "tool"; title: string; detail?: string }
  | { id: string; kind: "consent"; title: string; url: string }
  | { id: string; kind: "usage"; text: string };
type Approval = {
  approval_request_id: string;
  server_label?: string;
  tool_name?: string;
  arguments_summary?: string;
};

const id = () => crypto.randomUUID();

function parseRoute(): ChatRoute | null {
  const match = location.pathname.match(/^\/chat\/([^/]+)\/([^/]+)\/([^/]+)\/ui\/?$/);
  if (!match) return null;
  const [foundryHost, project, agent] = match.slice(1).map(decodeURIComponent);
  return {
    foundryHost,
    project,
    agent,
    baseUrl: `/chat/${encodeURIComponent(foundryHost)}/${encodeURIComponent(project)}/${encodeURIComponent(agent)}`
  };
}

function themeFor(value: TeamsTheme) {
  if (value === "dark") return teamsDarkTheme;
  if (value === "contrast") return teamsHighContrastTheme;
  return teamsLightTheme;
}

async function getAuthConfig(): Promise<AuthConfig> {
  const response = await fetch("/chat/auth/config");
  if (!response.ok) throw new Error("Teams tab authentication is not configured.");
  return response.json();
}

async function runAuthPopup(): Promise<void> {
  await app.initialize();
  const storageKey = "teams-tab-auth-config";
  const isCallback = location.pathname.endsWith("/auth/callback");
  const config = isCallback
    ? JSON.parse(sessionStorage.getItem(storageKey) || "null") as AuthConfig | null
    : await getAuthConfig();
  if (!config) {
    authentication.notifyFailure("The Teams agent authentication configuration is missing.");
    return;
  }
  sessionStorage.setItem(storageKey, JSON.stringify(config));
  const client = new PublicClientApplication({
    auth: {
      clientId: config.clientId,
      authority: config.authority,
      redirectUri: config.redirectUri,
      navigateToLoginRequestUrl: false
    },
    cache: { cacheLocation: "sessionStorage" }
  });
  await client.initialize();

  if (isCallback) {
    try {
      const result = await client.handleRedirectPromise();
      if (!result?.accessToken) throw new Error("Microsoft sign-in did not return an API token.");
      sessionStorage.removeItem(storageKey);
      authentication.notifySuccess(result.accessToken);
    } catch (error) {
      authentication.notifyFailure(error instanceof Error ? error.message : "Microsoft sign-in failed.");
    }
    return;
  }

  await client.loginRedirect({
    scopes: [config.apiScope],
    redirectUri: config.redirectUri
  });
}

function TeamsChat() {
  const route = useMemo(parseRoute, []);
  const [theme, setTheme] = useState<TeamsTheme>("default");
  const [token, setToken] = useState("");
  const [authError, setAuthError] = useState("");
  const [authBusy, setAuthBusy] = useState(true);
  const [conversationId, setConversationId] = useState("");
  const [items, setItems] = useState<TranscriptItem[]>([]);
  const [input, setInput] = useState("");
  const [sending, setSending] = useState(false);
  const [approval, setApproval] = useState<Approval | null>(null);
  const transcriptRef = useRef<HTMLDivElement>(null);
  const tokenRef = useRef("");

  const scroll = useCallback(() => {
    requestAnimationFrame(() => {
      if (transcriptRef.current)
        transcriptRef.current.scrollTop = transcriptRef.current.scrollHeight;
    });
  }, []);

  const verifyToken = useCallback(async (candidate: string) => {
    if (!route) throw new Error("The Teams chat route is invalid.");
    const response = await fetch(`${route.baseUrl}/context`, {
      headers: { Authorization: `Bearer ${candidate}` }
    });
    if (!response.ok) throw new Error(`The proxy rejected the Teams token (${response.status}).`);
    tokenRef.current = candidate;
    setToken(candidate);
    setAuthError("");
  }, [route]);

  const authorizedFetch = useCallback(async (url: string, init: RequestInit = {}) => {
    const send = (candidate: string) => fetch(url, {
      ...init,
      headers: {
        ...init.headers,
        Authorization: `Bearer ${candidate}`
      }
    });

    let response = await send(tokenRef.current);
    if (response.status !== 401) return response;

    try {
      const refreshed = await authentication.getAuthToken();
      await verifyToken(refreshed);
      response = await send(refreshed);
      return response;
    } catch (error) {
      tokenRef.current = "";
      setToken("");
      setAuthError(error instanceof Error ? error.message : "Your Teams sign-in expired.");
      throw error;
    }
  }, [verifyToken]);

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        await app.initialize();
        const context = await app.getContext();
        const initialTheme = context.app.theme;
        if (active && (initialTheme === "dark" || initialTheme === "contrast"))
          setTheme(initialTheme);
        app.registerOnThemeChangeHandler(next => {
          setTheme(next === "dark" || next === "contrast" ? next : "default");
        });
        const candidate = await authentication.getAuthToken();
        if (active) await verifyToken(candidate);
      } catch (error) {
        if (active)
          setAuthError(error instanceof Error ? error.message : "Teams single sign-on failed.");
      } finally {
        if (active) setAuthBusy(false);
      }
    })();
    return () => { active = false; };
  }, [verifyToken]);

  const interactiveSignIn = async () => {
    setAuthBusy(true);
    setAuthError("");
    try {
      const candidate = await authentication.authenticate({
        url: `${location.origin}/chat/auth/start`,
        width: 600,
        height: 535
      });
      await verifyToken(candidate);
    } catch (error) {
      setAuthError(error instanceof Error ? error.message : "Interactive sign-in failed.");
    } finally {
      setAuthBusy(false);
    }
  };

  const ensureConversation = async () => {
    if (conversationId) return conversationId;
    if (!route) throw new Error("The Teams chat route is invalid.");
    const response = await authorizedFetch(`${route.baseUrl}/conversations`, { method: "POST" });
    if (!response.ok) throw new Error(await response.text());
    const body = await response.json();
    setConversationId(body.conversationId);
    return body.conversationId as string;
  };

  const handleEvent = (eventName: string, data: string, assistantId: string) => {
    if (eventName === "text") {
      setItems(current => current.map(item =>
        item.id === assistantId && item.kind === "assistant"
          ? { ...item, text: item.text + data }
          : item));
    } else if (eventName === "tool") {
      const tool = JSON.parse(data);
      setItems(current => [...current, {
        id: id(),
        kind: "tool",
        title: `${tool.kind}: ${tool.tool}${tool.server ? ` on ${tool.server}` : ""}`,
        detail: tool.output || tool.args
      }]);
    } else if (eventName === "consent") {
      const consent = JSON.parse(data);
      setItems(current => [...current, {
        id: id(),
        kind: "consent",
        title: `Sign in to ${consent.serverLabel || "the connected service"}`,
        url: consent.consentLink
      }]);
    } else if (eventName === "approval") {
      setApproval(JSON.parse(data));
    } else if (eventName === "done") {
      const usage = JSON.parse(data);
      setItems(current => [...current, {
        id: id(),
        kind: "usage",
        text: `${usage.inputTokens} in · ${usage.outputTokens} out · ${usage.totalTokens} total tokens`
      }]);
    } else if (eventName === "error") {
      setItems(current => current.map(item =>
        item.id === assistantId ? { id: item.id, kind: "error", text: data } : item));
    }
    scroll();
  };

  const stream = async (payload: object) => {
    if (!route) return;
    const assistantId = id();
    setItems(current => [...current, { id: assistantId, kind: "assistant", text: "" }]);
    const response = await authorizedFetch(`${route.baseUrl}/messages`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify(payload)
    });
    if (!response.ok || !response.body) throw new Error(await response.text());

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let separator = buffer.indexOf("\n\n");
      while (separator >= 0) {
        const block = buffer.slice(0, separator);
        buffer = buffer.slice(separator + 2);
        let eventName = "message";
        const data: string[] = [];
        for (const line of block.split("\n")) {
          if (line.startsWith("event: ")) eventName = line.slice(7).trim();
          if (line.startsWith("data: ")) data.push(line.slice(6));
        }
        handleEvent(eventName, data.join("\n"), assistantId);
        separator = buffer.indexOf("\n\n");
      }
    }
  };

  const send = async () => {
    const text = input.trim();
    if (!text || sending || approval) return;
    setSending(true);
    setInput("");
    setItems(current => [...current, { id: id(), kind: "user", text }]);
    try {
      const currentConversation = await ensureConversation();
      await stream({ conversationId: currentConversation, message: text });
    } catch (error) {
      setItems(current => [...current, {
        id: id(),
        kind: "error",
        text: error instanceof Error ? error.message : "Chat request failed."
      }]);
    } finally {
      setSending(false);
      scroll();
    }
  };

  const answerApproval = async (approve: boolean) => {
    if (!approval || !conversationId) return;
    setSending(true);
    try {
      await stream({
        conversationId,
        approval: { requestId: approval.approval_request_id, approve }
      });
      setApproval(null);
    } finally {
      setSending(false);
    }
  };

  const reset = async () => {
    if (route && conversationId) {
      await authorizedFetch(
        `${route.baseUrl}/conversations/${encodeURIComponent(conversationId)}`,
        { method: "DELETE" }).catch(() => undefined);
    }
    setConversationId("");
    setApproval(null);
    setItems([]);
  };

  if (!route) {
    return <MessageBar intent="error"><MessageBarBody>Invalid Teams chat URL.</MessageBarBody></MessageBar>;
  }

  return (
    <FluentProvider theme={themeFor(theme)} className="provider">
      <main className="app-shell">
        <header className="app-header">
          <div>
            <Title3>{route.agent}</Title3>
            <Caption1>{route.foundryHost} / {route.project}</Caption1>
          </div>
          <Button appearance="subtle" onClick={reset} disabled={!token || sending}>New chat</Button>
        </header>

        {authBusy && <div className="center"><Spinner label="Connecting to Teams…" /></div>}
        {!authBusy && !token && (
          <div className="auth-panel">
            <MessageBar intent="warning">
              <MessageBarBody>{authError || "Sign in to continue."}</MessageBarBody>
            </MessageBar>
            <Button appearance="primary" onClick={interactiveSignIn}>Continue with Microsoft</Button>
          </div>
        )}

        {token && (
          <>
            <section className="transcript" ref={transcriptRef} aria-live="polite">
              {items.length === 0 && (
                <div className="empty">
                  <Title3>How can I help?</Title3>
                  <Body1>Messages are sent to the Foundry agent as your signed-in Teams identity.</Body1>
                </div>
              )}
              {items.map(item => {
                if (item.kind === "tool")
                  return <div className="tool-card" key={item.id}><strong>{item.title}</strong>{item.detail && <pre>{item.detail}</pre>}</div>;
                if (item.kind === "consent")
                  return <div className="tool-card" key={item.id}><strong>{item.title}</strong><Button as="a" href={item.url} target="_blank" appearance="primary">Open sign-in</Button></div>;
                if (item.kind === "usage")
                  return <Caption1 className="usage" key={item.id}>{item.text}</Caption1>;
                return <div className={`message ${item.kind}`} key={item.id}>{item.text || (sending ? "…" : "")}</div>;
              })}
              {approval && (
                <div className="approval-card">
                  <strong>Approve tool call?</strong>
                  <Body1>{approval.tool_name || "Tool"} on {approval.server_label || "MCP server"}</Body1>
                  {approval.arguments_summary && <pre>{approval.arguments_summary}</pre>}
                  <div className="approval-actions">
                    <Button appearance="primary" onClick={() => answerApproval(true)}>Approve</Button>
                    <Button onClick={() => answerApproval(false)}>Deny</Button>
                  </div>
                </div>
              )}
            </section>
            <footer className="composer">
              <Textarea
                resize="vertical"
                value={input}
                placeholder="Message the agent"
                disabled={sending || Boolean(approval)}
                onChange={(_, data) => setInput(data.value)}
                onKeyDown={event => {
                  if (event.key === "Enter" && !event.shiftKey) {
                    event.preventDefault();
                    void send();
                  }
                }}
              />
              <Button appearance="primary" onClick={send} disabled={!input.trim() || sending || Boolean(approval)}>
                Send
              </Button>
            </footer>
          </>
        )}
      </main>
    </FluentProvider>
  );
}

async function bootstrap() {
  if (location.pathname === "/chat/auth/start" || location.pathname === "/chat/auth/callback") {
    await runAuthPopup();
    return;
  }
  createRoot(document.getElementById("root")!).render(
    <React.StrictMode><TeamsChat /></React.StrictMode>
  );
}

void bootstrap();
