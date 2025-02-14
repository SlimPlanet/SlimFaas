// Initial state for /status-functions
let currentStatusFunctionsBody = [
    { NumberReady: 0, numberRequested: 0, PodType: "Deployment", Visibility: "Public", Name: "fibonacci1" },
    { NumberReady: 0, numberRequested: 0, PodType: "Deployment", Visibility: "Public", Name: "fibonacci2" },
    { NumberReady: 0, numberRequested: 1, PodType: "Deployment", Visibility: "Public", Name: "fibonacci3" },
    { NumberReady: 0, numberRequested: 2, PodType: "Deployment", Visibility: "Private", Name: "fibonacci4" }
];

// Alternate body for /status-functions
const alternateStatusFunctionsBody = [
    { NumberReady: 1, numberRequested: 1, PodType: "Deployment", Visibility: "Public", Name: "fibonacci1" },
    { NumberReady: 1, numberRequested: 1, PodType: "Deployment", Visibility: "Public", Name: "fibonacci2" },
    { NumberReady: 1, numberRequested: 1, PodType: "Deployment", Visibility: "Public", Name: "fibonacci3" },
    { NumberReady: 2, numberRequested: 2, PodType: "Deployment", Visibility: "Private", Name: "fibonacci4" }
];

// Function to toggle between the two bodies
let timeoutId;
function toggleStatusFunctionsBody() {
    currentStatusFunctionsBody =
        currentStatusFunctionsBody === alternateStatusFunctionsBody
            ? [
                { NumberReady: 0, numberRequested: 0, PodType: "Deployment", Visibility: "Public", Name: "fibonacci1" },
                { NumberReady: 0, numberRequested: 0, PodType: "Deployment", Visibility: "Public", Name: "fibonacci2" },
                { NumberReady: 0, numberRequested: 1, PodType: "Deployment", Visibility: "Public", Name: "fibonacci3" },
                { NumberReady: 0, numberRequested: 2, PodType: "Deployment", Visibility: "Private", Name: "fibonacci4" }
            ]
            : alternateStatusFunctionsBody;

}

// Event listener for visibility change
document.addEventListener("visibilitychange", () => {
    if (document.hidden) {
        // When the page is hidden, reset to the initial state and clear timeout
        currentStatusFunctionsBody = [
            { NumberReady: 0, numberRequested: 0, PodType: "Deployment", Visibility: "Public", Name: "fibonacci1" },
            { NumberReady: 0, numberRequested: 0, PodType: "Deployment", Visibility: "Public", Name: "fibonacci2" },
            { NumberReady: 0, numberRequested: 1, PodType: "Deployment", Visibility: "Public", Name: "fibonacci3" },
            { NumberReady: 0, numberRequested: 2, PodType: "Deployment", Visibility: "Private", Name: "fibonacci4" }
        ];
        clearTimeout(timeoutId);
    } else {
        // When the page becomes visible again, restart the toggle logic
        timeoutId = setTimeout(toggleStatusFunctionsBody, 8000);
    }
});

// Start the initial toggle logic
setTimeout(toggleStatusFunctionsBody, 8000);
// Mock fetch function
const mockFetch = (isError=true, errorProbability=20) => (url, options = {}) => {
    return new Promise((resolve, reject) => {
        if(isError) {
            // Lancer une exception aléatoirement 1 fois sur 20
            if (Math.floor(Math.random() * errorProbability) === 0) {
                reject(new Error("Exception aléatoire"));
                return;
            }
        }

        // Route: /status-functions
        if (url === "https://slimfaas/status-functions" && (!options.method || options.method === "GET")) {
            resolve({
                status: 200,
                ok: true,
                json: () => Promise.resolve(currentStatusFunctionsBody),
                text: () => Promise.resolve(JSON.stringify(currentStatusFunctionsBody)),
            });
        }
        // Routes: /wake-function/fibonacci1, /wake-function/fibonacci2, /wake-function/fibonacci3, /wake-function/fibonacci4
        else if (
            url.startsWith("https://slimfaas/wake-function") &&
            options.method === "POST"
        ) {
            resolve({
                status: 204,
                ok: true,
                text: () => Promise.resolve(""),
                json: () => Promise.resolve({}),
            });
        } else {
            // Fallback for unhandled routes
            resolve({
                status: 404,
                ok: false,
                text: () => Promise.resolve("Not Found"),
                json: () => Promise.reject(new Error("Not Found")),
            });
        }
    });
}
export default mockFetch;
