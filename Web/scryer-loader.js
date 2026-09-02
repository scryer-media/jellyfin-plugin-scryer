/*
 * RFC 153 browser entry point. This is deliberately the only injected runtime
 * tag: it owns ordered module loading, readiness checks, and reinjection safety.
 */
(function () {
    'use strict';

    var VERSION = '153.10';
    var runtime = window.ScryerRuntime153 = window.ScryerRuntime153 || {};
    if (runtime.version && runtime.version !== VERSION) {
        throw new Error('A different Scryer runtime version is already active.');
    }

    runtime.version = VERSION;
    runtime.modules = runtime.modules || {};
    runtime.registerModule = runtime.registerModule || function (name, version) {
        if (version !== VERSION) throw new Error('Unexpected Scryer module version for ' + name + '.');
        runtime.modules[name] = version;
    };

    function baseUrl() {
        var script = document.currentScript || document.querySelector('script[data-scryer-loader]');
        var src = script && script.src ? script.src : '/Scryer/Web/scryer-loader.js';
        return src.slice(0, src.lastIndexOf('/') + 1);
    }

    function hasModule(name) {
        return runtime.modules[name] === VERSION;
    }

    function assertReady(name, predicate) {
        if (!hasModule(name) || !predicate()) {
            throw new Error('Scryer module did not become ready: ' + name + '.');
        }
    }

    function loadScript(file, name, predicate) {
        if (hasModule(name)) {
            assertReady(name, predicate);
            return Promise.resolve();
        }

        return new Promise(function (resolve, reject) {
            var tag = document.createElement('script');
            tag.async = false;
            tag.src = baseUrl() + file + '?v=' + encodeURIComponent(VERSION);
            tag.onload = function () {
                try {
                    // Strings and styles are intentionally dependency-free legacy-compatible
                    // assets. Their concrete globals/DOM marker are the readiness contract.
                    if ((name === 'strings' || name === 'styles') && predicate()) {
                        runtime.registerModule(name, VERSION);
                    }
                    assertReady(name, predicate);
                    resolve();
                } catch (error) {
                    reject(error);
                }
            };
            tag.onerror = function () { reject(new Error('Could not load Scryer module: ' + name + '.')); };
            document.head.appendChild(tag);
        });
    }

    function start() {
        if (runtime.startPromise) return runtime.startPromise;
        runtime.startPromise = loadScript('scryer-strings.js', 'strings', function () {
            return !!window.ScryerStrings;
        }).then(function () {
            return loadScript('scryer-core.js', 'core', function () {
                return !!(window.Scryer && window.Scryer.version === VERSION && window.Scryer.lifecycle);
            });
        }).then(function () {
            return loadScript('scryer-styles.js', 'styles', function () {
                return !!document.getElementById('scryer-style');
            });
        }).then(function () {
            return loadScript('scryer-discovery.js', 'discovery', function () {
                return window.Scryer.lifecycle.hasFeature('discovery');
            });
        }).then(function () {
            return loadScript('scryer-calendar.js', 'calendar', function () {
                return window.Scryer.lifecycle.hasFeature('calendar');
            });
        }).then(function () {
            return loadScript('scryer-requests.js', 'requests', function () {
                return window.Scryer.lifecycle.hasFeature('requests');
            });
        }).then(function () {
            return loadScript('scryer-downloads.js', 'download', function () {
                return window.Scryer.lifecycle.hasFeature('download');
            });
        }).then(function () {
            return window.Scryer.startRuntime();
        }).catch(function (error) {
            runtime.startPromise = null;
            runtime.lastError = error && error.message ? error.message : 'Scryer runtime startup failed.';
            console.error('[Scryer] Runtime startup failed:', runtime.lastError);
            throw error;
        });
        return runtime.startPromise;
    }

    runtime.start = start;
    runtime.assertReady = assertReady;
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { start().catch(function () {}); }, { once: true });
    } else {
        start().catch(function () {});
    }
})();
