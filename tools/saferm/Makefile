# saferm 构建脚本（Linux/macOS；Windows 用 build.ps1）
#
# 产物命名与同仓库的 filelist 保持一致，统一放在 build/ 下。
# 交叉编译一律 CGO_ENABLED=0，这是"单文件、零运行时依赖"的前提。

.PHONY: build build-linux build-linux-arm64 build-windows build-windows-arm64 \
        build-all test vet fmt invariants check fuzz clean distclean help

BUILDDIR := build
SRC      := src
MAINPKG  := ./cmd/saferm
VERSION  ?= 1.0.0-dev
LDFLAGS  := -s -w -X main.version=$(VERSION)
GOFLAGS_BUILD := -trimpath -buildvcs=false

build: ## 构建当前平台
	cd $(SRC) && CGO_ENABLED=0 go build $(GOFLAGS_BUILD) -ldflags '$(LDFLAGS)' -o ../$(BUILDDIR)/saferm $(MAINPKG)

build-linux: ## 交叉编译 Linux amd64
	cd $(SRC) && GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build $(GOFLAGS_BUILD) -ldflags '$(LDFLAGS)' -o ../$(BUILDDIR)/saferm-linux-amd64 $(MAINPKG)

build-linux-arm64: ## 交叉编译 Linux arm64
	cd $(SRC) && GOOS=linux GOARCH=arm64 CGO_ENABLED=0 go build $(GOFLAGS_BUILD) -ldflags '$(LDFLAGS)' -o ../$(BUILDDIR)/saferm-linux-arm64 $(MAINPKG)

build-windows: ## 交叉编译 Windows amd64
	cd $(SRC) && GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build $(GOFLAGS_BUILD) -ldflags '$(LDFLAGS)' -o ../$(BUILDDIR)/saferm-windows-amd64.exe $(MAINPKG)

build-windows-arm64: ## 交叉编译 Windows arm64
	cd $(SRC) && GOOS=windows GOARCH=arm64 CGO_ENABLED=0 go build $(GOFLAGS_BUILD) -ldflags '$(LDFLAGS)' -o ../$(BUILDDIR)/saferm-windows-arm64.exe $(MAINPKG)

build-all: build-windows build-windows-arm64 build-linux build-linux-arm64 ## 交叉编译全部四个目标

test: ## 跑单元测试
	cd $(SRC) && go test ./...

vet: ## go vet
	cd $(SRC) && go vet ./...

invariants: ## 源码不变量扫描：代码里不存在任何永久删除调用
	cd $(SRC) && go test -count=1 -run 'TestNoPermanentDeleteCalls|TestScannerDetectsAndIgnores' -v ./internal/invariants/

fmt: ## gofmt 检查（有输出即为不合格）
	cd $(SRC) && gofmt -l .

fuzz: ## 短时跑一轮 fuzz（可 FUZZTIME=30s 覆盖）
	cd $(SRC) && go test -run '^$$' -fuzz FuzzParse -fuzztime $(or $(FUZZTIME),15s) ./internal/config/

check: fmt vet invariants test ## 质量门：gofmt + vet + 不变量扫描 + test
	@echo "quality gate: OK"

clean: ## 清理构建产物
	rm -rf $(BUILDDIR)

distclean: clean ## 清理构建产物与本机 fuzz 缓存
	go clean -fuzzcache -cache

help: ## 显示帮助
	@grep -E '^[a-zA-Z_0-9-]+:.*?##' $(MAKEFILE_LIST) | awk 'BEGIN{FS=":.*?## "}{printf "  \033[36m%-22s\033[0m %s\n", $$1, $$2}'
