#!/bin/sh
# Starts Flexarr as PUID:PGID (1654 unless set), as the *arr containers do. Started with --user instead, it runs as
# that user straight away and leaves /config as it is.
set -eu

if [ "$(id -u)" != 0 ]; then
    exec dotnet /app/Flexarr.dll "$@"
fi

uid=${PUID:-1654}
gid=${PGID:-1654}

# A folder Docker made for a bind mount belongs to root, and one from an earlier PUID to that user: hand it over, with
# everything in it. Only when its owner is wrong, so an ordinary start doesn't walk the whole folder.
if [ "$(stat -c %u:%g /config)" != "$uid:$gid" ]; then
    echo "Giving /config to $uid:$gid"
    chown -R "$uid:$gid" /config
fi

# The graphics devices' groups (passed in with --device /dev/dri), so VA-API and Quick Sync can open them.
groups=
for dev in /dev/dri/renderD* /dev/dri/card*; do
    [ -e "$dev" ] || continue
    g=$(stat -c %g "$dev")
    case ",$groups," in
        *",$g,"*) ;;
        *) groups="${groups:+$groups,}$g" ;;
    esac
done

# root's home isn't the new user's to write to.
export HOME=/tmp
if [ -n "$groups" ]; then
    exec setpriv --reuid="$uid" --regid="$gid" --groups="$groups" dotnet /app/Flexarr.dll "$@"
fi
exec setpriv --reuid="$uid" --regid="$gid" --clear-groups dotnet /app/Flexarr.dll "$@"
