import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createInterface, type Interface } from "node:readline";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { McpServer, type CallToolResult } from "@modelcontextprotocol/server";
import { serveStdio } from "@modelcontextprotocol/server/stdio";
import { z } from "zod";

type JsonObject = Record<string, unknown>;
type RequestId = number | string;

interface NativeErrorPayload {
  code?: string;
  message?: string;
  details?: unknown;
}

interface NativeResponse {
  id?: RequestId | null;
  result?: unknown;
  error?: NativeErrorPayload;
}

class NativeRpcError extends Error {
  readonly code: string;
  readonly details: unknown;

  constructor(payload: NativeErrorPayload) {
    super(payload.message ?? "Computer Use for Antigravity native runtime failed.");
    this.name = "NativeRpcError";
    this.code = payload.code ?? "NATIVE_ERROR";
    this.details = payload.details;
  }
}

interface PendingCall {
  resolve: (value: unknown) => void;
  reject: (reason?: unknown) => void;
  timer: NodeJS.Timeout;
}

class NativeRpcClient {
  private child: ChildProcessWithoutNullStreams | undefined;
  private lines: Interface | undefined;
  private nextId = 1;
  private startup: Promise<void> | undefined;
  private readonly pending = new Map<RequestId, PendingCall>();

  async call(method: string, params: JsonObject, timeoutMs = 60_000): Promise<unknown> {
    await this.ensureStarted();
    const child = this.child;
    if (!child || child.exitCode !== null) {
      throw new Error("Computer Use for Antigravity native runtime is not running.");
    }

    const id = this.nextId++;
    const request = JSON.stringify({ id, method, params });

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Native method '${method}' timed out after ${timeoutMs} ms.`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });

      try {
        child.stdin.write(`${request}\n`);
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(error);
      }
    });
  }

  async close(): Promise<void> {
    const child = this.child;
    this.lines?.close();
    this.lines = undefined;
    this.child = undefined;
    if (!child || child.exitCode !== null) {
      return;
    }

    child.stdin.end();
    await new Promise<void>((resolve) => {
      const timer = setTimeout(() => {
        child.kill();
        resolve();
      }, 1_000);
      child.once("exit", () => {
        clearTimeout(timer);
        resolve();
      });
    });
  }

  private async ensureStarted(): Promise<void> {
    if (this.child && this.child.exitCode === null) {
      return;
    }

    if (!this.startup) {
      this.startup = this.startProcess().finally(() => {
        this.startup = undefined;
      });
    }

    await this.startup;
  }

  private startProcess(): Promise<void> {
    const nativePath = process.env.COMPUTER_USE_NATIVE
      ? path.resolve(process.env.COMPUTER_USE_NATIVE)
      : path.resolve(
          path.dirname(fileURLToPath(import.meta.url)),
          "../../dist/native/ComputerUse.Native.exe",
        );

    const child = spawn(nativePath, [], {
      cwd: path.dirname(nativePath),
      env: process.env,
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
    this.child = child;

    this.lines = createInterface({ input: child.stdout });
    this.lines.on("line", (line) => this.handleLine(line));
    child.stderr.on("data", (chunk: Buffer) => {
      process.stderr.write(`[computer-use-native] ${chunk.toString()}`);
    });

    child.once("exit", (code, signal) => {
      this.lines?.close();
      this.lines = undefined;
      if (this.child === child) {
        this.child = undefined;
      }

      const reason = new Error(
        `Computer Use for Antigravity native runtime exited (code=${code ?? "null"}, signal=${signal ?? "null"}).`,
      );
      this.rejectPending(reason);
    });

    return new Promise<void>((resolve, reject) => {
      const onSpawn = () => {
        child.off("error", onError);
        resolve();
      };
      const onError = (error: Error) => {
        child.off("spawn", onSpawn);
        if (this.child === child) {
          this.child = undefined;
        }
        reject(new Error(`Unable to start '${nativePath}': ${error.message}`));
      };

      child.once("spawn", onSpawn);
      child.once("error", onError);
    });
  }

  private handleLine(line: string): void {
    if (!line.trim()) {
      return;
    }

    let response: NativeResponse;
    try {
      response = JSON.parse(line) as NativeResponse;
    } catch {
      process.stderr.write(`[computer-use-native] Ignoring invalid JSON response: ${line}\n`);
      return;
    }

    if (response.id === undefined || response.id === null) {
      return;
    }

    const pending = this.pending.get(response.id);
    if (!pending) {
      return;
    }

    this.pending.delete(response.id);
    clearTimeout(pending.timer);
    if (response.error) {
      pending.reject(new NativeRpcError(response.error));
    } else {
      pending.resolve(response.result);
    }
  }

  private rejectPending(reason: Error): void {
    for (const [id, pending] of this.pending) {
      clearTimeout(pending.timer);
      pending.reject(reason);
      this.pending.delete(id);
    }
  }
}

const native = new NativeRpcClient();

const coordinateSpaceSchema = z.enum(["screen", "window", "normalized"]);

const targetSelectorSchema = z
  .object({
    element_id: z.number().int().optional(),
    id: z.number().int().optional(),
    name: z.string().optional(),
    role: z.string().optional(),
    automation_id: z.string().optional(),
    x: z.number().optional(),
    y: z.number().optional(),
    coordinate_space: coordinateSpaceSchema.optional(),
  })
  .passthrough();

const postconditionTargetSchema = z.union([targetSelectorSchema, z.string()]);
const postconditionTargetOrWrapperSchema = z.union([
  postconditionTargetSchema,
  z
    .object({
      target: postconditionTargetSchema,
    })
    .passthrough(),
]);
const postconditionSchema = z
  .object({
    element: postconditionTargetOrWrapperSchema.optional(),
    element_absent: postconditionTargetOrWrapperSchema.optional(),
    element_enabled: postconditionTargetOrWrapperSchema.optional(),
    element_disabled: postconditionTargetOrWrapperSchema.optional(),
    value: z
      .object({
        target: postconditionTargetSchema,
        equals: z.string(),
      })
      .passthrough()
      .optional(),
    window_title_contains: z.string().min(1).optional(),
    ui_changed: z.boolean().optional(),
    ui_stable: z.boolean().optional(),
  })
  .passthrough();

const retrySchema = z
  .object({
    max_attempts: z.number().int().min(1).max(5).optional(),
    delay_ms: z.number().int().min(0).max(5_000).optional(),
  })
  .passthrough();

const actionSchema = z
  .object({
    type: z.enum([
      "click",
      "double_click",
      "right_click",
      "type_text",
      "set_value",
      "press_key",
      "hotkey",
      "scroll",
      "drag",
      "wait",
    ]),
    element_id: z.number().int().optional(),
    target: z.union([targetSelectorSchema, z.string()]).optional(),
    name: z.string().optional(),
    role: z.string().optional(),
    automation_id: z.string().optional(),
    x: z.number().optional(),
    y: z.number().optional(),
    coordinate_space: coordinateSpaceSchema.optional(),
    text: z.string().optional(),
    value: z.string().optional(),
    key: z.string().optional(),
    modifiers: z.array(z.string()).optional(),
    amount: z.number().int().optional(),
    milliseconds: z.number().int().optional(),
    start_target: z.union([targetSelectorSchema, z.string()]).optional(),
    end_target: z.union([targetSelectorSchema, z.string()]).optional(),
    start: z.union([targetSelectorSchema, z.string()]).optional(),
    end: z.union([targetSelectorSchema, z.string()]).optional(),
    expect: postconditionSchema.optional(),
    retry: retrySchema.optional(),
  })
  .passthrough();

const observeSchema = z.object({
  window_id: z.string().min(1),
});

const actSchema = z.object({
  state_id: z.string().min(1),
  window_id: z.string().min(1).optional(),
  action: actionSchema,
});

const performSchema = z.object({
  window_id: z.string().min(1),
  actions: z.array(actionSchema).max(64),
  verify: z.boolean().optional(),
});

const waitSchema = z.object({
  window_id: z.string().min(1).optional(),
  title_contains: z.string().min(1).optional(),
  process_contains: z.string().min(1).optional(),
  milliseconds: z.number().int().min(0).max(60_000).optional(),
  timeout_ms: z.number().int().min(0).max(60_000).optional(),
  poll_ms: z.number().int().min(25).max(2_000).optional(),
});

const launchSchema = z.object({
  path: z.string().min(1),
  args: z.union([z.string(), z.array(z.string())]).optional(),
  working_directory: z.string().min(1).optional(),
  wait_for_window: z.boolean().optional(),
  title_contains: z.string().min(1).optional(),
  timeout_ms: z.number().int().min(0).max(60_000).optional(),
  poll_ms: z.number().int().min(25).max(2_000).optional(),
});

function jsonText(value: unknown): string {
  return JSON.stringify(value, null, 2);
}

function normalResult(value: unknown): CallToolResult {
  return {
    content: [{ type: "text", text: jsonText(value) }],
  };
}

function visualResult(value: unknown): CallToolResult {
  if (!value || typeof value !== "object") {
    return normalResult(value);
  }

  const record = value as Record<string, unknown>;
  const screenshot = typeof record.screenshot === "string" ? record.screenshot : undefined;
  const metadata = { ...record };
  delete metadata.screenshot;

  const content: CallToolResult["content"] = [
    { type: "text", text: jsonText(metadata) },
  ];
  if (screenshot) {
    content.push({ type: "image", data: screenshot, mimeType: "image/png" });
  }

  return { content };
}

function errorResult(error: unknown): CallToolResult {
  if (error instanceof NativeRpcError) {
    return {
      isError: true,
      content: [
        {
          type: "text",
          text: jsonText({
            error: {
              code: error.code,
              message: error.message,
              details: error.details,
            },
          }),
        },
      ],
    };
  }

  const message = error instanceof Error ? error.message : String(error);
  return {
    isError: true,
    content: [{ type: "text", text: jsonText({ error: { code: "MCP_ERROR", message } }) }],
  };
}

async function callTool(
  method: string,
  params: JsonObject,
  visual = false,
): Promise<CallToolResult> {
  activeToolCalls += 1;
  try {
    const result = await native.call(method, params);
    return visual ? visualResult(result) : normalResult(result);
  } catch (error) {
    return errorResult(error);
  } finally {
    activeToolCalls -= 1;
  }
}

function createServer(): McpServer {
  const server = new McpServer({ name: "computer-use", version: "0.3.0" });

  server.registerTool(
    "computer_list_windows",
    {
      title: "List Windows",
      description: "List visible top-level Windows application windows.",
      inputSchema: z.object({}),
    },
    async () => callTool("list_windows", {}),
  );

  server.registerTool(
    "computer_observe",
    {
      title: "Observe Window",
      description:
        "Capture a Windows application window as a screenshot plus a semantic UI Automation element tree. The response reports the capture backend, fallback diagnostics, and that screenshot coordinates are window-relative while UI Automation bounds are screen-relative. Use this before element-id actions.",
      inputSchema: observeSchema,
    },
    async (args) => callTool("observe", args, true),
  );

  server.registerTool(
    "computer_act",
    {
      title: "Act on Window",
      description:
        "Execute one UI action against a recent computer_observe state. Element ids are state-bound and fail safely with STALE_STATE when the UI changed; ambiguous semantic selectors fail with AMBIGUOUS_TARGET and candidates instead of guessing. Coordinate targets support screen, window, and normalized spaces.",
      inputSchema: actSchema,
    },
    async (args) => callTool("act", args),
  );

  server.registerTool(
    "computer_perform",
    {
      title: "Perform Actions",
      description:
        "Execute a bounded workflow with deterministic postconditions, retry/re-observe recovery, automatic stale-state recovery, and a compact execution trace. Semantic ambiguity fails closed; coordinate targets support screen, window, and normalized spaces.",
      inputSchema: performSchema,
    },
    async (args) => callTool("perform", args, true),
  );

  server.registerTool(
    "computer_wait_for",
    {
      title: "Wait for Window",
      description:
        "Wait for a visible window/title/process condition or for a bounded delay before the next computer-use action.",
      inputSchema: waitSchema,
    },
    async (args) => callTool("wait_for", args),
  );

  server.registerTool(
    "computer_launch",
    {
      title: "Launch Application",
      description:
        "Launch a Windows application as the current user. Computer Use for Antigravity does not elevate itself; elevated targets are reported separately.",
      inputSchema: launchSchema,
    },
    async (args) => callTool("launch", args),
  );

  return server;
}

const handle = serveStdio(createServer, {
  onerror: (error) => {
    process.stderr.write(`[computer-use-for-antigravity-mcp] ${error.stack ?? error.message}\n`);
  },
});

let activeToolCalls = 0;
let shutdownPromise: Promise<void> | undefined;

const shutdown = (): Promise<void> => {
  if (!shutdownPromise) {
    shutdownPromise = (async () => {
      await handle.close();
      await native.close();
    })();
  }

  return shutdownPromise;
};

const shutdownAfterStdinEnd = async () => {
  // serveStdio queues inbound messages internally. Give that queue time to
  // dispatch the final messages, then wait for any active native call before
  // closing the child process.
  await new Promise((resolve) => setTimeout(resolve, 100));
  let idleChecks = 0;
  while (idleChecks < 3) {
    if (activeToolCalls === 0) {
      idleChecks += 1;
    } else {
      idleChecks = 0;
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  await shutdown();
};

process.stdin.once("end", () => void shutdownAfterStdinEnd());
process.once("SIGINT", () => void shutdown());
process.once("SIGTERM", () => void shutdown());
