import '../src/styles/main.scss';
import type { Preview } from '@storybook/react';
const preview: Preview = { parameters: { backgrounds: { default: 'light', values: [{ name: 'light', value: '#f7f9fc' }] } } };
export default preview;
