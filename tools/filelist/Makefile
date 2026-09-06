.PHONY: build build-linux build-windows build-all run clean help

BUILDDIR := build
SRC := src
LDFLAGS := -s -w

build: ## Build for current platform
	cd $(SRC) && go build -ldflags '$(LDFLAGS)' -o ../$(BUILDDIR)/filelist .

build-linux: ## Cross-compile for Linux amd64
	cd $(SRC) && GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -ldflags '$(LDFLAGS)' -o ../$(BUILDDIR)/filelist-linux-amd64 .

build-windows: ## Cross-compile for Windows amd64
	cd $(SRC) && GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -ldflags '$(LDFLAGS)' -o ../$(BUILDDIR)/filelist-windows-amd64.exe .

build-all: build-linux build-windows ## Cross-compile for all platforms

run: ## Run with default config
	cd $(SRC) && go run . -config ../config.yaml

clean: ## Clean build artifacts
	rm -rf $(BUILDDIR) *.idx

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?##' $(MAKEFILE_LIST) | awk 'BEGIN{FS=":.*?## "}{printf "  \033[36m%-16s\033[0m %s\n", $$1, $$2}'
