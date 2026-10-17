#!/usr/bin/env bash
# Call one MCP tool on the local host with curl (Streamable HTTP). Usage: ./scripts/local-mcp-call.sh adr_list '{"take":5}'
set -euo pipefail
URL="${MCP_URL:-http://localhost:7071/runtime/webhooks/mcp}"
TOOL="$1"; ARGS="${2:-{\}}"
HDRS=(-H "Content-Type: application/json" -H "Accept: application/json, text/event-stream")
[[ -n "${MCP_KEY:-}" ]] && HDRS+=(-H "x-functions-key: ${MCP_KEY}")
INIT=$(curl -sS -D - "${HDRS[@]}" "$URL" -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"1"}}}')
SID=$(printf '%s' "$INIT" | grep -i '^mcp-session-id:' | awk '{print $2}' | tr -d '\r')
[[ -n "$SID" ]] && HDRS+=(-H "mcp-session-id: $SID")
curl -sS "${HDRS[@]}" "$URL" -d '{"jsonrpc":"2.0","method":"notifications/initialized"}' > /dev/null || true
curl -sS "${HDRS[@]}" "$URL" -d "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"$TOOL\",\"arguments\":$ARGS}}" | sed -n 's/^data: //p; /^{/p' | tail -1 | python3 -c 'import sys,json; m=json.load(sys.stdin); r=m.get("result",m); t=[c["text"] for c in r.get("content",[]) if c.get("type")=="text"]; print(json.dumps(json.loads(t[0]) if t else r, indent=2))'
