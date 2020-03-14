(function() {
    let statusInterval = null;
    
    document.addEventListener("submit", onSubmit);
    watchForStatusUpdates();
    
    async function onSubmit(e) {
        e.preventDefault();
        const form = e.target;
        const button = form.querySelector("button");
        
        button.classList.add("is-loading");
        const response = await fetch(form.action, {
            method: "POST"
        });

        handleUpdate(response, form.closest(".card"))
            .then(() => { watchForStatusUpdates(); })
            .catch(error => {
                button.classList.remove("is-loading");
                alert(`Error: ${error}`)
            });
    }
    
    function watchForStatusUpdates() {
        if (statusInterval !== null) return;
        
        statusInterval = setInterval(() => {
            const startingContainers = document.querySelectorAll(".Starting");
            if (startingContainers.length === 0) {
                clearInterval(statusInterval);
                statusInterval = null;
            }
            else {
                startingContainers.forEach(async e => {
                    const response = await fetch(`/containers/${e.dataset.name}`, {method: "GET"});
                    handleUpdate(response, e)
                        .catch(error => {
                            console.log(`Error refreshing container status: ${error}`)
                        });
                });
            }
        }, 5000)
    }
    
    function handleUpdate(response, container) {
        return new Promise(async (resolve, reject) => {
            if (response.ok) {
                const html = await response.text();
                const fragment = document.createRange().createContextualFragment(html);
                container.replaceWith(fragment);
                
                resolve();
            } else {
                const body = await response.text();
                const extra = body ? ` - ${body}` : "";

                reject(Error(`${response.status} - ${response.statusText}${extra}`));
            }
        });
    }
})();