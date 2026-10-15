// Map for the Family Map widget, built on Leaflet (vendored in lib/leaflet).
//
// The Blazor component creates one map per widget element, then calls update() on each new poll.
// The map is never rebuilt on an update, so it doesn't flicker or lose its tiles.
window.familyMap = (() => {
    // OpenStreetMap's standard tiles need no API key; a single family kiosk is well within their tile
    // usage policy (https://operations.osmfoundation.org/policies/tiles/), which requires this
    // attribution and a Referer, which browsers send. The tiles are darkened in CSS to suit the dashboard.
    const TileUrl = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';
    const Attribution = '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors';
    const maps = new WeakMap();

    function create(element) {
        if (!window.L || maps.has(element)) {
            return;
        }

        // A kiosk shows the map; it isn't something to pan around, so interaction is off.
        const map = L.map(element, {
            zoomControl: false,
            dragging: false,
            touchZoom: false,
            scrollWheelZoom: false,
            doubleClickZoom: false,
            boxZoom: false,
            keyboard: false,
            attributionControl: true
        });
        map.attributionControl.setPrefix(false);
        L.tileLayer(TileUrl, { attribution: Attribution, maxZoom: 19 }).addTo(map);
        map.setView([0, 0], 2);

        const state = { map, markers: new Map(), fitted: false };
        // The widget is sized by the dashboard grid, so keep Leaflet's idea of the size current.
        state.observer = new ResizeObserver(() => {
            map.invalidateSize();
            if (state.bounds) {
                fit(state);
            }
        });
        state.observer.observe(element);
        maps.set(element, state);
    }

    function fit(state) {
        const { map, bounds } = state;
        if (!bounds) {
            return;
        }
        if (bounds.isValid() && bounds.getSouthWest().equals(bounds.getNorthEast())) {
            map.setView(bounds.getCenter(), 15, { animate: false });
        } else {
            map.fitBounds(bounds, { padding: [40, 40], maxZoom: 15, animate: false });
        }
    }

    function markerIcon(member) {
        const html = `<div class="family-pin${member.home ? ' is-home' : ''}">`
            + `<span class="family-pin-initials"></span>`
            + `<span class="family-pin-name"></span></div>`;
        return L.divIcon({ html, className: 'family-pin-wrap', iconSize: [0, 0] });
    }

    function update(element, members) {
        const state = maps.get(element);
        if (!state) {
            return;
        }

        const seen = new Set();
        const points = [];
        for (const member of members) {
            if (typeof member.lat !== 'number' || typeof member.lon !== 'number') {
                continue;
            }
            seen.add(member.id);
            const latLng = [member.lat, member.lon];
            points.push(latLng);

            let marker = state.markers.get(member.id);
            if (!marker) {
                marker = L.marker(latLng, { icon: markerIcon(member), keyboard: false, interactive: false }).addTo(state.map);
                state.markers.set(member.id, marker);
            } else {
                marker.setLatLng(latLng);
                marker.setIcon(markerIcon(member));
            }

            // Names come from Home Assistant, so they go in as text, never markup.
            const root = marker.getElement();
            if (root) {
                root.querySelector('.family-pin-initials').textContent = member.initials;
                root.querySelector('.family-pin-name').textContent = member.place
                    ? `${member.name} · ${member.place}`
                    : member.name;
            }
        }

        for (const [id, marker] of state.markers) {
            if (!seen.has(id)) {
                marker.remove();
                state.markers.delete(id);
            }
        }

        state.bounds = points.length > 0 ? L.latLngBounds(points) : null;
        fit(state);
    }

    function dispose(element) {
        const state = maps.get(element);
        if (!state) {
            return;
        }
        state.observer.disconnect();
        state.map.remove();
        maps.delete(element);
    }

    return { create, update, dispose };
})();
