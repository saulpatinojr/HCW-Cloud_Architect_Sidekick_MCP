#!/usr/bin/env python3
"""End-to-end check of the Obsidian vault MCP server (ADR-0010 gate 1), over stdio or over HTTP.

Speaks MCP JSON-RPC to the server (initialize, tools/list, tools/call) and checks that exactly the four read-only
tools are listed and that a search answers. No MCP client library is needed.

    # phase 1, the stdio host, against the built dll (a throw-away vault is created when --vault is omitted)
    python3 src/VaultMcp/smoke-test.py --dll src/VaultMcp/VaultMcp.Stdio/bin/Release/net10.0/VaultMcp.Stdio.dll

    # phase 2, the HTTP host (streamable HTTP), against a running server
    python3 src/VaultMcp/smoke-test.py --url http://127.0.0.1:8080/mcp --private-note Personal/Diary.md

With a throw-away vault the script also checks that the private note is refused. Over HTTP the vault is whatever
the server was started with; pass --private-note to check one path outside the allow-list is refused.
Against a server in Entra mode (phase 2, step 3) put an access token for the vault server in the environment
variable VAULT_MCP_TOKEN (for example from `az account get-access-token --scope api://<client id>/user_impersonation
--query accessToken -o tsv`) and add --bearer-from-env; the token is read from the environment, never from the command line.
Exit code 0 means every line printed OK.
"""
import argparse
import json
import os
import shutil
import subprocess  # nosec B404: the only process started is the .NET runtime, with arguments from this script's own command line
import sys
import tempfile
import urllib.error
import urllib.request

EXPECTED_TOOLS = ["vault_backlinks", "vault_note_get", "vault_search", "vault_tags"]
PROTOCOL = "2025-06-18"


def make_temp_vault() -> str:
    root = tempfile.mkdtemp(prefix="vaultmcp-smoke-")
    os.makedirs(os.path.join(root, "Work"))
    os.makedirs(os.path.join(root, "Personal"))
    with open(os.path.join(root, "adr-sidekick.md"), "w", encoding="utf-8") as f:
        f.write("---\nallow: [Work]\n---\n")
    with open(os.path.join(root, "Work", "Cosmos decision.md"), "w", encoding="utf-8") as f:
        f.write("# Cosmos decision\n#adr-context\nWe chose Cosmos. See [[Naming]].\n")
    with open(os.path.join(root, "Work", "Naming.md"), "w", encoding="utf-8") as f:
        f.write("naming rules\n")
    with open(os.path.join(root, "Personal", "Diary.md"), "w", encoding="utf-8") as f:
        f.write("private cosmos diary\n")
    return root


class StdioTransport:
    """Newline-delimited JSON-RPC over the server's stdin and stdout."""

    def __init__(self, dll: str, vault: str):
        dotnet = shutil.which("dotnet")
        if dotnet is None:
            raise SystemExit("FAIL the .NET runtime ('dotnet') is not on PATH; install the .NET 10 SDK first")
        if not os.path.isfile(dll):
            raise SystemExit(f"FAIL {dll} not found; build first: dotnet build src/VaultMcp/VaultMcp.sln -c Release")
        # No shell is involved; the argument list is fixed apart from the paths given on this script's own command line.
        self.proc = subprocess.Popen([dotnet, dll, "--vault", vault], stdin=subprocess.PIPE, stdout=subprocess.PIPE,  # nosec B603
                                     stderr=subprocess.PIPE, text=True, encoding="utf-8")
        self.next_id = 0

    def _write(self, msg):
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()

    def request(self, method, params=None):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        self._write(msg)
        while True:
            line = self.proc.stdout.readline()
            if not line:
                sys.stderr.write("server closed its output; its stderr was:\n" + self.proc.stderr.read())
                raise SystemExit(1)
            line = line.strip()
            if line:
                reply = json.loads(line)
                if "error" in reply:
                    raise SystemExit(f"FAIL protocol error: {reply['error']}")
                return reply["result"]

    def notify(self, method, params=None):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        self._write(msg)

    def close(self) -> int:
        self.proc.stdin.close()
        self.proc.wait(timeout=15)
        return self.proc.returncode


