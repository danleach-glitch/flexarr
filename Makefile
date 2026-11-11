.PHONY: tunnel tunnel-test tunnel-apple tunnel-android guide apps tokens strings languages apple-install ios-install ios-upload ios-dsyms tv-upload mac-upload mac-player-dmg android-bundle android-upload android-listing setup dev-api dev-web test test-api test-web test-apple build apple docker deploy logs

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

# The Tailscale tunnel the apps carry for family guests (clients/tunnel, Go): rebuilt when its sources change.
TUNNEL_SRC := $(shell find clients/tunnel -name '*.go' -not -path '*/spike/*') clients/tunnel/go.mod clients/tunnel/go.sum clients/tunnel/tags.sh
GO_PATH_ADD = PATH="$$PATH:/opt/homebrew/bin:/usr/local/go/bin"
clients/tunnel/build/apple.stamp: $(TUNNEL_SRC) clients/tunnel/build-apple.sh
	$(GO_PATH_ADD) clients/tunnel/build-apple.sh && touch $@
# Also writes the sources' hash the Android build compares (buildTunnel in clients/android/app/build.gradle.kts: the
# package's Go files but its tests, go.mod, go.sum, tags.sh and build-android.sh, each as name NUL content NUL, sorted
# by name), so Gradle doesn't build the AAR again.
clients/tunnel/build/android.stamp: $(TUNNEL_SRC) clients/tunnel/build-android.sh
	$(GO_PATH_ADD) clients/tunnel/build-android.sh
	cd clients/tunnel && for f in $$(ls | LC_ALL=C sort); do case "$$f" in *_test.go) ;; *.go|go.mod|go.sum|tags.sh|build-android.sh) \
	  [ -f "$$f" ] && { printf '%s\0' "$$f"; cat "$$f"; printf '\0'; } ;; esac; done | shasum -a 256 | cut -d' ' -f1 >build/flexarr-tunnel.aar.hash
	touch $@
tunnel-test: ## Test the apps' Tailscale tunnel
	cd clients/tunnel && $(GO_PATH_ADD) go test ./...
tunnel-apple: clients/tunnel/build/apple.stamp ## Build the tunnel for the Apple apps (xcframework)
tunnel-android: clients/tunnel/build/android.stamp ## Build the tunnel for the Android app (AAR)
tunnel: tunnel-apple tunnel-android ## Build the tunnel for every app

build: ## Build server and web client
	dotnet build -c Release
	cd frontend && npm run build

apple: tunnel-apple ## Generate the Xcode project and build the tvOS and iOS apps
	cd clients/apple && xcodegen generate && \
	xcodebuild -project Flexarr.xcodeproj -scheme FlexarrTV -destination 'generic/platform=tvOS' CODE_SIGNING_ALLOWED=NO build && \
	xcodebuild -project Flexarr.xcodeproj -scheme FlexarriOS -destination 'generic/platform=iOS' CODE_SIGNING_ALLOWED=NO build

# Install on a real Apple TV (paired in Xcode). A free Apple ID's installs stop opening after 7 days; run this again.
#   make apple-install TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.tv [DEVICE=<udid>]
# TEAM is in Xcode › Settings › Accounts; DEVICE defaults to the first paired Apple TV.
apple-install: tunnel-apple ## Build, sign and install the tvOS app on a paired Apple TV
	@test -n "$(TEAM)" -a -n "$(BUNDLE_ID)" || { echo "usage: make apple-install TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.tv [DEVICE=<udid>]"; exit 1; }
	cd clients/apple && xcodegen generate --quiet
	device="$(DEVICE)"; [ -n "$$device" ] || device=$$(xcrun devicectl list devices 2>/dev/null | awk '/AppleTV/ && !/simulated/ {for (i=1;i<=NF;i++) if ($$i ~ /^[0-9A-F]{8}-[0-9A-F]{16}$$/) {print $$i; exit}}'); \
	test -n "$$device" || { echo "No paired Apple TV found (Xcode › Window › Devices and Simulators, or ⇧⌘2)"; exit 1; }; \
	xcodebuild -project clients/apple/Flexarr.xcodeproj -scheme FlexarrTV -configuration Release -destination "id=$$device" \
		-derivedDataPath build/apple -allowProvisioningUpdates -allowProvisioningDeviceRegistration \
		DEVELOPMENT_TEAM=$(TEAM) FLEXARR_TV_BUNDLE_ID=$(BUNDLE_ID) CODE_SIGN_STYLE=Automatic $(APPLE_VERSION) build -quiet && \
	xcrun devicectl device install app --device "$$device" build/apple/Build/Products/Release-appletvos/FlexarrTV.app && \
	xcrun devicectl device process launch --device "$$device" $(BUNDLE_ID)

