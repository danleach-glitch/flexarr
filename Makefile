.PHONY: apple-install ios-install setup dev-api dev-web test test-api test-web test-apple build apple docker deploy logs

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

apple: ## Generate the Xcode project and build the tvOS and iOS apps
	cd clients/apple && xcodegen generate && \
	xcodebuild -project Flexarr.xcodeproj -scheme FlexarrTV -destination 'generic/platform=tvOS' CODE_SIGNING_ALLOWED=NO build && \
	xcodebuild -project Flexarr.xcodeproj -scheme FlexarriOS -destination 'generic/platform=iOS' CODE_SIGNING_ALLOWED=NO build

# Install on a real Apple TV (paired in Xcode). A free Apple ID's installs stop opening after 7 days; run this again.
#   make apple-install TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.tv [DEVICE=<udid>]
# TEAM is in Xcode › Settings › Accounts; DEVICE defaults to the first paired Apple TV.
apple-install: ## Build, sign and install the tvOS app on a paired Apple TV
	@test -n "$(TEAM)" -a -n "$(BUNDLE_ID)" || { echo "usage: make apple-install TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.tv [DEVICE=<udid>]"; exit 1; }
	cd clients/apple && xcodegen generate --quiet
	device="$(DEVICE)"; [ -n "$$device" ] || device=$$(xcrun devicectl list devices 2>/dev/null | awk '/AppleTV/ && !/simulated/ {for (i=1;i<=NF;i++) if ($$i ~ /^[0-9A-F]{8}-[0-9A-F]{16}$$/) {print $$i; exit}}'); \
	test -n "$$device" || { echo "No paired Apple TV found (Xcode › Window › Devices and Simulators, or ⇧⌘2)"; exit 1; }; \
	xcodebuild -project clients/apple/Flexarr.xcodeproj -scheme FlexarrTV -configuration Release -destination "id=$$device" \
		-derivedDataPath build/apple -allowProvisioningUpdates -allowProvisioningDeviceRegistration \
		DEVELOPMENT_TEAM=$(TEAM) PRODUCT_BUNDLE_IDENTIFIER=$(BUNDLE_ID) CODE_SIGN_STYLE=Automatic build -quiet && \
	xcrun devicectl device install app --device "$$device" build/apple/Build/Products/Release-appletvos/FlexarrTV.app && \
	xcrun devicectl device process launch --device "$$device" $(BUNDLE_ID)

# Install on your own iPhone or iPad (connected by cable or paired over Wi-Fi in Xcode). Free Apple ID: 7 days, as above.
#   make ios-install TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.ios [DEVICE=<udid>]
ios-install: ## Build, sign and install the iOS app on a connected iPhone or iPad
	@test -n "$(TEAM)" -a -n "$(BUNDLE_ID)" || { echo "usage: make ios-install TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.ios [DEVICE=<udid>]"; exit 1; }
	cd clients/apple && xcodegen generate --quiet
	device="$(DEVICE)"; [ -n "$$device" ] || device=$$(xcrun devicectl list devices 2>/dev/null | awk '/iPhone|iPad/ && !/simulated/ {for (i=1;i<=NF;i++) if ($$i ~ /^[0-9A-F]{8}-[0-9A-F]{16}$$/ || $$i ~ /^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$$/) {print $$i; exit}}'); \
	test -n "$$device" || { echo "No iPhone or iPad found (connect it, or pair it in Xcode › Window › Devices and Simulators)"; exit 1; }; \
	xcodebuild -project clients/apple/Flexarr.xcodeproj -scheme FlexarriOS -configuration Release -destination "id=$$device" \
		-derivedDataPath build/apple -allowProvisioningUpdates -allowProvisioningDeviceRegistration \
		DEVELOPMENT_TEAM=$(TEAM) PRODUCT_BUNDLE_IDENTIFIER=$(BUNDLE_ID) CODE_SIGN_STYLE=Automatic build -quiet && \
	xcrun devicectl device install app --device "$$device" build/apple/Build/Products/Release-iphoneos/FlexarriOS.app && \
	xcrun devicectl device process launch --device "$$device" $(BUNDLE_ID)

docker: ## Build the container image
	docker build -t flexarr:dev .

DEPLOY_HOST ?= mini

deploy: ## Build and install/upgrade Flexarr on DEPLOY_HOST (default: mini) over SSH
	scripts/deploy-macos.sh $(DEPLOY_HOST)

logs: ## Tail the server log on DEPLOY_HOST
	ssh $(DEPLOY_HOST) tail -f .flexarr/logs/flexarr.log
