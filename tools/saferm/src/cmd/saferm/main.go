// Command saferm 是"移动而非删除"的安全 rm：目标被移动到同卷的回收目录，
// 而不是永久删除，也不进系统回收站。
//
// 注意：本程序的设计不变量是"代码里不存在任何永久删除调用"，
// 详见 docs/saferm-design.md §10.1。
package main

import (
	"fmt"
	"os"
)

// version 由构建脚本通过 -ldflags 注入；本地开发时保持 dev。
var version = "dev"

func main() {
	// 参数解析、子命令与退出码在后续步骤补齐（设计文档 §12 第 5 步）。
	// 目前只保证骨架能在 Windows 与 Linux 上交叉编译通过。
	if len(os.Args) > 1 && (os.Args[1] == "-V" || os.Args[1] == "--version") {
		fmt.Printf("saferm %s\n", version)
		return
	}
	fmt.Fprintf(os.Stderr, "saferm %s: not implemented yet\n", version)
	os.Exit(1)
}