# Install on your own iPhone or iPad (connected by cable or paired over Wi-Fi in Xcode). Free Apple ID: 7 days, as above.
#   make ios-install TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.ios [DEVICE=<udid>]
# The app group the widgets share with the app needs Xcode signed in to the team's account. Once Apple grants CarPlay,
# pass IOS_ENTITLEMENTS=FlexarriOS/FlexarriOS-CarPlay.entitlements; IOS_ENTITLEMENTS= WIDGET_ENTITLEMENTS=
# NOTIFICATION_ENTITLEMENTS= builds without the app group or push notifications (widgets then show nothing, and
# notifications can't be turned on).
IOS_ENTITLEMENTS ?= FlexarriOS/FlexarriOS.entitlements
WIDGET_ENTITLEMENTS ?= Widgets/FlexarrWidgets.entitlements
NOTIFICATION_ENTITLEMENTS ?= NotificationService/NotificationService.entitlements

ios-install: tunnel-apple ## Build, sign and install the iOS app on a connected iPhone or iPad
	@test -n "$(TEAM)" -a -n "$(BUNDLE_ID)" || { echo "usage: make ios-install TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.ios [DEVICE=<udid>]"; exit 1; }
	cd clients/apple && xcodegen generate --quiet
	device="$(DEVICE)"; [ -n "$$device" ] || device=$$(xcrun devicectl list devices 2>/dev/null | awk '/iPhone|iPad/ && !/simulated/ {for (i=1;i<=NF;i++) if ($$i ~ /^[0-9A-F]{8}-[0-9A-F]{16}$$/ || $$i ~ /^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$$/) {print $$i; exit}}'); \
	test -n "$$device" || { echo "No iPhone or iPad found (connect it, or pair it in Xcode › Window › Devices and Simulators)"; exit 1; }; \
	xcodebuild -project clients/apple/Flexarr.xcodeproj -scheme FlexarriOS -configuration Release -destination "id=$$device" \
		-derivedDataPath build/apple -allowProvisioningUpdates -allowProvisioningDeviceRegistration \
		DEVELOPMENT_TEAM=$(TEAM) FLEXARR_IOS_BUNDLE_ID=$(BUNDLE_ID) FLEXARR_IOS_ENTITLEMENTS=$(IOS_ENTITLEMENTS) FLEXARR_WIDGET_ENTITLEMENTS=$(WIDGET_ENTITLEMENTS) FLEXARR_NOTIFICATION_ENTITLEMENTS=$(NOTIFICATION_ENTITLEMENTS) CODE_SIGN_STYLE=Automatic $(APPLE_VERSION) build -quiet && \
	xcrun devicectl device install app --device "$$device" build/apple/Build/Products/Release-iphoneos/FlexarriOS.app && \
	xcrun devicectl device process launch --device "$$device" $(BUNDLE_ID)

# Archive the iOS app and upload it to App Store Connect (paid developer account, signed in to Xcode).
#   make ios-upload TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.ios
# Each upload needs a higher build number, so it defaults to the current time.
BUILD ?= $(shell date +%Y%m%d%H%M)
# MARKETING=0.51.0 builds the Apple apps as that version without editing project.yml. App Store Connect refuses a
# TestFlight upload of a version that's already on the App Store, so builds from main between releases pass the next one.
APPLE_VERSION = $(if $(MARKETING),MARKETING_VERSION=$(MARKETING))
# Sign in to App Store Connect with an API key instead of Xcode's account: ASC_KEY_ID, ASC_ISSUER_ID and ASC_KEY_PATH (the .p8).
ASC_AUTH = $(if $(ASC_KEY_ID),-authenticationKeyID $(ASC_KEY_ID) -authenticationKeyIssuerID $(ASC_ISSUER_ID) -authenticationKeyPath $(ASC_KEY_PATH))

