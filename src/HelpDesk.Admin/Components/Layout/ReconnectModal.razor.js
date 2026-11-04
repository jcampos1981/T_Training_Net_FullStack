// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

let lastAttemptChecked = 0;
let serverWasUp = false;

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        lastAttemptChecked = 0;
        serverWasUp = false;
        reconnectModal.close();
    } else if (event.detail.state === "retrying") {
        reloadIfTheCircuitIsGone(event.detail.currentAttempt);
    } else if (event.detail.state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

// Al desplegar una versión nueva se pierden el circuito y los tokens que estaban en memoria. Sin sesión, la reconexión termina en un
// redirect a Keycloak que la Content-Security-Policy bloquea, y para Blazor eso es un servidor inalcanzable: reintenta para siempre, y
// la pestaña queda atascada con el servidor ya sano.
//
// Si el servidor ya respondía antes del último intento, y ese intento falló igual, no va a reconectar: se recarga la página. No basta
// con que responda ahora: tras un corte de red, el intento siguiente puede recuperar el circuito, con lo que el usuario estaba escribiendo.
async function reloadIfTheCircuitIsGone(attempt) {
    if (attempt <= lastAttemptChecked) {
        return; // este evento también llega en cada segundo de la cuenta regresiva
    }
    lastAttemptChecked = attempt;

    if (serverWasUp) {
        location.reload();
        return;
    }

    try {
        const response = await fetch("/health/live", { cache: "no-store" });
        serverWasUp = response.ok;
    } catch {
        serverWasUp = false;
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                reconnectModal.close();
            }
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
