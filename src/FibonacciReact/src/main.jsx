import React from 'react'
import ReactDOM from 'react-dom/client'
import Main from './App.jsx'
import PlanetSaver from './PlanetSaver.jsx'
import './index.css'

const newFetch = (url, options) => {
    const headers = new Headers(options?.headers || {});
    headers.set('Accept', 'application/json');
    headers.set('Content-Type', 'application/json');
    const newOptions = {
        ...options,
        headers,
    };
    return fetch(url, newOptions);
}

const searchParams = new URLSearchParams(window.location.search);
const isPlanetSaver = searchParams.get("planetsaver") === "true";

ReactDOM.createRoot(document.getElementById('root')).render(
    <React.StrictMode>
        {isPlanetSaver ? <PlanetSaver baseUrl="http://localhost:30021"
                     fetch={newFetch}
                     noActivityTimeout={10000}
                     interval={2000}
                     wakeUpTimeout={1000}
                     behavior={{
                         'fibonacci1': 'WakeUp+BlockUI',
                         'fibonacci2': 'WakeUp',
                         'fibonacci3': 'WakeUp',
                         'fibonacci4': 'None',
                         'mysql': 'WakeUp+BlockUI'
        }}>
            <Main url="http://localhost:30021"/>
        </PlanetSaver> : <Main url="http://localhost:30021" />}
    </React.StrictMode>,
)

