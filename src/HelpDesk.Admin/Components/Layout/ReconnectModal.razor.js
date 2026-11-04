// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

// Cuánto tiempo seguido tiene que responder la página antes de recargar.
const stableMilliseconds = 3000;

let lastAttemptChecked = 0;
let servableSince = null;

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        lastAttemptChecked = 0;
        servableSince = null;
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
// Si la página se puede servir, y sigue así después de otro intento fallido, no va a reconectar: se recarga. No basta con que responda una
// vez, por dos razones:
// - Tras un corte de red, el intento siguiente puede recuperar el circuito, con lo que el usuario estaba escribiendo.
// - Un pod que se está yendo todavía responde en sus últimos milisegundos, y el proxy manda ahí a los clientes con afinidad. Recargar en ese
//   momento dejaría la pestaña en un 502, una página sin JavaScript para volver a intentarlo. Unos segundos después, ese pod ya no está.
async function reloadIfTheCircuitIsGone(attempt) {
    if (attempt <= lastAttemptChecked) {
        return; // este evento también llega en cada segundo de la cuenta regresiva
    }
    lastAttemptChecked = attempt;

    if (!await pageCanBeServed()) {
        servableSince = null;
        return;
    }

    servableSince ??= Date.now();
    if (Date.now() - servableSince >= stableMilliseconds) {
        location.reload();
    }
}

// Se pregunta por la misma página, y por el mismo camino que usará la recarga.
async function pageCanBeServed() {
    try {
        // manual: sin sesión, la página redirige a Keycloak. Eso también es que el servidor responde, y la CSP no deja seguir ese redirect.
        const response = await fetch(location.href, { cache: "no-store", redirect: "manual" });
        return response.ok || response.type === "opaqueredirect";
    } catch {
        return false;
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
