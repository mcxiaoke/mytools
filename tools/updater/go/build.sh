#!/usr/bin/env bash
# 构建 updater.exe（无控制台、无调试符号）
set -euo pipefail
cd "$(dirname "$0")"

GOFLAGS=${GOFLAGS:-}
go vet ./...
# 注意：不使用 UPX，压缩壳会明显提高杀软误报率
go build -ldflags "-s -w -H=windowsgui" -o updater.exe .
ls -l updater.exe
