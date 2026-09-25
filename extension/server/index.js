#!/usr/bin/env node
// TodoWall MCP server.
//
// Claude Desktop starts this over stdio. Every tool call becomes one request down
// the named pipe the running TodoWall listens on (\\.\pipe\TodoWall.Bridge), and
// TodoWall applies the change on its own UI thread, so the board updates on the
// desktop the moment Claude adds a task - no file juggling, no restart.
//
// Tasks have no ids. A task is addressed by its day and its position in that day's
// list; the text is passed along as a check so that a board that has changed since
// it was last listed is not edited blind.

import net from "node:net";
import fs from "node:fs";
import { spawn } from "node:child_process";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const PIPE = "\\\\.\\pipe\\TodoWall.Bridge";
const EXE = (process.env.TODOWALL_EXE || "").trim();

// ------------------------------------------------------------------ dates

const WEEKDAYS = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
const WD_SHORT = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

function iso(d) {
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}
function localToday() {
  const n = new Date();
  return new Date(n.getFullYear(), n.getMonth(), n.getDate());
}
function addDays(d, n) {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate() + n);
}
function mondayOf(d) {
  return addDays(d, -((d.getDay() + 6) % 7));
}
function parseIso(s) {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s);
  if (!m) return null;
  const d = new Date(+m[1], +m[2] - 1, +m[3]);
  return iso(d) === s ? d : null;
}

/** "2026-09-25", "today", "tomorrow", "friday", "next monday", "+3" -> Date (local). */
function resolveDate(input, what = "date") {
  const s = String(input ?? "").trim().toLowerCase();
  if (!s || s === "today") return localToday();
  if (s === "tomorrow") return addDays(localToday(), 1);
  if (s === "yesterday") return addDays(localToday(), -1);
  const exact = parseIso(s);
  if (exact) return exact;
  const rel = /^([+-]\d+)$/.exec(s);
  if (rel) return addDays(localToday(), +rel[1]);

  const nextM = /^(?:next\s+)?([a-z]+)$/.exec(s);
  if (nextM) {
    const idx = WEEKDAYS.findIndex((w) => w === nextM[1] || w.slice(0, 3) === nextM[1]);
    if (idx >= 0) {
      const t = localToday();
      let delta = (idx - t.getDay() + 7) % 7;
      if (s.startsWith("next ")) delta = delta === 0 ? 7 : delta + 7;
      return addDays(t, delta);
    }
  }
  throw new Error(
    `Could not understand ${what} "${input}". Use YYYY-MM-DD, today, tomorrow, a weekday name, or "next <weekday>".`
  );
}

const DateArg = z
  .string()
  .describe('A calendar day: "YYYY-MM-DD", "today", "tomorrow", a weekday name like "friday" (the next one, today included), or "next monday".');

// ------------------------------------------------------------------ pipe

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

function once(request) {
  return new Promise((resolve, reject) => {
    const sock = net.createConnection({ path: PIPE });
    let buf = "";
    let settled = false;
    const done = (fn, v) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      sock.destroy();
      fn(v);
    };
    const timer = setTimeout(() => done(reject, new Error("TodoWall did not answer within 10 s.")), 10000);
    sock.setEncoding("utf8");
    sock.on("connect", () => sock.write(JSON.stringify(request) + "\n"));
    sock.on("data", (chunk) => {
      buf += chunk;
      const nl = buf.indexOf("\n");
      if (nl >= 0) done(resolve, buf.slice(0, nl));
    });
    sock.on("end", () => {
      if (buf.trim()) done(resolve, buf.trim());
      else done(reject, Object.assign(new Error("TodoWall closed the pipe without answering."), { code: "EEMPTY" }));
    });
    sock.on("error", (e) => done(reject, e));
  });
}

let launched = false;
function tryLaunch() {
  if (launched || !EXE) return false;
  launched = true;
  try {
    if (!fs.existsSync(EXE)) return false;
    const child = spawn(EXE, [], { detached: true, stdio: "ignore", windowsHide: true });
    child.unref();
    return true;
  } catch {
    return false;
  }
}

const RETRIABLE = new Set(["ENOENT", "EBUSY", "ECONNREFUSED", "EPIPE", "ECONNRESET", "EEMPTY"]);

/** Send one request. Retries while the single pipe instance is busy with another
 *  caller, and (when an exe path is configured) starts TodoWall if it is not running. */
async function call(request) {
  const deadline = Date.now() + 4000;
  let extended = false;
  let lastErr;
  for (;;) {
    try {
      const line = await once(request);
      let reply;
      try {
        reply = JSON.parse(line);
      } catch {
        throw new Error("TodoWall sent something that was not JSON: " + line.slice(0, 200));
      }
      if (!reply || reply.ok !== true) throw new Error(reply?.error || "TodoWall refused the request.");
      return reply;
    } catch (e) {
      lastErr = e;
      if (!RETRIABLE.has(e.code)) throw e;
      if (e.code === "ENOENT" && !extended && tryLaunch()) {
        extended = true;
        // First paint plus the welcome screen; the pipe opens right after.
        await sleep(1500);
        continue;
      }
      if (Date.now() > deadline + (extended ? 12000 : 0)) break;
      await sleep(150);
    }
  }
  if (lastErr?.code === "ENOENT") {
    throw new Error(
      "TodoWall is not running on this PC. Start it (double-click TodoWall.exe) and try again." +
        (EXE ? "" : " Tip: set the TodoWall.exe path in this extension's settings and it will be started automatically.")
    );
  }
  throw lastErr;
}

