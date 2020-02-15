(function() {
    document.addEventListener("submit", onSubmit);
    
    async function onSubmit(e) {
        e.preventDefault();
        const form = e.target;
        const button = form.querySelector("button");
        
        button.classList.add("is-loading");
        const response = await fetch(form.action, {
            method: "POST"
        });
        
        if (response.ok) {
            const container = form.closest(".card");
            const html = await response.text();
            const fragment = document.createRange().createContextualFragment(html);
            container.replaceWith(fragment);
        } else {
            const body = await response.text();
            const extra = body ? ` - ${body}` : "";

            button.classList.remove("is-loading");
            alert(`Error: ${response.status} - ${response.statusText}${extra}`)
        }
    }
})();