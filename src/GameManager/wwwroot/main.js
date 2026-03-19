(function() {
    hookEventSource();
    const sseUrl = document.getElementById('sse').getAttribute('mu-url');
    let suppressWarning = false;
    mu.init({ progress: false });
    window.addEventListener('beforeunload', () => {
        suppressWarning = true;
    });
    document.addEventListener('submit', (event) => {
        const btn = event.submitter;
        if (btn)
            btn.classList.add('is-loading');
    });
    document.addEventListener('mu:fetch-error', e => {
        if (e.detail.url === sseUrl && !suppressWarning)
            document.getElementById('sse-disconnected').classList.add('is-visible');
    });
    document.addEventListener('sse-opened', e => {
        if (e.detail.url === sseUrl)
            document.getElementById('sse-disconnected').classList.remove('is-visible');
    });
})();

// µJS doesn't provide a hook into onopen so we use the nuclear option of overriding the EventSource constructor
function hookEventSource() {
    const OriginalEventSource = window.EventSource;
    window.EventSource = function(url, configuration) {
        const instance = new OriginalEventSource(url, configuration);
        instance.onopen = () => {
            document.dispatchEvent(new CustomEvent('sse-opened', { detail: { url: url, source: instance } }));
        };

        return instance;
    };
    window.EventSource.prototype = OriginalEventSource.prototype;
}