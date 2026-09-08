import { useEffect } from 'react';
import { useRouter } from 'next/router';

let rendering = Promise.resolve();
let diagramId = 0;

export default function MermaidRenderer() {
    const { pathname } = useRouter();
    useEffect(() => {
        let disposed = false;
        const elements = Array.from(document.querySelectorAll<HTMLElement>('.doc-diagram[data-source]'));
        if (!elements.length) return;
        rendering = rendering.catch(() => {}).then(async () => {
            const mermaid = (await import('mermaid')).default;
            mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'neutral', suppressErrorRendering: true });
            for (const element of elements) {
                if (disposed || !element.isConnected) return;
                const source = element.dataset.source ?? '';
                try {
                    const { svg } = await mermaid.render(`diagram-${++diagramId}`, source);
                    if (!disposed && element.isConnected) element.innerHTML = svg;
                } catch {
                    if (!disposed && element.isConnected) {
                        element.textContent = `Diagram could not be rendered. Mermaid source:\n${source}`;
                        element.classList.add('doc-diagram--error');
                    }
                }
            }
        }).catch(() => {
            // The original text remains readable if the optional renderer fails to load.
        });
        return () => { disposed = true; };
    }, [pathname]);
    return null;
}