ios-upload: tunnel-apple ## Archive the iOS app and upload it to App Store Connect
	@test -n "$(TEAM)" -a -n "$(BUNDLE_ID)" || { echo "usage: make ios-upload TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.ios"; exit 1; }
	cd clients/apple && xcodegen generate --quiet
	xcodebuild -project clients/apple/Flexarr.xcodeproj -scheme FlexarriOS -configuration Release -destination 'generic/platform=iOS' \
		-archivePath build/apple/FlexarriOS.xcarchive -allowProvisioningUpdates \
		DEVELOPMENT_TEAM=$(TEAM) FLEXARR_IOS_BUNDLE_ID=$(BUNDLE_ID) FLEXARR_IOS_ENTITLEMENTS=$(IOS_ENTITLEMENTS) FLEXARR_WIDGET_ENTITLEMENTS=$(WIDGET_ENTITLEMENTS) FLEXARR_NOTIFICATION_ENTITLEMENTS=$(NOTIFICATION_ENTITLEMENTS) CODE_SIGN_STYLE=Automatic $(APPLE_VERSION) CURRENT_PROJECT_VERSION=$(BUILD) archive -quiet
	printf '%s\n' '<?xml version="1.0" encoding="UTF-8"?>' '<plist version="1.0"><dict>' \
		'<key>method</key><string>app-store-connect</string><key>destination</key><string>upload</string>' \
		'<key>teamID</key><string>$(TEAM)</string><key>signingStyle</key><string>automatic</string>' \
		'</dict></plist>' > build/apple/ExportOptions.plist
	xcodebuild -exportArchive -archivePath build/apple/FlexarriOS.xcarchive -exportOptionsPlist build/apple/ExportOptions.plist \
		-exportPath build/apple/export -allowProvisioningUpdates $(ASC_AUTH)
	@$(MAKE) --no-print-directory ios-dsyms

# Debug symbols to Sentry, so crash and hang reports from that build show function names. Needs an organization token
# for dan-leach, in SENTRY_AUTH_TOKEN or ~/.sentryclirc; without one it's skipped, so forks can still upload.
SENTRY_CLI = npx --yes @sentry/cli@3.8.2
DSYM_ARCHIVE ?= build/apple/FlexarriOS.xcarchive
ios-dsyms: ## Upload the last iOS archive's debug symbols to Sentry
	@if [ -z "$$SENTRY_AUTH_TOKEN" ] && ! grep -qs '^token=' ~/.sentryclirc; then echo "No Sentry token (SENTRY_AUTH_TOKEN or ~/.sentryclirc): skipping the dSYM upload to Sentry"; exit 0; fi; \
	$(SENTRY_CLI) debug-files upload --org dan-leach --project flexarr-apple --include-sources $(DSYM_ARCHIVE)/dSYMs

guide: ## Build flexarr.io/guide (site/guide) from docs/guide
	cd scripts/guide && npm ci --silent && node build.mjs

tokens: ## Write design/tokens.json and the score marks out as the web's CSS and TS, Swift and Kotlin
	python3 scripts/design/tokens.py

strings: ## Collect every looked-up string into languages/packs/en.json, check the packs, write what's built from them
	python3 scripts/i18n/strings.py

languages: strings ## Build flexarr.io/languages (site/languages) from languages/ (part of make strings)

apps: ## Build flexarr.io/apps (site/apps.html) and the README's apps table from site/apps.json
	node scripts/apps/build.mjs

# Archive the tvOS app and upload it to App Store Connect. Pass the iOS app's bundle ID to ship both on one
# listing (universal purchase); the Top Shelf extension becomes <bundle id>.topshelf.
#   make tv-upload TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.ios
tv-upload: tunnel-apple ## Archive the tvOS app and upload it to App Store Connect
	@test -n "$(TEAM)" -a -n "$(BUNDLE_ID)" || { echo "usage: make tv-upload TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.ios"; exit 1; }
	cd clients/apple && xcodegen generate --quiet
	xcodebuild -project clients/apple/Flexarr.xcodeproj -scheme FlexarrTV -configuration Release -destination 'generic/platform=tvOS' \
		-archivePath build/apple/FlexarrTV.xcarchive -allowProvisioningUpdates \
		DEVELOPMENT_TEAM=$(TEAM) FLEXARR_TV_BUNDLE_ID=$(BUNDLE_ID) CODE_SIGN_STYLE=Automatic $(APPLE_VERSION) CURRENT_PROJECT_VERSION=$(BUILD) archive -quiet
	printf '%s\n' '<?xml version="1.0" encoding="UTF-8"?>' '<plist version="1.0"><dict>' \
		'<key>method</key><string>app-store-connect</string><key>destination</key><string>upload</string>' \
		'<key>teamID</key><string>$(TEAM)</string><key>signingStyle</key><string>automatic</string>' \
		'</dict></plist>' > build/apple/ExportOptions.plist
	xcodebuild -exportArchive -archivePath build/apple/FlexarrTV.xcarchive -exportOptionsPlist build/apple/ExportOptions.plist \
		-exportPath build/apple/export-tv -allowProvisioningUpdates $(ASC_AUTH)