class HttpTransport:
    """MCP streamable HTTP: one POST per message; the answer is JSON or a short SSE stream."""

    def __init__(self, url: str, token: str | None = None):
        self.url = url
        self.token = token
        self.session = None
        self.next_id = 0

    def _post(self, msg):
        headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream",
                   "MCP-Protocol-Version": PROTOCOL}
        if self.session:
            headers["Mcp-Session-Id"] = self.session
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        req = urllib.request.Request(self.url, data=json.dumps(msg).encode("utf-8"), headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:  # nosec B310: the URL comes from this script's own command line
                sid = resp.headers.get("Mcp-Session-Id")
                if sid:
                    self.session = sid
                return resp.status, resp.headers.get("Content-Type", ""), resp.read().decode("utf-8")
        except urllib.error.HTTPError as e:
            return e.code, e.headers.get("Content-Type", ""), e.read().decode("utf-8", "replace")

    def request(self, method, params=None):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        status, content_type, body = self._post(msg)
        if status >= 400:
            raise SystemExit(f"FAIL HTTP {status} from {method}: {body[:200]}")
        replies = []
        if content_type.startswith("text/event-stream"):
            for line in body.split("\n"):
                if line.startswith("data:"):
                    replies.append(json.loads(line[5:].strip()))
        elif body.strip():
            replies.append(json.loads(body))
        for reply in replies:
            if reply.get("id") == self.next_id:
                if "error" in reply:
                    raise SystemExit(f"FAIL protocol error: {reply['error']}")
                return reply["result"]
        raise SystemExit(f"FAIL no reply with id {self.next_id} in: {body[:200]}")

    def notify(self, method, params=None):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        status, _, body = self._post(msg)
        if status >= 400:
            raise SystemExit(f"FAIL HTTP {status} from {method}: {body[:200]}")

    def close(self) -> int:
        return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    target = ap.add_mutually_exclusive_group(required=True)
    target.add_argument("--dll", help="path to VaultMcp.Stdio.dll (build first): the stdio host is started here")
    target.add_argument("--url", help="MCP endpoint of a running HTTP host, for example http://127.0.0.1:8080/mcp")
    ap.add_argument("--vault", help="stdio only: vault folder; omitted = a throw-away vault with a private folder to refuse")
    ap.add_argument("--query", default="cosmos", help="search word for the vault_search check")
    ap.add_argument("--private-note", help="a note path outside the allow-list that must be refused (automatic for a throw-away vault)")
    ap.add_argument("--bearer-from-env", metavar="NAME", nargs="?", const="VAULT_MCP_TOKEN",
                    help="HTTP only: send the access token held in this environment variable (default VAULT_MCP_TOKEN) as a Bearer credential")
    args = ap.parse_args()

    token = None
    if args.bearer_from_env:
        if not args.url:
            ap.error("--bearer-from-env applies to --url only")
        token = os.environ.get(args.bearer_from_env, "").strip()
        if not token:
            ap.error(f"environment variable {args.bearer_from_env} is empty; put the access token there first")

    temp = args.dll is not None and args.vault is None
    vault = make_temp_vault() if temp else args.vault
    private_note = args.private_note or ("Personal/Diary.md" if temp else None)
    transport = StdioTransport(args.dll, vault) if args.dll else HttpTransport(args.url, token)
    failures = 0

    def check(ok, label, detail=""):
        nonlocal failures
        print(("OK   " if ok else "FAIL ") + label + (f"  {detail}" if detail else ""))
        if not ok:
            failures += 1

    def call(name, arguments):
        result = transport.request("tools/call", {"name": name, "arguments": arguments})
        return json.loads(result["content"][0]["text"])

    try:
        info = transport.request("initialize", {"protocolVersion": PROTOCOL, "capabilities": {},
                                                "clientInfo": {"name": "vault-smoke", "version": "0"}})["serverInfo"]
        check(True, "initialize", f"{info.get('name')} {info.get('version')}")
        transport.notify("notifications/initialized")

        tools = transport.request("tools/list", {})["tools"]
        names = sorted(t["name"] for t in tools)
        check(names == EXPECTED_TOOLS, "tools/list is exactly the four read tools", str(names))
        read_only = all((t.get("annotations") or {}).get("readOnlyHint") is True for t in tools)
        check(read_only, "every tool carries readOnlyHint=true")

        search = call("vault_search", {"query": args.query, "take": 5})
        if "error" in search:
            check(False, "vault_search", str(search["error"]))
        else:
            check(True, "vault_search", f"total={search['total']} note={search.get('note')!r}")
            if temp:
                check([h["path"] for h in search["hits"]] == ["Work/Cosmos decision.md"], "search returns the allow-listed note only")

        if private_note:
            refused = call("vault_note_get", {"path": private_note})
            check(refused.get("error", {}).get("code") == "NOT_FOUND", f"{private_note} outside the allow-list is refused with NOT_FOUND")
        if temp:
            back = call("vault_backlinks", {"path": "Naming"})
            check(back.get("total") == 1, "vault_backlinks finds the linking note")
            tags = call("vault_tags", {})
            check(tags.get("total") == 1 and tags.get("subject") == "#adr-context", "vault_tags defaults to the context tag")
    finally:
        code = transport.close()
        if temp:
            shutil.rmtree(vault, ignore_errors=True)

    if args.dll:
        check(code == 0, "server exited cleanly", f"code {code}")
    print("\n" + ("ALL OK" if failures == 0 else f"{failures} check(s) failed"))
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
