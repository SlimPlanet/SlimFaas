import { useEffect } from 'react';
import { useRouter } from 'next/router';

export default function MermaidRenderer() {
    const router = useRouter();

    useEffect(() => {
        let disposed = false;

        const render = async () => {
            try {
                const mermaid = (await import('mermaid')).default;

                // "strict" ou "sandbox" selon ton niveau de parano (sandbox = iframe) :contentReference[oaicite:2]{index=2}
                mermaid.initialize({
                    startOnLoad: false,
                    securityLevel: 'strict',
                });

                // Render tous les .mermaid présents
                await mermaid.run({ querySelector: '.mermaid' });
            } catch (e) {
                // Ne pas casser le site si un diagramme est invalide
                console.warn('[Mermaid] render failed', e);
            }
        };

        // 1er rendu
        requestAnimationFrame(() => { if (!disposed) void render(); });

        // Re-rendu après navigation SPA
        const onDone = () => { if (!disposed) void render(); };
        router.events.on('routeChangeComplete', onDone);

        return () => {
            disposed = true;
            router.events.off('routeChangeComplete', onDone);
        };
    }, [router.events]);

    return null;
}
