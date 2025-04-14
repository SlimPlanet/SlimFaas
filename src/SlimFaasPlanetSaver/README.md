# @axa-fr/slimfaas-planet-saver

[![npm version](https://badge.fury.io/js/%40axa-fr%2Fslimfaas-planet-saver.svg)](https://badge.fury.io/js/%40axa-fr%2Fslimfaas-planet-saver)

![SlimFaas.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/SlimFaas.png)

A Vanilla JS project to save the planet. SlimFaas (https://github.com/SlimPlanet/slimfaas) is the slimmest and simplest Function As A Service on Kubernetes.
It works as a proxy that you can be deployed in your namespace.

SlimFaas API can give to the frontend information about the infrastructure state. **It is a mind changer !**

**Why?**

Because in production instead of setting up 2 replicas of your API backend, you can set up 0 replicas and use an UX that will show the user that the backend is down instead !
**@axa-fr/slimfaas-planet-saver** is here to for doing that easy.

![SlimFaasPlanetSaver.gif](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/SlimfaasPlanetSaver.gif)

## Getting Started

```javascript
npm install @axa-fr/slimfaas-planet-saver
```

Example usage with react :
```javascript
import React, { useState, useEffect, useRef } from 'react';
import { SlimFaasPlanetSaver } from '@axa-fr/slimfaas-planet-saver';

const PlanetSaver = ({ children, baseUrl, fetch, noActivityTimeout=60000, behavior={} }) => {
    const [isFirstStart, setIsFirstStart] = useState(true);
    const environmentStarterRef = useRef(null);

    useEffect(() => {
        if (!baseUrl) return;

        if (environmentStarterRef.current) return;

        const instance = new SlimFaasPlanetSaver(baseUrl, {
            interval: 2000,
            fetch,
            behavior,
            updateCallback: (data) => {
                // Filter only the items that block the UI (WakeUp+BockUI)
                const blockingItems = data.filter(
                    (item) => instance.getBehavior(item.Name) === 'WakeUp+BockUI'
                );

                // If all blocking items are ready, set isFirstStart to false
                const allBlockingReady = blockingItems.every(
                    (item) => item.NumberReady >= 1
                );
                if (allBlockingReady && isFirstStart) {
                    setIsFirstStart(false);
                }
            },
            errorCallback: (error) => {
                console.error('Error detected :', error);
            },
            overlayStartingMessage: '🌳 Starting the environment.... 🌳',
            overlayNoActivityMessage: 'Waiting activity to start environment...',
            overlayErrorMessage: 'An error occurred when starting environment. Please contact an administrator.',
            overlaySecondaryMessage: 'Startup should be fast, but if no machines are available it can take several minutes.',
            overlayLoadingIcon: '🌍',
            overlayErrorSecondaryMessage: 'If the error persists, please contact an administrator.',
            noActivityTimeout
        });

        environmentStarterRef.current = instance;

        // Initialiser les effets de bord
        instance.initialize();
        instance.startPolling();

        return () => {
            instance.cleanup();
            environmentStarterRef.current = null;
        };
    }, [baseUrl]);

    if (isFirstStart) {
        return null;
    }

    return <>{children}</>;
};

export default PlanetSaver;



```

---

## Run the demo

```javascript
git clone https://github.com/SlimPlanet/slimfaas.git
cd slimfaas/src/SlimFaasPlanetSaver
npm i
npm run dev
```
This will launch a local dev server, letting you see `SlimFaasPlanetSaver` in action.

---

## Configuration Options
When you create a `new SlimFaasPlanetSaver(baseUrl, options)`, you can provide the following optional properties in the `options` object:

| Property                  | Type                                           | Default                                   | Description                                                                                                                                                                                               |
|---------------------------|------------------------------------------------|-------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| updateCallback            | `(data: any[]) => void`                       | `() => {}`                                | Function called after a successful fetch of the functions’ status. The array data includes objects with info about each function, for example: `[{ Name: 'myFunc', NumberReady: 1 }, ...]`.               |
| errorCallback             | `(errorMessage: string) => void`              | `() => {}`                                | Function called if an error occurs during the status fetch (e.g., network error). Receives an errorMessage string.                                                                                        |
| interval                  | `number`                                       | `5000`                                    | How frequently (in ms) the polling should run.                                                                                                                                                            |
| overlayStartingMessage    | `string`                                       | `"🌳 Starting the environment.... 🌳"`    | Main message shown on the overlay when the environment is waking up.                                                                                                                                      |
| overlayNoActivityMessage  | `string`                                       | `"Waiting activity to start environment..."` | Message shown if there is no user activity (mouse movement) for too long, but the environment is not ready yet.                                                                                           |
| overlayErrorMessage       | `string`                                       | `"An error occurred while starting the environment."` | Main message shown on the overlay if an error occurs (e.g., network error).                                                                                                                                |
| overlaySecondaryMessage   | `string`                                       | `"Startup should be fast, but if no machines are available it can take several minutes."` | Secondary message shown on the overlay when the environment is waking up.                                                                                      |
| overlayErrorSecondaryMessage | `string`                                   | `"If the error persists, please contact an administrator."`                        | Secondary message shown on the overlay when an error occurs.                                                                                                                                              |
| overlayLoadingIcon        | `string`                                       | `"🌍"`                                    | Text or icon shown on the overlay. By default, it is animated to spin.                                                                                                                                     |
| noActivityTimeout         | `number`                                       | `60000`                                   | How long (in ms) to wait for mouse movement before concluding there is no activity. If no activity is detected, a different overlay message is displayed.                                                                                        |
| wakeUpTimeout             | `number`                                       | `60000`                                   | If a function was recently “woken up,” we’ll skip re-calling wake-up for that function within this timeout window (in ms).                                                                                 |
| fetch                     | `typeof fetch`                                 | Global fetch                              | Custom fetch function if you want to provide your own (e.g., for SSR, or if your environment doesn't have a global fetch).                                                                                 |
| behavior                  | `{ [functionName: string]: 'WakeUp+BockUI' \| 'WakeUp' \| 'None' }` | *Not set; defaults each function to "WakeUp+BockUI" if unspecified* | Allows you to override how each function is handled: 1. `"WakeUp+BockUI"`: wakes the function and blocks the UI with the overlay until it’s ready. 2. `"WakeUp"`: wakes without blocking the UI. 3. `"None"`: no wake-up call. |

### Notes on Behavior
If a function is **not** specified in the behavior map, it defaults to `"WakeUp+BockUI"`.

- `"WakeUp+BockUI"` means the overlay will be shown until that function is `NumberReady >= 1`.
- `"WakeUp"` means we attempt to wake up the function, but do not keep the overlay shown specifically for that function.
- `"None"` means the function will neither be woken up nor block the UI.

### Lifecycle

1. **Initialize**
Call `instance.initialize()` to create the overlay elements, inject styles, and bind event listeners (e.g., for mouse movement).

2. **Start Polling**
Call `instance.startPolling()` to begin the periodic checks of the environment. If the environment is not ready, the overlay will appear.

3. **Stop Polling / Cleanup**
When your component unmounts or you no longer need to monitor the environment, call `instance.cleanup()`. This removes the overlay, styles, and any timers or event listeners.
