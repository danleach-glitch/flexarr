.PHONY: setup dev-api dev-web test test-api test-web test-apple build apple docker deploy logs

export DOTNET_CLI_TELEMETRY_OPTOUT := 1

setup: ## Install dependencies for every component
	dotnet restore
	cd frontend && npm ci

dev-api: ## Run the server on :8383 with hot reload
	dotnet watch --project src/Flexarr.Api

dev-web: ## Run the web client on :5173 (proxies /api to :8383)
	cd frontend && npm run dev

test: test-api test-web ## Run server and web tests

test-api:
	dotnet test

test-web:
	cd frontend && npm run lint && npm test

test-apple:
	cd clients/apple/FlexarrKit && swift test

build: ## Build server and web client
	dotnet build -c Release
	cd frontend && npm run build

apple: ## Generate the Xcode project and build the tvOS app
	cd clients/apple && xcodegen generate && \
	xcodebuild -project Flexarr.xcodeproj -scheme FlexarrTV -destination 'generic/platform=tvOS' CODE_SIGNING_ALLOWED=NO build

docker: ## Build the container image
	docker build -t flexarr:dev .

DEPLOY_HOST ?= mini

deploy: ## Build and install/upgrade Flexarr on DEPLOY_HOST (default: mini) over SSH
	scripts/deploy-macos.sh $(DEPLOY_HOST)

logs: ## Tail the server log on DEPLOY_HOST
	ssh $(DEPLOY_HOST) tail -f .flexarr/logs/flexarr.log