# Archive the Mac server app and upload it to App Store Connect. It has its own App Store record, not the iOS app's.
#   make mac-upload TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.mac
# MAC_EXPORT=export builds the signed installer package into clients/macos/build/export instead of uploading it.
MAC_EXPORT ?= upload
mac-upload: ## Archive the Mac app and upload it to App Store Connect
	@test -n "$(TEAM)" -a -n "$(BUNDLE_ID)" || { echo "usage: make mac-upload TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.mac"; exit 1; }
	clients/macos/Tools/build-ffmpeg.sh
	clients/macos/Tools/publish-server.sh
	cd clients/macos && xcodegen generate --quiet
	xcodebuild -project clients/macos/FlexarrServer.xcodeproj -scheme FlexarrServer -configuration Release -destination 'generic/platform=macOS' \
		-archivePath clients/macos/build/FlexarrServer.xcarchive -allowProvisioningUpdates \
		DEVELOPMENT_TEAM=$(TEAM) FLEXARR_MAC_BUNDLE_ID=$(BUNDLE_ID) CODE_SIGN_STYLE=Automatic CODE_SIGN_IDENTITY="Apple Development" \
		$(APPLE_VERSION) CURRENT_PROJECT_VERSION=$(BUILD) archive -quiet
	printf '%s\n' '<?xml version="1.0" encoding="UTF-8"?>' '<plist version="1.0"><dict>' \
		'<key>method</key><string>app-store-connect</string><key>destination</key><string>$(MAC_EXPORT)</string>' \
		'<key>teamID</key><string>$(TEAM)</string><key>signingStyle</key><string>automatic</string>' \
		'</dict></plist>' > clients/macos/build/ExportOptions.plist
	xcodebuild -exportArchive -archivePath clients/macos/build/FlexarrServer.xcarchive -exportOptionsPlist clients/macos/build/ExportOptions.plist \
		-exportPath clients/macos/build/export -allowProvisioningUpdates $(ASC_AUTH)

# The Mac app (the player, clients/apple's FlexarrMac; not Flexarr Server). Install builds it signed with your Apple
# Development certificate (no account in Xcode needed) and puts it in MAC_PLAYER_DIR, without notifications.
#   make mac-player-install
MAC_PLAYER_DIR ?= /Applications
mac-player-install: tunnel-apple ## Build the Mac app (the player) and install it in /Applications
	cd clients/apple && xcodegen generate --quiet
	xcodebuild -project clients/apple/Flexarr.xcodeproj -scheme FlexarrMac -configuration Release -destination 'generic/platform=macOS' \
		-derivedDataPath build/apple-mac CODE_SIGNING_ALLOWED=NO $(APPLE_VERSION) build -quiet
	app=build/apple-mac/Build/Products/Release/Flexarr.app; \
	codesign --force --options runtime -s "Apple Development" --entitlements clients/apple/FlexarrMacNotificationService/FlexarrMacNotificationService.entitlements "$$app/Contents/PlugIns/FlexarrMacNotificationService.appex" && \
	codesign --force --options runtime -s "Apple Development" --entitlements clients/apple/FlexarrMac/FlexarrMac.entitlements "$$app" && \
	rm -rf "$(MAC_PLAYER_DIR)/Flexarr.app" && cp -R "$$app" "$(MAC_PLAYER_DIR)/" && open "$(MAC_PLAYER_DIR)/Flexarr.app"

# The Mac app as Flexarr.dmg for downloading from flexarr.io: Developer ID, notarised (scripts/mac-player-dmg.sh, which
# says what it needs). scripts/release.sh publish makes it for each release.
#   make mac-player-dmg [VERSION=x.y.z]
mac-player-dmg: ## Build the Mac app (the player) as a signed, notarised Flexarr.dmg in build/apple
	scripts/mac-player-dmg.sh $(VERSION)

