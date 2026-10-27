"""Keeps Flexarr's device list current and turns its playback events into Home Assistant events."""

from __future__ import annotations

import asyncio
import logging
from typing import Any

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryAuthFailed
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from .api import FlexarrAuthError, FlexarrClient, FlexarrError
from .const import DOMAIN, EVENT_PLAYBACK, RECONNECT_DELAYS, SCAN_INTERVAL

_LOGGER = logging.getLogger(__name__)

type FlexarrConfigEntry = ConfigEntry[FlexarrCoordinator]


class FlexarrCoordinator(DataUpdateCoordinator[dict[str, dict[str, Any]]]):
    """Flexarr's devices by id, refreshed on a timer and whenever something starts, pauses or stops."""

    config_entry: FlexarrConfigEntry

    def __init__(self, hass: HomeAssistant, entry: FlexarrConfigEntry, client: FlexarrClient) -> None:
        super().__init__(hass, _LOGGER, config_entry=entry, name=DOMAIN, update_interval=SCAN_INTERVAL)
        self.client = client

    async def _async_update_data(self) -> dict[str, dict[str, Any]]:
        try:
            devices = await self.client.devices()
        except FlexarrAuthError as err:
            raise ConfigEntryAuthFailed(str(err)) from err
        except FlexarrError as err:
            raise UpdateFailed(str(err)) from err
        return {device["id"]: device for device in devices}

    async def listen(self) -> None:
        """Follows the event stream for as long as the entry is loaded, reconnecting when it drops."""
        failures = 0
        while True:
            try:
                async for event in self.client.events():
                    failures = 0
                    self._fire(event)
                    await self.async_request_refresh()
            except FlexarrAuthError:
                _LOGGER.error("Flexarr refused the API key for its event stream; reconfigure the integration")
                self.config_entry.async_start_reauth(self.hass)
                return
            except FlexarrError as err:
                delay = RECONNECT_DELAYS[min(failures, len(RECONNECT_DELAYS) - 1)]
                (_LOGGER.debug if failures else _LOGGER.info)("%s; reconnecting in %s s", err, delay)
                failures += 1
                await asyncio.sleep(delay)

    def _fire(self, event: dict[str, Any]) -> None:
        item = event.get("item") or {}
        flexarr_device = event.get("deviceId")
        device = dr.async_get(self.hass).async_get_device(identifiers={(DOMAIN, flexarr_device)}) if flexarr_device else None
        self.hass.bus.async_fire(
            EVENT_PLAYBACK,
            {
                "type": str(event["kind"]).lower(),
                # The Home Assistant device, for device triggers; None for a browser, which isn't one.
                "device_id": device.id if device else None,
                "flexarr_device_id": flexarr_device,
                "device_name": event.get("deviceName"),
                "profile": event.get("profileName"),
                "session_id": event.get("sessionId"),
                "media_id": item.get("id"),
                "media_type": media_type(item.get("kind")),
                "title": item.get("title"),
                "series_title": item.get("seriesTitle"),
                "season": item.get("seasonNumber"),
                "episode": item.get("episodeNumber"),
                "position": event.get("positionSeconds"),
                "duration": event.get("durationSeconds"),
            },
        )


def media_type(kind: str | None) -> str | None:
    """Flexarr's kind of title in Home Assistant's media terms."""
    return {
        "Movie": "movie",
        "Episode": "episode",
        "Series": "tvshow",
        "Season": "season",
        "Track": "music",
        "Album": "album",
        "Podcast": "podcast",
        "PodcastEpisode": "podcast",
    }.get(kind or "")
