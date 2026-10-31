#!/bin/sh
# Prints the Go build tags for release builds of the tunnel: tsnet features it never
# uses are left out for size, and logtail (log upload) isn't compiled in at all.
omit="logtail ssh taildrop tap capture drive webclient clientupdate kube aws bird tpm
relayserver wakeonlan debugportmapper portlist completion appconnectors c2n posture doctor
oauthkey identityfederation advertiseexitnode advertiseroutes tailnetlock netlog
linuxdnsfight networkmanager resolved dbus systray sdnotify synology iptables cli qrcodes
webbrowser flashappliance conn25 cloud syspolicy peerapiserver peerapiclient usermetrics
runtimemetrics clientmetrics useexitnode useroutes debug debugeventbus captiveportal ace
routecheck linkspeed tundevstats favorites serviceclientprefs remoteconfig connreject"
printf '%s' "$(for f in $omit; do printf 'ts_omit_%s,' "$f"; done)" | sed 's/,$//'
