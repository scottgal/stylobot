(function() {
    'use strict';

    // Get configuration from script tag data attributes
    const currentScript = document.currentScript || document.querySelector('script[data-endpoint]');
    if (!currentScript) {
        console.debug('Client geo script: could not find configuration');
        return;
    }

    const endpoint = currentScript.dataset.endpoint || '/api/v1/client-geo';
    const sessionId = currentScript.dataset.sessionId;
    const highAccuracy = currentScript.dataset.highAccuracy === 'true';

    // Client geo collection for bot detection
    function collectGeoData() {
        const geoData = {
            sessionId: sessionId,
            timestamp: new Date().toISOString(),
            timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
            timezoneOffset: new Date().getTimezoneOffset(),
            locale: navigator.language || navigator.userLanguage,
            languages: navigator.languages ? Array.from(navigator.languages) : [],
            coords: null
        };

        // Try to get geolocation if available
        if ('geolocation' in navigator) {
            const options = {
                enableHighAccuracy: highAccuracy,
                timeout: 5000,
                maximumAge: 300000  // Cache for 5 minutes
            };

            navigator.geolocation.getCurrentPosition(
                function(position) {
                    geoData.coords = {
                        latitude: position.coords.latitude,
                        longitude: position.coords.longitude,
                        accuracy: position.coords.accuracy
                    };
                    sendGeoData(geoData);
                },
                function(error) {
                    // Failed to get location - still send timezone/locale data
                    geoData.geoError = error.code;
                    geoData.geoErrorMessage = error.message;
                    sendGeoData(geoData);
                },
                options
            );
        } else {
            // No geolocation API - just send timezone/locale
            sendGeoData(geoData);
        }
    }

    function sendGeoData(data) {
        // Use sendBeacon for reliability, fallback to fetch
        const payload = JSON.stringify(data);

        if (navigator.sendBeacon) {
            const blob = new Blob([payload], { type: 'application/json' });
            navigator.sendBeacon(endpoint, blob);
        } else {
            fetch(endpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: payload,
                keepalive: true
            }).catch(function(err) {
                console.debug('Client geo collection failed:', err);
            });
        }
    }

    // Collect on page load
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', collectGeoData);
    } else {
        collectGeoData();
    }
})();
