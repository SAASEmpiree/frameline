#!/bin/bash
# mcp-smoke.sh — checks the MCP server of the RUNNING FrameLine through its discovery file:
# 401 without token, 403 with a foreign Host, then initialize + tools/list + list_recent_captures.
# Read-only: takes no screenshot, starts no recording. Never prints the token.
set -euo pipefail
FILE="$HOME/Library/Application Support/FrameLine/mcp.json"
[ -f "$FILE" ] || { echo "✗ $FILE not found: is FrameLine running with the MCP server enabled?"; exit 1; }
URL="$(plutil -extract mcpServers.frameline.url raw -o - "$FILE")"
AUTH="$(plutil -extract mcpServers.frameline.headers.Authorization raw -o - "$FILE")"
PORT="${URL##*:}"; PORT="${PORT%%/*}"
echo "▸ server: $URL (pid $(plutil -extract app.pid raw -o - "$FILE"))"
INIT='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"mcp-smoke","version":"1"}}}'
code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
c=$(code -X POST "$URL" -H 'Content-Type: application/json' -d "$INIT"); [ "$c" = 401 ] && echo "✓ 401 without token" || { echo "✗ expected 401, got $c"; exit 1; }
c=$(code -X POST "$URL" -H "Host: evil.example:$PORT" -H "Authorization: $AUTH" -H 'Content-Type: application/json' -d "$INIT")
[ "$c" = 403 ] && echo "✓ 403 with a foreign Host" || { echo "✗ expected 403, got $c"; exit 1; }
HDRS="$(mktemp)"; trap 'rm -f "$HDRS"' EXIT
curl -s -D "$HDRS" -o /dev/null -X POST "$URL" -H "Authorization: $AUTH" -H 'Content-Type: application/json' \
     -H 'Accept: application/json, text/event-stream' -d "$INIT"
SID="$(grep -i '^mcp-session-id:' "$HDRS" | tr -d '\r' | awk '{print $2}')"
[ -n "$SID" ] && echo "✓ initialize (session opened)" || { echo "✗ no Mcp-Session-Id"; exit 1; }
post() { curl -s -X POST "$URL" -H "Authorization: $AUTH" -H "Mcp-Session-Id: $SID" -H 'MCP-Protocol-Version: 2025-06-18' \
              -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -d "$1"; }
post '{"jsonrpc":"2.0","method":"notifications/initialized"}' >/dev/null
echo "▸ tools/list:"; post '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | plutil -convert json -r -o - -- - | grep '"name"' | sed 's/^ */  /'
echo "▸ list_recent_captures (limit 3):"
post '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_recent_captures","arguments":{"limit":3}}}' \
    | plutil -extract result.structuredContent json -r -o - -- -
curl -s -o /dev/null -X DELETE "$URL" -H "Authorization: $AUTH" -H "Mcp-Session-Id: $SID"
echo "✓ smoke test passed"
