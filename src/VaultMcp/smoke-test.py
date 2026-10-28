#!/usr/bin/env python3
"""End-to-end check of the Obsidian vault MCP server over stdio (ADR-0010 gate 1).

Starts the built server against a vault, speaks MCP JSON-RPC to it (initialize, tools/list, tools/call) and checks
that exactly the four read-only tools are listed and that a search answers. No MCP client library is needed.

    python3 src/VaultMcp/smoke-test.py --dll src/VaultMcp/VaultMcp.Stdio/bin/Release/net10.0/VaultMcp.Stdio.dll --vault "<vault>"

Without --vault a throw-away vault with one allow-listed folder and one private folder is created, and the script
also checks that the private note is refused. Exit code 0 means every line printed OK.
"""
import argparse
import json
import os
import shutil
import subprocess  # nosec B404: the only process started is the .NET runtime, with arguments from this script's own command line
import sys
import tempfile

EXPECTED_TOOLS = ["vault_backlinks", "vault_note_get", "vault_search", "vault_tags"]


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


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dll", required=True, help="path to VaultMcp.Stdio.dll (build first)")
    ap.add_argument("--vault", help="vault folder; omitted = a throw-away vault with a private folder to refuse")
    ap.add_argument("--query", default="cosmos", help="search word for the vault_search check")
    args = ap.parse_args()

    dotnet = shutil.which("dotnet")
    if dotnet is None:
        print("FAIL the .NET runtime ('dotnet') is not on PATH; install the .NET 10 SDK first", file=sys.stderr)
        return 2
    if not os.path.isfile(args.dll):
        print(f"FAIL {args.dll} not found; build first: dotnet build src/VaultMcp/VaultMcp.sln -c Release", file=sys.stderr)
        return 2

    temp = args.vault is None
    vault = make_temp_vault() if temp else args.vault
    # No shell is involved; the argument list is fixed apart from the paths given on this script's own command line.
    proc = subprocess.Popen([dotnet, args.dll, "--vault", vault], stdin=subprocess.PIPE, stdout=subprocess.PIPE,  # nosec B603
                            stderr=subprocess.PIPE, text=True, encoding="utf-8")
    next_id = [0]

    def send(method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        if not notify:
            next_id[0] += 1
            msg["id"] = next_id[0]
        proc.stdin.write(json.dumps(msg) + "\n")
        proc.stdin.flush()

    def recv():
        while True:
            line = proc.stdout.readline()
            if not line:
                sys.stderr.write("server closed its output; its stderr was:\n" + proc.stderr.read())
                raise SystemExit(1)
            line = line.strip()
            if line:
                msg = json.loads(line)
                if "error" in msg:
                    raise SystemExit(f"FAIL protocol error: {msg['error']}")
                return msg["result"]

    def call(name, arguments):
        send("tools/call", {"name": name, "arguments": arguments})
        result = recv()
        return json.loads(result["content"][0]["text"])

    failures = 0

    def check(ok, label, detail=""):
        nonlocal failures
        print(("OK   " if ok else "FAIL ") + label + (f"  {detail}" if detail else ""))
        if not ok:
            failures += 1

    try:
        send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                            "clientInfo": {"name": "vault-smoke", "version": "0"}})
        info = recv()["serverInfo"]
        check(True, "initialize", f"{info.get('name')} {info.get('version')}")
        send("notifications/initialized", notify=True)

        send("tools/list", {})
        tools = recv()["tools"]
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

        if temp:
            refused = call("vault_note_get", {"path": "Personal/Diary.md"})
            check(refused.get("error", {}).get("code") == "NOT_FOUND", "a note outside the allow-list is refused with NOT_FOUND")
            back = call("vault_backlinks", {"path": "Naming"})
            check(back.get("total") == 1, "vault_backlinks finds the linking note")
            tags = call("vault_tags", {})
            check(tags.get("total") == 1 and tags.get("subject") == "#adr-context", "vault_tags defaults to the context tag")
    finally:
        proc.stdin.close()
        proc.wait(timeout=15)
        if temp:
            shutil.rmtree(vault, ignore_errors=True)

    check(proc.returncode == 0, "server exited cleanly", f"code {proc.returncode}")
    print("\n" + ("ALL OK" if failures == 0 else f"{failures} check(s) failed"))
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