// ------------------------------------------------------------------ formatting

function fmtDay(day) {
  const d = parseIso(day.date);
  const head = `${WD_SHORT[d.getDay()]} ${day.date}`;
  if (!day.tasks.length) return `${head}: (no tasks)`;
  const lines = day.tasks.map((t) => `  [${t.index}] ${t.done ? "✓ " : ""}${t.text}`);
  return `${head}\n${lines.join("\n")}`;
}

function text(s) {
  return { content: [{ type: "text", text: s }] };
}
function fail(e) {
  return { isError: true, content: [{ type: "text", text: String(e?.message || e) }] };
}

// ------------------------------------------------------------------ server

const server = new McpServer({ name: "todowall", version: "1.0.3" });

server.registerTool(
  "get_board",
  {
    title: "Show the TodoWall board",
    description:
      "List the tasks on the TodoWall desktop board, day by day, with each task's index. " +
      "Defaults to this week and next (Monday to Sunday). Also reports today's date. " +
      "Call this before completing, moving or removing a task so you have current indices.",
    inputSchema: {
      from: DateArg.optional().describe("First day to show. Default: Monday of this week."),
      to: DateArg.optional().describe("Last day to show. Default: Sunday of next week."),
    },
    annotations: { readOnlyHint: true, openWorldHint: false },
  },
  async ({ from, to }) => {
    try {
      const req = { op: "list" };
      if (from) req.from = iso(resolveDate(from, "from"));
      if (to) req.to = iso(resolveDate(to, "to"));
      const r = await call(req);
      const t = parseIso(r.today);
      const header = `Today is ${WD_SHORT[t.getDay()]} ${r.today}. Board from ${r.from} to ${r.to}:`;
      const open = r.days.reduce((n, d) => n + d.tasks.filter((x) => !x.done).length, 0);
      const done = r.days.reduce((n, d) => n + d.tasks.filter((x) => x.done).length, 0);
      return text([header, "", ...r.days.map(fmtDay), "", `${open} open, ${done} done.`].join("\n"));
    } catch (e) {
      return fail(e);
    }
  }
);

server.registerTool(
  "add_tasks",
  {
    title: "Add tasks to TodoWall",
    description:
      "Add one or more tasks to the TodoWall desktop board. Each task goes under a day. " +
      "Keep task text short, one line, like a sticky note. They appear on the desktop immediately.",
    inputSchema: {
      tasks: z
        .array(
          z.object({
            text: z.string().min(1).max(2000).describe("The task, one short line."),
            date: DateArg.default("today"),
          })
        )
        .min(1)
        .max(200)
        .describe("The tasks to add."),
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  },
  async ({ tasks }) => {
    try {
      const items = tasks.map((t) => ({ date: iso(resolveDate(t.date)), text: t.text.trim() }));
      const r = await call({ op: "add", items });
      const lines = r.added.map((a) => {
        const d = parseIso(a.date);
        return `• ${a.text}  →  ${WD_SHORT[d.getDay()]} ${a.date} [${a.index}]`;
      });
      return text(`Added ${r.added.length} task${r.added.length === 1 ? "" : "s"}:\n${lines.join("\n")}`);
    } catch (e) {
      return fail(e);
    }
  }
);

const Locator = {
  date: DateArg.describe("The day the task is on."),
  index: z.number().int().min(0).describe("The task's index on that day, as shown by get_board."),
  text: z
    .string()
    .optional()
    .describe("The task's text as last seen. Used to double-check the index, and to find the task if the list has shifted."),
};

server.registerTool(
  "complete_task",
  {
    title: "Tick or untick a task",
    description: "Mark a TodoWall task done (or not done). Identify it by day and index from get_board.",
    inputSchema: {
      ...Locator,
      done: z.boolean().default(true).describe("true to tick it, false to untick."),
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  },
  async ({ date, index, text: t, done }) => {
    try {
      const r = await call({ op: "complete", date: iso(resolveDate(date)), index, text: t, done });
      return text(`${done ? "Done" : "Reopened"}: "${r.task.text}" on ${r.task.date}.`);
    } catch (e) {
      return fail(e);
    }
  }
);

server.registerTool(
  "move_task",
  {
    title: "Move a task to another day",
    description: "Move a TodoWall task to a different day. Identify it by day and index from get_board.",
    inputSchema: {
      ...Locator,
      to: DateArg.describe("The day to move it to."),
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  },
  async ({ date, index, text: t, to }) => {
    try {
      const r = await call({ op: "move", date: iso(resolveDate(date)), index, text: t, to: iso(resolveDate(to, "to")) });
      return text(`Moved "${r.task.text}" to ${r.task.date} [${r.task.index}].`);
    } catch (e) {
      return fail(e);
    }
  }
);

server.registerTool(
  "remove_task",
  {
    title: "Delete a task",
    description:
      "Delete a TodoWall task outright. Prefer complete_task unless the user clearly wants it gone. " +
      "Identify it by day and index from get_board.",
    inputSchema: { ...Locator },
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
  },
  async ({ date, index, text: t }) => {
    try {
      const r = await call({ op: "remove", date: iso(resolveDate(date)), index, text: t });
      return text(`Removed "${r.removed.text}" from ${r.removed.date}.`);
    } catch (e) {
      return fail(e);
    }
  }
);

const transport = new StdioServerTransport();
await server.connect(transport);