# Archive the Mac app and upload it to App Store Connect. Pass the iOS app's bundle ID to ship it on the iPhone and Apple
# TV listing (universal purchase, docs/mac-player.md); the notification extension becomes <bundle id>.NotificationService.
# It's signed with notifications and the app group (the -Store entitlements). MAC_EXPORT=export builds the package only.
#   make mac-player-upload TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.ios
mac-player-upload: tunnel-apple ## Archive the Mac app (the player) and upload it to App Store Connect
	@test -n "$(TEAM)" -a -n "$(BUNDLE_ID)" || { echo "usage: make mac-player-upload TEAM=<team id> BUNDLE_ID=com.yourname.flexarr.ios"; exit 1; }
	cd clients/apple && xcodegen generate --quiet
	xcodebuild -project clients/apple/Flexarr.xcodeproj -scheme FlexarrMac -configuration Release -destination 'generic/platform=macOS' \
		-archivePath build/apple/FlexarrMac.xcarchive -allowProvisioningUpdates \
		DEVELOPMENT_TEAM=$(TEAM) FLEXARR_MAC_PLAYER_BUNDLE_ID=$(BUNDLE_ID) CODE_SIGN_STYLE=Automatic CODE_SIGN_IDENTITY="Apple Development" \
		FLEXARR_MAC_ENTITLEMENTS=FlexarrMac/FlexarrMac-Store.entitlements \
		FLEXARR_MAC_NOTIFICATION_ENTITLEMENTS=FlexarrMacNotificationService/FlexarrMacNotificationService-Store.entitlements \
		$(APPLE_VERSION) CURRENT_PROJECT_VERSION=$(BUILD) archive -quiet
	@# The Swift packages' resource bundles (FlexarrKit's, AetherEngine's) come out of the archive signed with the
	@# development certificate, and the export doesn't re-sign bundles under Resources, so App Store Connect turns the
	@# build away (ITMS-90284). They hold no code: unsigned, they're sealed as the app's resources when it's signed.
	find build/apple/FlexarrMac.xcarchive/Products/Applications/Flexarr.app -type d -name '*.bundle' -path '*/Contents/Resources/*' \
		-exec codesign --remove-signature {} \;
	printf '%s\n' '<?xml version="1.0" encoding="UTF-8"?>' '<plist version="1.0"><dict>' \
		'<key>method</key><string>app-store-connect</string><key>destination</key><string>$(MAC_EXPORT)</string>' \
		'<key>teamID</key><string>$(TEAM)</string><key>signingStyle</key><string>automatic</string>' \
		'</dict></plist>' > build/apple/ExportOptions-mac.plist
	xcodebuild -exportArchive -archivePath build/apple/FlexarrMac.xcarchive -exportOptionsPlist build/apple/ExportOptions-mac.plist \
		-exportPath build/apple/export-mac -allowProvisioningUpdates $(ASC_AUTH)
	@$(MAKE) --no-print-directory ios-dsyms DSYM_ARCHIVE=build/apple/FlexarrMac.xcarchive

# The Android app for Google Play, signed with the upload key in ~/.flexarr-release (see docs/play-store.md).
#   make android-bundle    the signed app bundle, in clients/android/app/build/outputs/bundle/release
#   make android-upload    upload it to a Play testing track (TRACK=internal by default; alpha is the closed test)
#   make android-listing   publish the store listing in clients/android/app/src/main/play
# MARKETING=0.53.0 builds as that version, as for the Apple apps.
TRACK ?= internal
ANDROID_JAVA ?= /Applications/Android Studio.app/Contents/jbr/Contents/Home
ANDROID_GRADLE = cd clients/android && JAVA_HOME="$${JAVA_HOME:-$(ANDROID_JAVA)}" ./gradlew $(if $(MARKETING),-Pflexarr.version=$(MARKETING))
android-bundle: tunnel-android ## Build the signed Android app bundle for Google Play
	$(ANDROID_GRADLE) :app:bundleRelease
android-upload: tunnel-android ## Upload the Android app to a Google Play testing track
	$(ANDROID_GRADLE) -Pflexarr.track=$(TRACK) :app:publishReleaseBundle
# The listing's contact email is public on Play but kept out of this repo: it's copied in from ~/.flexarr-release.
PLAY_CONTACT_EMAIL ?= $(HOME)/.flexarr-release/play-contact-email.txt
android-listing: ## Publish the Android app's Google Play listing
	@test -f "$(PLAY_CONTACT_EMAIL)" || { echo "Missing $(PLAY_CONTACT_EMAIL) (the listing's contact email; docs/play-store.md)" >&2; exit 1; }
	cp "$(PLAY_CONTACT_EMAIL)" clients/android/app/src/main/play/contact-email.txt
	$(ANDROID_GRADLE) :app:publishReleaseListing

docker: ## Build the container image
	docker build -t flexarr:dev .

DEPLOY_HOST ?= mini

deploy: ## Build and install/upgrade Flexarr on DEPLOY_HOST (default: mini) over SSH
	scripts/deploy-macos.sh $(DEPLOY_HOST)

logs: ## Tail the server log on DEPLOY_HOST (today's file; LEVEL=Warning to show only warnings and errors)
	ssh $(DEPLOY_HOST) 'tail -n 100 -F "$$(ls -t .flexarr/logs/flexarr-*.log | head -1)"' | python3 scripts/pretty-log.py $(LEVEL)
