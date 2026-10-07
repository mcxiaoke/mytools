// Command saferm 是"移动而非删除"的安全 rm：目标被移动到同卷的回收目录，
// 而不是永久删除，也不进系统回收站。
//
// 本程序的设计不变量：代码里不存在任何永久删除调用（见 docs/saferm-design.md §10.1）。
package main

import (
	"os"

	i18n "saferm/internal/i18n"
)

// version 由构建脚本通过 -ldflags 注入；本地开发时保持 dev。
var version = "dev"

func main() {
	i18n.Init()
	a := &app{stdin: os.Stdin, stdout: os.Stdout, stderr: os.Stderr, args: os.Args[1:]}
	os.Exit(a.run())
}
