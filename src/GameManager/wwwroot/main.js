(function() {
    document.addEventListener("submit", onSubmit);
    document.querySelectorAll(".Starting").forEach(pollForUpdate);
    document.querySelectorAll(".Fetching").forEach(el => getStatus(el).then(pollForUpdate));
    
    async function onSubmit(e) {
        e.preventDefault();
        const form = e.target;
        const button = form.querySelector("button");
        
        button.classList.add("is-loading");
        const response = await fetch(form.action, {
            method: "POST"
        });

        handleUpdate(response, form.closest(".card"))
            .then(pollForUpdate)
            .catch(error => {
                button.classList.remove("is-loading");
                alert(`Error: ${error}`)
            });
    }

    async function pollForUpdate(el) {
        while (true) {
            if (!el.classList.contains("Starting"))
                break;
            
            await delay(5000);
            el = await getStatus(el);
        }
    }
    
    async function getStatus(el) {
        const response = await fetch(`/servers/${el.dataset.name}`, {method: "GET"});
        return handleUpdate(response, el)
            .catch(error => setError(el, error));
    }
    
    function setError(el, error) {
        const msg = `Couldn't get server status: ${error}`
        console.error(msg)
        el.classList.remove("Starting", "Fetching");
        el.classList.add("Error");
        const tag = el.querySelector(".tag");
        tag.classList.remove("is-loading", "is-info");
        tag.classList.add("is-danger");
        tag.title = msg;
        tag.textContent = "Error";
    }
    
    function delay(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
    }
    
    function handleUpdate(response, container) {
        return new Promise(async (resolve, reject) => {
            if (response.ok) {
                const template = document.createElement("template");
                template.innerHTML = await response.text();
                const newContainer = template.content.firstElementChild;
                container.replaceWith(newContainer);
                
                resolve(newContainer);
            } else {
                const body = await response.text();
                const extra = body ? ` - ${body}` : "";

                reject(Error(`${response.status} - ${response.statusText}${extra}`));
            }
        });
    }
})();