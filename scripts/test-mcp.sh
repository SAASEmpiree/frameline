#!/bin/bash
# test-mcp.sh — compiles and runs the MCP server tests (unit + loopback HTTP integration).
# Plain executables built with swiftc (Command Line Tools only, no XCTest), from the
# Foundation/Network-only files of Sources/FrameLine/MCP (the AppKit glue is left out).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/.build/mcp-tests"
mkdir -p "$OUT"
CORE=(
    "$ROOT/Sources/FrameLine/MCP/MCPJSONRPC.swift"
    "$ROOT/Sources/FrameLine/MCP/MCPHTTP.swift"
    "$ROOT/Sources/FrameLine/MCP/MCPServer.swift"
    "$ROOT/Sources/FrameLine/MCP/MCPDiscovery.swift"
    "$ROOT/Sources/FrameLine/MCP/FrameLineMCPCatalog.swift"
)
echo "▸ MCP unit tests"
swiftc -parse-as-library -O "${CORE[@]}" "$ROOT/tests/MCPCoreTests.swift" -o "$OUT/mcp-core-tests"
"$OUT/mcp-core-tests"
echo "▸ MCP integration test (real listener on 127.0.0.1)"
swiftc -parse-as-library -O "${CORE[@]}" "$ROOT/tests/MCPIntegrationTests.swift" -o "$OUT/mcp-integration-tests"
"$OUT/mcp-integration-tests"
echo "✓ MCP tests passed"
