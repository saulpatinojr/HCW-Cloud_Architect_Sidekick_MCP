#!/usr/bin/env python3
"""
MCP smoke test for the ADR Intelligence server (Streamable HTTP transport, JSON-RPC 2.0).

Usage:
  python3 scripts/smoke-test.py --url http://localhost:7071/runtime/webhooks/mcp
  ADR_MCP_KEY="$MCP_EXTENSION_KEY" python3 scripts/smoke-test.py --url https://<app>.azurewebsites.net/runtime/webhooks/mcp
  python3 scripts/smoke-test.py --url ... --bearer "$ACCESS_TOKEN"          # built-in Entra auth

Exit code 0 when initialize, tools/list and a read/write round-trip succeed. Never prints the key or token.
Standard library only (no pip install); safe for CI.
"""
import argparse, json, os, sys, urllib.request, urllib.error

def post(url, headers, payload, timeout=60):
    data = json.dumps(payload).encode()
    req = urllib.request.Request(url, data=data, method="POST", headers={**headers, "Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            body = resp.read().decode("utf-8", "replace")
            return resp.status, dict(resp.headers), body
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read().decode("utf-8", "replace")

def parse_jsonrpc(body):
    """Accept either a plain JSON response or an SSE stream containing JSON-RPC messages."""
    body = body.strip()
    if not body:
        return None
    if body.startswith("{"):
        return json.loads(body)
    last = None
    for line in body.splitlines():
        if line.startswith("data:"):
            chunk = line[5:].strip()
            if chunk:
                try:
                    msg = json.loads(chunk)
                    if "result" in msg or "error" in msg:
                        last = msg
                except json.JSONDecodeError:
                    pass
    return last

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", required=True)
    ap.add_argument("--key", default=os.environ.get("ADR_MCP_KEY"), help="mcp_extension system key (x-functions-key); defaults to $ADR_MCP_KEY so it stays off the command line")
    ap.add_argument("--bearer", help="Entra access token for built-in auth")
    ap.add_argument("--write", action="store_true", help="also create a smoke-test ADR (mutating)")
    a = ap.parse_args()

    headers = {}
    if a.key: headers["x-functions-key"] = a.key
    if a.bearer: headers["Authorization"] = f"Bearer {a.bearer}"

    rid = 0
    def call(method, params=None):
        nonlocal rid, headers
        rid += 1
        status, h, body = post(a.url, headers, {"jsonrpc": "2.0", "id": rid, "method": method, "params": params or {}})
        sid = {k.lower(): v for k, v in h.items()}.get("mcp-session-id")
        if sid: headers["mcp-session-id"] = sid
        msg = parse_jsonrpc(body)
        if status >= 400 or msg is None or "error" in msg:
            print(f"FAIL {method}: HTTP {status} {json.dumps(msg)[:400] if msg else body[:400]}")
            sys.exit(1)
        return msg["result"]

    init = call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "adr-smoke", "version": "1.0"}})
    print(f"OK initialize: server={init.get('serverInfo', {}).get('name')} protocol={init.get('protocolVersion')}")
    # notifications/initialized is a notification (no id); best effort.
    post(a.url, headers, {"jsonrpc": "2.0", "method": "notifications/initialized"})

    tools = call("tools/list")["tools"]
    names = sorted(t["name"] for t in tools)
    print(f"OK tools/list: {len(names)} tools")
    required = ["adr_create", "adr_get", "adr_list", "adr_search", "adr_propose_change", "adr_approve_change", "file_upload_request", "file_register_upload", "evidence_attach", "technology_detect", "technology_refresh", "adr_check_contradictions", "adr_safety_review", "adr_detect_drift", "analysis_job_get"]
    missing = [r for r in required if r not in names]
    if missing:
        print(f"FAIL missing MVP tools: {missing}"); sys.exit(1)
    for t in tools:
        if not t.get("description") or not t.get("inputSchema", {}).get("properties"):
            print(f"FAIL tool {t['name']} has no description or schema"); sys.exit(1)
    print("OK every tool advertises a description and input schema")

    def tool(name, args):
        res = call("tools/call", {"name": name, "arguments": args})
        text = next((c["text"] for c in res.get("content", []) if c.get("type") == "text"), None)
        env = json.loads(text) if text else res.get("structuredContent")
        if not env or not env.get("ok"):
            print(f"FAIL {name}: {json.dumps(env)[:500]}"); sys.exit(1)
        return env["data"]

    data = tool("adr_list", {"take": 1})
    print(f"OK adr_list: {len(data['items'])} item(s) returned")
    data = tool("technology_detect", {"files": [{"path": "smoke/package.json", "content": json.dumps({"dependencies": {"express": "^4.19.2"}})}]})
    print(f"OK technology_detect: {[t['name'] for t in data['technologies']]}")
    data = tool("adr_safety_review", {"inline": {"title": "Smoke", "decision": "Use basic auth over http://example.internal", "rationale": "smoke", "persist": False}})
    print(f"OK adr_safety_review: {len(data['findings'])} finding(s)")
    if a.write:
        created = tool("adr_create", {"fields": {"title": "Smoke test ADR", "decision": "Smoke test decision. We must not use Newtonsoft.Json.", "status": "Draft", "tags": ["smoke"]}, "idempotencyKey": "smoke-test-adr"})
        adr_id = created["adr"]["summary"]["id"]
        got = tool("adr_get", {"adrId": adr_id})
        assert got["adr"]["summary"]["id"] == adr_id
        drift = tool("adr_detect_drift", {"adrId": adr_id})
        print(f"OK write round-trip: {adr_id} drift={drift['overallStatus']}")
    print("SMOKE TEST PASSED")

if __name__ == "__main__":
    main()
