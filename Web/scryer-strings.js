(function () {
    'use strict';

    window.ScryerStrings = Object.freeze({
        pages: Object.freeze({
            discovery: 'Discover',
            calendar: 'Calendar',
            requests: 'Requests',
            downloads: 'Downloads'
        }),
        states: Object.freeze({
            notConfigured: 'Scryer is not configured.',
            notConnected: 'Connect your Scryer account to continue.',
            authorizationExpired: 'Your Scryer connection expired. Connect again.',
            permissionDenied: 'Your Scryer account does not permit this action.',
            offline: 'Scryer is currently unreachable.',
            incompatible: 'This Scryer server does not provide the required Alpha contract.',
            rateLimited: 'Scryer is rate-limiting this request. Try again shortly.',
            invalidResponse: 'Scryer returned an invalid response.',
            requestConflict: 'This request conflicts with its current Scryer state.',
            internalError: 'The Scryer request could not be completed.'
        })
    });
})();
